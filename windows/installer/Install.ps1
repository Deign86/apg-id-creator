<#
.SYNOPSIS
    Installs Alpha Premier ID Maker for the person signed in, and the XP-Pen
    pen tablet driver if this computer does not have one.

.DESCRIPTION
    The app itself needs no administrator: it goes into the user's own
    Programs folder with a Start-menu shortcut.

    The pen tablet driver is different. A driver is installed for the whole
    computer, so Windows asks for an administrator once (the usual "Do you
    want to allow this app to make changes" prompt). If the driver is
    already there, that step is skipped and nothing is asked.

    The driver is XP-Pen's own installer. It is taken from the "driver"
    folder beside this script when the package carries it, and otherwise
    downloaded from XP-Pen's server; either way it is only run if it is,
    byte for byte, the file named in Driver.psd1.

.PARAMETER SkipDriver
    Install the app only.

.PARAMETER Quiet
    No questions and no "press Enter" at the end, for remote deployment
    (Action1 and the like). Run it as an administrator, or the driver step
    will stop at the Windows prompt nobody is there to answer.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Install.ps1
#>
[CmdletBinding()]
param(
    [switch]$SkipDriver,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$AppName   = 'Alpha Premier ID Maker'
$ExeName   = 'AlphaPremierIdMaker.exe'
$Here      = Split-Path -Parent $MyInvocation.MyCommand.Path
$Source    = Join-Path $Here 'app'
$Target    = Join-Path $env:LOCALAPPDATA "Programs\$AppName"
$Driver    = Import-PowerShellDataFile (Join-Path $Here 'Driver.psd1')

function Say($text) { Write-Host $text }

function Test-TabletDriver {
    # XP-Pen's installer registers itself for uninstalling like any other
    # program; a tablet driver from another maker counts too, since two
    # tablet drivers on one computer fight over the pen.
    $keys = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
            'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
    $found = Get-ItemProperty $keys -ErrorAction SilentlyContinue |
        Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -match 'XP-?Pen|Pentablet|Wacom Tablet|Huion' }
    return [bool]$found
}

function Get-DriverInstaller {
    $work = Join-Path $env:TEMP "apg-id-maker-driver-$($Driver.Version)"
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    $zip = Join-Path (Join-Path $Here 'driver') $Driver.Zip
    if (-not (Test-Path $zip)) {
        $zip = Join-Path $work $Driver.Zip
        if (-not (Test-Path $zip)) {
            Say "Downloading the $($Driver.Name) from xp-pen.com (about 30 MB)..."
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            Invoke-WebRequest -Uri $Driver.Url -OutFile $zip -UseBasicParsing
        }
    }
    $hash = (Get-FileHash -Algorithm SHA256 -Path $zip).Hash.ToLowerInvariant()
    if ($hash -ne $Driver.Sha256) {
        Remove-Item $zip -Force -ErrorAction SilentlyContinue
        throw "The driver file is not the one this installer was made for (its checksum is different), so it was not run. Download the driver from https://www.xp-pen.com/download/deco-fun.html and install it by hand."
    }
    Expand-Archive -Path $zip -DestinationPath $work -Force
    $exe = Join-Path $work $Driver.Exe
    if (-not (Test-Path $exe)) { throw "The driver package did not contain $($Driver.Exe)." }
    return $exe
}

function Install-TabletDriver {
    if (Test-TabletDriver) { Say 'A pen tablet driver is already installed. Leaving it as it is.'; return }
    $exe = Get-DriverInstaller
    Say "Installing the $($Driver.Name) $($Driver.Version). Windows will ask for permission."
    # XP-Pen's installer is built with Inno Setup; these are Inno Setup's switches for installing without questions.
    try {
        $run = Start-Process -FilePath $exe -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-' -Verb RunAs -Wait -PassThru
    } catch {
        Say 'The driver was not installed, because permission was not given. The app works without it (sign with the mouse, or use a scan); run this installer again to add the driver later.'
        return
    }
    if ($run.ExitCode -ne 0) {
        Say "The driver's installer stopped with code $($run.ExitCode). The app is installed; to add the driver, run $exe by hand."
        return
    }
    Say 'Driver installed. Unplug the tablet and plug it back in. If the pen does not respond, restart the computer once.'
}

if (-not (Test-Path (Join-Path $Source $ExeName))) {
    throw "The app's files are missing from $Source. Unzip the whole package before running the installer."
}

# the app is closed first, so its files can be replaced
Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($ExeName)) -ErrorAction SilentlyContinue | ForEach-Object {
    Say "Closing $AppName so it can be updated..."
    $null = $_.CloseMainWindow()
    if (-not $_.WaitForExit(8000)) { $_ | Stop-Process -Force }
}

Say "Installing $AppName to $Target"
if (Test-Path $Target) { Remove-Item $Target -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Target | Out-Null
Copy-Item -Path (Join-Path $Source '*') -Destination $Target -Recurse -Force
Copy-Item -Path (Join-Path $Here 'Uninstall.ps1') -Destination $Target -Force

# files that came from the internet are marked by Windows; clearing the mark stops it second-guessing every start
Get-ChildItem $Target -Recurse -File | Unblock-File -ErrorAction SilentlyContinue

$shell = New-Object -ComObject WScript.Shell
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$shortcut = $shell.CreateShortcut((Join-Path $startMenu "$AppName.lnk"))
$shortcut.TargetPath = Join-Path $Target $ExeName
$shortcut.WorkingDirectory = $Target
$shortcut.Description = 'Make employee ID cards'
$shortcut.Save()

if (-not $SkipDriver) {
    try { Install-TabletDriver }
    catch { Say "The pen tablet driver was not installed: $($_.Exception.Message)" }
}

Say ''
Say "$AppName is installed. Find it in the Start menu."
if (-not $Quiet) { Read-Host 'Press Enter to close' | Out-Null }
