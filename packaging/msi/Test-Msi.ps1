# Installs, upgrades, downgrades and uninstalls the MSI silently and checks the result after each step.
# Used by the CI workflow on a throwaway runner. It replaces any Fisheye Dewarp install on the PC, so don't run it on a workstation.
#   .\Test-Msi.ps1 -Msi FisheyeDewarp-1.2.3.msi -UpgradeMsi FisheyeDewarp-1.2.4.msi
param(
    [Parameter(Mandatory)][string]$Msi,
    [Parameter(Mandatory)][string]$UpgradeMsi
)
$ErrorActionPreference = 'Stop'
$target = Join-Path $env:ProgramFiles 'Milestone\MIPPlugins\FisheyeDewarp'
$files = 'FisheyeDewarp.dll', 'FisheyeDewarp.pdb', 'plugin.def'
$logDir = Join-Path $PSScriptRoot 'test-logs'
New-Item -ItemType Directory -Path $logDir -Force | Out-Null

function Invoke-Msiexec([string]$Action, [string]$Path, [string]$Log) {
    $p = Start-Process msiexec.exe -ArgumentList $Action, "`"$((Resolve-Path $Path).Path)`"", '/qn', '/norestart', '/l*v', "`"$(Join-Path $logDir $Log)`"" -Wait -PassThru
    return $p.ExitCode
}

function Get-ArpEntries {
    Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' |
        Get-ItemProperty | Where-Object { $_.DisplayName -eq 'Fisheye Dewarp for XProtect Smart Client' }
}

function Assert-Installed([string]$Version) {
    foreach ($f in $files) {
        if (-not (Test-Path (Join-Path $target $f))) { throw "$f is missing from $target" }
    }
    $arp = @(Get-ArpEntries)
    if ($arp.Count -ne 1) { throw "Expected 1 entry in Add/Remove Programs, found $($arp.Count)" }
    if ($arp[0].DisplayVersion -ne $Version) { throw "Add/Remove Programs shows version $($arp[0].DisplayVersion), expected $Version" }
    Write-Host "  OK: files present, one Add/Remove Programs entry, version $Version"
}

function Get-MsiVersion([string]$Path) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $db = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @((Resolve-Path $Path).Path, 0))
    $view = $db.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $db, @("SELECT Value FROM Property WHERE Property='ProductVersion'"))
    $null = $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
    $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
    $version = $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, 1)
    $null = $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null)
    return $version
}

$version = Get-MsiVersion $Msi
$upgradeVersion = Get-MsiVersion $UpgradeMsi

Write-Host "Install $version"
$code = Invoke-Msiexec '/i' $Msi 'install.log'
if ($code -ne 0) { throw "Install failed with exit code $code (see install.log)" }
Assert-Installed $version

Write-Host "Install $version again (repair)"
$code = Invoke-Msiexec '/i' $Msi 'reinstall.log'
if ($code -ne 0) { throw "Reinstall failed with exit code $code (see reinstall.log)" }
Assert-Installed $version

Write-Host "Upgrade to $upgradeVersion"
$code = Invoke-Msiexec '/i' $UpgradeMsi 'upgrade.log'
if ($code -ne 0) { throw "Upgrade failed with exit code $code (see upgrade.log)" }
Assert-Installed $upgradeVersion

Write-Host "Downgrade to $version (must be refused)"
$code = Invoke-Msiexec '/i' $Msi 'downgrade.log'
if ($code -eq 0) { throw 'Downgrade was allowed; it should have been refused' }
Assert-Installed $upgradeVersion

Write-Host 'Uninstall'
$code = Invoke-Msiexec '/x' $UpgradeMsi 'uninstall.log'
if ($code -ne 0) { throw "Uninstall failed with exit code $code (see uninstall.log)" }
if (Test-Path $target) { throw "$target still exists after uninstall" }
if (@(Get-ArpEntries).Count -ne 0) { throw 'Add/Remove Programs entry still present after uninstall' }
Write-Host '  OK: folder and Add/Remove Programs entry removed'

Write-Host 'MSI test passed'
