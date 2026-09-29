# Installs (or removes) the Fisheye Dewarp plugin for XProtect Smart Client on this PC.
# Run from the extracted release folder in an administrator PowerShell:
#   powershell -ExecutionPolicy Bypass -File .\install.ps1
#   powershell -ExecutionPolicy Bypass -File .\install.ps1 -Uninstall
param([switch]$Uninstall)
$ErrorActionPreference = 'Stop'
$target = Join-Path $env:ProgramFiles 'Milestone\MIPPlugins\FisheyeDewarp'
$files = 'FisheyeDewarp.dll', 'FisheyeDewarp.pdb', 'plugin.def'

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw 'Run this script from an administrator PowerShell (right-click PowerShell > Run as administrator).' }

if (Get-Process -Name Client -ErrorAction SilentlyContinue) {
    throw 'XProtect Smart Client is running. Close it, then run this script again.'
}

if ($Uninstall) {
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    Write-Host "Fisheye Dewarp removed from $target"
    return
}

foreach ($f in $files) {
    if (-not (Test-Path (Join-Path $PSScriptRoot $f))) { throw "Missing $f next to install.ps1. Extract the whole zip first." }
}
New-Item -ItemType Directory -Path $target -Force | Out-Null
foreach ($f in $files) { Copy-Item (Join-Path $PSScriptRoot $f) -Destination $target -Force }
Get-ChildItem $target | Unblock-File

Write-Host "Fisheye Dewarp installed to $target"
Write-Host 'Start Smart Client, open a fisheye camera and look for the Dewarp button on the tile toolbar.'
