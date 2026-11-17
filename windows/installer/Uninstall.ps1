<#
.SYNOPSIS
    Removes Alpha Premier ID Maker from this user's account.

.DESCRIPTION
    Removes the program and its Start-menu shortcut. The list of issued IDs
    and any card design set up on this computer are kept unless -RemoveData
    is given; the shared register is never touched. The pen tablet driver is
    left alone: remove it from Windows Settings, Apps, if it is no longer
    wanted.
#>
[CmdletBinding()]
param([switch]$RemoveData)

$ErrorActionPreference = 'Stop'
$AppName = 'Alpha Premier ID Maker'
$Target  = Join-Path $env:LOCALAPPDATA "Programs\$AppName"
$Data    = Join-Path $env:LOCALAPPDATA 'Alpha Premier\ID Maker'

Get-Process -Name 'AlphaPremierIdMaker' -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$AppName.lnk") -Force -ErrorAction SilentlyContinue
if ($RemoveData -and (Test-Path $Data)) { Remove-Item $Data -Recurse -Force }

# this script lives in the folder it is removing, so the folder is removed after it exits
if (Test-Path $Target) {
    Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', "timeout /t 2 /nobreak >nul & rmdir /s /q `"$Target`"" -WindowStyle Hidden
}
Write-Host "$AppName has been removed."
