<#
.SYNOPSIS
    Builds the package HR is given: one zip holding the app, its installer,
    and the pen tablet driver.

.DESCRIPTION
    Publishes the app with .NET built in (so the office computers need
    nothing installed first), fetches XP-Pen's driver and checks it is the
    exact file named in Driver.psd1, and zips the lot to
    windows\dist\AlphaPremierIdMaker-setup.zip.

    The driver is XP-Pen's software, redistributed unchanged inside the
    package; it is not kept in this repository.

.PARAMETER NoDriver
    Leave the driver out. Install.ps1 then downloads it when it runs.
#>
[CmdletBinding()]
param([switch]$NoDriver)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Here    = Split-Path -Parent $MyInvocation.MyCommand.Path
$Windows = Split-Path -Parent $Here
$Dist    = Join-Path $Windows 'dist'
$Stage   = Join-Path $Dist 'AlphaPremierIdMaker-setup'
$Driver  = Import-PowerShellDataFile (Join-Path $Here 'Driver.psd1')

if (Test-Path $Dist) { Remove-Item $Dist -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Stage | Out-Null

dotnet publish (Join-Path $Windows 'IdMaker.App\IdMaker.App.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $Stage 'app')
if ($LASTEXITCODE -ne 0) { throw 'The app did not build.' }

foreach ($need in 'AlphaPremierIdMaker.exe', 'Assets\front-template.png', 'Assets\back.png', 'Fonts\Poppins-Bold.ttf', 'Models\modnet.onnx', 'onnxruntime.dll') {
    if (-not (Test-Path (Join-Path $Stage "app\$need"))) { throw "The published app is missing $need." }
}

Copy-Item (Join-Path $Here 'Install.ps1'), (Join-Path $Here 'Install.cmd'), (Join-Path $Here 'Uninstall.ps1'), (Join-Path $Here 'Driver.psd1') -Destination $Stage

if (-not $NoDriver) {
    $driverDir = Join-Path $Stage 'driver'
    New-Item -ItemType Directory -Force -Path $driverDir | Out-Null
    $zip = Join-Path $driverDir $Driver.Zip
    Write-Host "Fetching the $($Driver.Name) $($Driver.Version)..."
    Invoke-WebRequest -Uri $Driver.Url -OutFile $zip -UseBasicParsing
    $hash = (Get-FileHash -Algorithm SHA256 -Path $zip).Hash.ToLowerInvariant()
    if ($hash -ne $Driver.Sha256) { throw "The driver that was downloaded is not the file Driver.psd1 names (checksum $hash). Check XP-Pen's download page and update Driver.psd1." }
}

$out = Join-Path $Dist 'AlphaPremierIdMaker-setup.zip'
Compress-Archive -Path (Join-Path $Stage '*') -DestinationPath $out -Force
Write-Host "Packaged $out ($([math]::Round((Get-Item $out).Length / 1MB)) MB)"
