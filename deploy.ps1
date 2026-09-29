# Build the plugin and copy it into Smart Client's plugin folder.
# Smart Client locks the DLL while running, so it must be closed first.
param([switch]$StartClient)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$target = 'C:\Program Files\Milestone\MIPPlugins\FisheyeDewarp'

if (Get-Process -Name Client -ErrorAction SilentlyContinue) {
    throw 'Smart Client is running. Close it, then deploy again.'
}
& 'C:\Program Files\dotnet\dotnet.exe' build "$root\src\FisheyeDewarp\FisheyeDewarp.csproj" -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$out = "$root\src\FisheyeDewarp\bin\Release\net48"
Copy-Item "$out\FisheyeDewarp.dll", "$out\FisheyeDewarp.pdb", "$out\plugin.def" -Destination $target -Force
Get-ChildItem $target | Format-Table Name, Length, LastWriteTime -AutoSize

if ($StartClient) { Start-Process 'C:\Program Files\Milestone\XProtect Smart Client\Client.exe' }
