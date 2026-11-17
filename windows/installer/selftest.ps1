<#
.SYNOPSIS
    Proves the package installs and the installed app starts. Run after
    package.ps1; CI runs it on every change.

.DESCRIPTION
    Unzips the package the way a person would, runs Install.ps1 without the
    driver step (a build machine has no tablet and nobody to answer the
    Windows prompt), starts the installed app, checks its window opens, and
    uninstalls it again.
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
$Zip  = Join-Path (Split-Path -Parent $Here) 'dist\AlphaPremierIdMaker-setup.zip'
$Work = Join-Path $env:TEMP "apg-id-maker-selftest-$PID"
Expand-Archive -Path $Zip -DestinationPath $Work -Force

$driver = Import-PowerShellDataFile (Join-Path $Work 'Driver.psd1')
$driverZip = Join-Path $Work "driver\$($driver.Zip)"
if (-not (Test-Path $driverZip)) { throw 'The package does not carry the pen tablet driver.' }
if ((Get-FileHash -Algorithm SHA256 $driverZip).Hash.ToLowerInvariant() -ne $driver.Sha256) { throw 'The driver in the package is not the pinned file.' }
"Driver in the package: $($driver.Zip), checksum matches."

& (Join-Path $Work 'Install.ps1') -SkipDriver -Quiet

$exe = Join-Path $env:LOCALAPPDATA 'Programs\Alpha Premier ID Maker\AlphaPremierIdMaker.exe'
if (-not (Test-Path $exe)) { throw "The installer did not put the app at $exe." }
if (-not (Test-Path (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Alpha Premier ID Maker.lnk'))) { throw 'No Start-menu shortcut was made.' }

# The app reads the shared register as it starts. A build machine has no
# business asking the company's sheet for anything, so this computer is
# pointed at an address nothing answers on, the same way a person would in
# Issued IDs -> Shared register address.
$data = Join-Path $env:LOCALAPPDATA 'Alpha Premier\ID Maker'
New-Item -ItemType Directory -Force -Path $data | Out-Null
Set-Content -Path (Join-Path $data 'register-url.json') -Value 'http://localhost:9/exec' -NoNewline -Encoding utf8

$app = Start-Process -FilePath $exe -PassThru
$title = ''
foreach ($i in 1..40) {
    Start-Sleep -Milliseconds 500
    $app.Refresh()
    if ($app.HasExited) { throw "The app closed by itself straight after starting (exit code $($app.ExitCode))." }
    if ($app.MainWindowTitle) { $title = $app.MainWindowTitle; break }
}
if ($title -ne 'Alpha Premier ID Maker') { $app | Stop-Process -Force; throw "The app's window did not open (title: '$title')." }
"The installed app started and showed its window: $title"
Start-Sleep -Seconds 3
$app.Refresh()
if ($app.HasExited) { throw 'The app closed by itself a few seconds after starting.' }
$err = Join-Path $env:LOCALAPPDATA 'Alpha Premier\ID Maker\last-error.txt'
if (Test-Path $err) { Get-Content $err; $app | Stop-Process -Force; throw 'The app reported an error while starting.' }
$app | Stop-Process -Force

& (Join-Path $env:LOCALAPPDATA 'Programs\Alpha Premier ID Maker\Uninstall.ps1') -RemoveData
'Installed, started and removed.'
