# Collects crash and install evidence for the Fisheye Dewarp plugin into one zip, for sending to support.
# Run on the affected workstation, ideally from an administrator PowerShell (more is readable as admin):
#   powershell -ExecutionPolicy Bypass -File .\collect-diagnostics.ps1
#   powershell -ExecutionPolicy Bypass -File .\collect-diagnostics.ps1 -Days 30 -IncludeDumps
# The script only reads. It never stops Smart Client or changes settings. Each section is collected on its own, so one
# failure (a locked file, a missing log) is recorded in collector.log and does not stop the rest.
param(
    # How far back to collect events, logs and crash reports.
    [int]$Days = 14,
    # Also copy application crash dumps (Client.exe*.dmp, WER heap dumps). These can be hundreds of MB.
    [switch]$IncludeDumps,
    # Folder for the zip. Defaults to the current user's desktop.
    [string]$OutputFolder = [Environment]::GetFolderPath('Desktop')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$since = (Get-Date).AddDays(-$Days)
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$name = "FisheyeDewarp-diagnostics-$env:COMPUTERNAME-$stamp"
$stage = Join-Path ([IO.Path]::GetTempPath()) $name
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$collectorLog = Join-Path $stage 'collector.log'

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
$pluginDir = Join-Path $env:ProgramFiles 'Milestone\MIPPlugins\FisheyeDewarp'
$smartClientData = Join-Path $env:ProgramData 'Milestone\XProtect Smart Client'

function Note([string]$message) {
    $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $message"
    Add-Content -Path $collectorLog -Value $line
    Write-Host $message
}

# Runs one collection step; a failure is noted and the next step still runs.
function Step([string]$title, [scriptblock]$body) {
    Write-Host "- $title"
    try { & $body } catch { Note "FAILED: $title :: $($_.Exception.Message)" }
}

# Reads a property that may be absent (registry values, uninstall entries) without tripping strict mode.
function Prop($object, [string]$name) {
    if ($null -eq $object) { return $null }
    $p = $object.PSObject.Properties[$name]
    if ($p) { $p.Value }
}

# Copies a file even when its owner holds it open for writing (Smart Client keeps its logs open).
function Copy-Shared([string]$source, [string]$destination) {
    New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
    try {
        Copy-Item -LiteralPath $source -Destination $destination -Force
    } catch {
        $in = [IO.File]::Open($source, 'Open', 'Read', 'ReadWrite, Delete')
        try {
            $out = [IO.File]::Create($destination)
            try { $in.CopyTo($out) } finally { $out.Dispose() }
        } finally { $in.Dispose() }
    }
}

# Copies files under $root that changed since $since, keeping their relative paths under $destination.
function Copy-Recent([string]$root, [string]$destination, [string[]]$include = @('*'), [long]$maxBytes = 200MB) {
    if (-not (Test-Path -LiteralPath $root)) { Note "Not present: $root"; return }
    $files = @(Get-ChildItem -LiteralPath $root -Recurse -File -Include $include -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -ge $since })
    if ($files.Count -eq 0) { Note "Nothing changed since $($since.ToString('yyyy-MM-dd')) in $root"; return }
    foreach ($f in $files) {
        if ($f.Length -gt $maxBytes) { Note "Skipped (too large, $([math]::Round($f.Length / 1MB)) MB): $($f.FullName)"; continue }
        $relative = $f.FullName.Substring($root.TrimEnd('\').Length).TrimStart('\')
        try { Copy-Shared $f.FullName (Join-Path $destination $relative) }
        catch { Note "Could not copy $($f.FullName) :: $($_.Exception.Message)" }
    }
    Note "Copied $($files.Count) file(s) from $root"
}

# Writes the matching events as readable text (newest first), and as .evtx for Event Viewer.
# Several filters are OR-ed, so each event ID can be tied to its own provider.
function Export-Events([string]$logName, [hashtable[]]$filters, [string]$fileBase) {
    # Get-WinEvent throws "The parameter is incorrect" for a provider that is not registered (nvlddmkm without an
    # NVIDIA driver), even with -ErrorAction SilentlyContinue, so those filters are dropped first.
    $filters = @($filters | Where-Object {
        -not $_.ContainsKey('ProviderName') -or (Get-WinEvent -ListProvider $_['ProviderName'] -ErrorAction SilentlyContinue)
    } | ForEach-Object {
        $f = $_.Clone()
        $f['LogName'] = $logName
        $f['StartTime'] = $since
        $f
    })
    $text = Join-Path $stage "events\$fileBase.txt"
    New-Item -ItemType Directory -Path (Split-Path $text) -Force | Out-Null
    if ($filters.Count -eq 0) {
        "Event provider not installed on this computer" | Set-Content $text -Encoding UTF8
        Note "$logName ${fileBase}: provider not installed"
        return
    }
    $events = @(Get-WinEvent -FilterHashtable $filters -ErrorAction SilentlyContinue)
    if ($events.Count -eq 0) {
        "No matching $logName events since $since" | Set-Content $text -Encoding UTF8
    } else {
        $events | ForEach-Object {
            "==== $($_.TimeCreated.ToString('yyyy-MM-dd HH:mm:ss')) | $($_.LevelDisplayName) | $($_.ProviderName) | Event $($_.Id)"
            $_.Message
            ''
        } | Set-Content $text -Encoding UTF8
    }
    Note "$logName ${fileBase}: $($events.Count) event(s)"
}

Write-Host "Collecting Fisheye Dewarp diagnostics for the last $Days day(s)..."
if (-not $admin) {
    Note 'Not running as administrator: kernel minidumps and other users'' plugin logs may be missing.'
}

Step 'System summary' {
    $os = Get-CimInstance Win32_OperatingSystem
    $cs = Get-CimInstance Win32_ComputerSystem
    $netRelease = Prop (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue) 'Release'
    $lines = @(
        "Collected:        $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz') by $env:USERDOMAIN\$env:USERNAME (admin: $admin)"
        "Window:           last $Days day(s), since $($since.ToString('yyyy-MM-dd HH:mm'))"
        "Computer:         $env:COMPUTERNAME ($($cs.Manufacturer) $($cs.Model))"
        "OS:               $($os.Caption) $($os.Version) build $($os.BuildNumber), $($os.OSArchitecture)"
        "Last boot:        $($os.LastBootUpTime)"
        "Memory:           $([math]::Round($cs.TotalPhysicalMemory / 1GB, 1)) GB"
        "Session:          $(if ($env:SESSIONNAME) { $env:SESSIONNAME } else { 'unknown' }) (RDP-* means Remote Desktop)"
        ".NET 4.x release: $netRelease (528040 or higher is 4.8)"
        "Media Foundation: $(Test-Path (Join-Path $env:SystemRoot 'System32\mfplat.dll'))"
    )
    Get-CimInstance Win32_Processor | ForEach-Object { $lines += "CPU:              $($_.Name.Trim()) ($($_.NumberOfLogicalProcessors) threads)" }
    Get-CimInstance Win32_VideoController | ForEach-Object {
        $lines += "GPU:              $($_.Name) | driver $($_.DriverVersion) dated $($_.DriverDate) | $($_.CurrentHorizontalResolution)x$($_.CurrentVerticalResolution)"
    }
    $lines | Set-Content (Join-Path $stage 'system.txt') -Encoding UTF8
}

Step 'Installed plugin and Smart Client versions' {
    $lines = @("Plugin folder: $pluginDir")
    if (Test-Path $pluginDir) {
        Get-ChildItem $pluginDir -File -Force | ForEach-Object {
            $blocked = [bool](Get-Item -LiteralPath $_.FullName -Stream Zone.Identifier -ErrorAction SilentlyContinue)
            $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLower()
            $lines += "  $($_.Name) | $($_.Length) bytes | modified $($_.LastWriteTime) | version $($_.VersionInfo.FileVersion) | blocked $blocked | sha256 $hash"
        }
    } else { $lines += '  (not installed)' }
    $lines += '', 'Other MIP plugins:'
    $mip = Split-Path $pluginDir
    if (Test-Path $mip) { Get-ChildItem $mip -Directory | ForEach-Object { $lines += "  $($_.Name)" } }
    $lines += '', 'Add/Remove Programs entries (Fisheye, Milestone, XProtect, Axis):'
    $keys = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
            'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
    Get-ItemProperty $keys -ErrorAction SilentlyContinue |
        Where-Object { (Prop $_ 'DisplayName') -match 'Fisheye|Milestone|XProtect|Axis' } |
        Sort-Object DisplayName |
        ForEach-Object { $lines += "  $($_.DisplayName) | $(Prop $_ 'DisplayVersion') | installed $(Prop $_ 'InstallDate')" }
    $client = Get-ChildItem (Join-Path $env:ProgramFiles 'Milestone') -Recurse -Filter Client.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    $lines += '', "Smart Client: $(if ($client) { "$($client.FullName) version $($client.VersionInfo.FileVersion)" } else { 'Client.exe not found' })"
    $running = @(Get-Process -Name Client -ErrorAction SilentlyContinue)
    $lines += "Smart Client running now: $($running.Count -gt 0)"
    $lines | Set-Content (Join-Path $stage 'installed.txt') -Encoding UTF8
}

Step 'Application crashes and hangs' {
    # 1000 Application Error, 1002 Application Hang, 1026 .NET Runtime unhandled exception. Event IDs are tied to their
    # providers because other software reuses them (for example .NET Runtime 1000 for trace output).
    Export-Events 'Application' @(
        @{ ProviderName = 'Application Error'; Id = 1000 },
        @{ ProviderName = 'Application Hang'; Id = 1002 },
        @{ ProviderName = '.NET Runtime'; Id = 1026 }) 'application-crashes'
    # WER 1001 reports (APPCRASH, BlueScreen, LiveKernelEvent) are kept apart: WER re-logs queued reports on every
    # retry, which can add thousands of repeats that would bury the crashes above.
    Export-Events 'Application' @{ ProviderName = 'Windows Error Reporting'; Id = 1001 } 'wer-reports'
}

Step 'Installer events' {
    # 1033/1034 product installed/removed, 11707/11708 install succeeded/failed, 11724 removal.
    Export-Events 'Application' @{ ProviderName = 'MsiInstaller' } 'msi-installer'
}

Step 'System crashes, reboots and GPU resets' {
    # 41 Kernel-Power unexpected reboot, 1001 BugCheck (bluescreen), 6008 unexpected shutdown,
    # 4101 display driver stopped responding and recovered (TDR).
    Export-Events 'System' @{ Id = 41, 1001, 6008, 4101 } 'system-crashes'
    # NVIDIA driver errors (nvlddmkm) are logged under their own provider.
    Export-Events 'System' @{ ProviderName = 'nvlddmkm'; Level = 1, 2, 3 } 'nvidia-driver'
}

Step 'Full Application and System logs (.evtx)' {
    $ms = [long]((Get-Date) - $since).TotalMilliseconds
    $query = "*[System[TimeCreated[timediff(@SystemTime) <= $ms]]]"
    foreach ($log in 'Application', 'System') {
        & wevtutil.exe epl $log (Join-Path $stage "events\$log.evtx") "/q:$query" /ow:true
        if ($LASTEXITCODE) { Note "wevtutil could not export $log (exit $LASTEXITCODE)" }
    }
}

Step 'Fisheye Dewarp plugin logs (all user profiles)' {
    # Each Windows user has their own %LOCALAPPDATA%\FisheyeDewarp (dewarp.log plus any flag files).
    $profiles = @(Get-ChildItem (Split-Path $env:USERPROFILE) -Directory -Force -ErrorAction SilentlyContinue)
    $found = 0
    foreach ($p in $profiles) {
        $dir = Join-Path $p.FullName 'AppData\Local\FisheyeDewarp'
        if (-not (Test-Path $dir)) { continue }
        $found++
        foreach ($f in Get-ChildItem $dir -File -Force -ErrorAction SilentlyContinue) {
            try { Copy-Shared $f.FullName (Join-Path $stage "plugin-log\$($p.Name)\$($f.Name)") }
            catch { Note "Could not copy $($f.FullName) :: $($_.Exception.Message)" }
        }
    }
    Note "Plugin log folders found: $found"
}

Step 'Smart Client logs' {
    Copy-Recent $smartClientData (Join-Path $stage 'smart-client-logs') @('*.txt', '*.log', '*.xml')
}

Step 'Windows Error Reporting reports' {
    # Report.wer text names the crashing module, exception code and offset. Dumps are only copied with -IncludeDumps.
    $werRoots = @(Join-Path $env:ProgramData 'Microsoft\Windows\WER')
    $werRoots += Get-ChildItem (Split-Path $env:USERPROFILE) -Directory -Force -ErrorAction SilentlyContinue |
        ForEach-Object { Join-Path $_.FullName 'AppData\Local\Microsoft\Windows\WER' }
    $include = @('*.wer', '*.txt', '*.xml', '*.csv')
    if ($IncludeDumps) { $include += '*.dmp', '*.mdmp', '*.hdmp' }
    $i = 0
    foreach ($root in $werRoots | Where-Object { Test-Path $_ }) {
        $i++
        foreach ($sub in 'ReportArchive', 'ReportQueue') {
            $dir = Join-Path $root $sub
            if (Test-Path $dir) { Copy-Recent $dir (Join-Path $stage "wer\$i-$sub") $include 2GB }
        }
    }
}

Step 'Crash dumps' {
    $lines = @()
    $userDumps = Get-ChildItem (Split-Path $env:USERPROFILE) -Directory -Force -ErrorAction SilentlyContinue |
        ForEach-Object { Join-Path $_.FullName 'AppData\Local\CrashDumps' } | Where-Object { Test-Path $_ }
    foreach ($dir in @($userDumps) + (Join-Path $env:SystemRoot 'Minidump')) {
        if (-not (Test-Path $dir)) { continue }
        foreach ($d in Get-ChildItem $dir -File -Filter *.dmp -Force -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $since }) {
            $lines += "$($d.FullName) | $([math]::Round($d.Length / 1MB, 1)) MB | $($d.LastWriteTime)"
            # Kernel minidumps are small and are the main evidence for a bluescreen, so they are always copied.
            $kernel = $dir -like "$env:SystemRoot*"
            if ($kernel -or $IncludeDumps) {
                try { Copy-Shared $d.FullName (Join-Path $stage "dumps\$($d.Name)") }
                catch { Note "Could not copy $($d.FullName) :: $($_.Exception.Message)" }
            }
        }
    }
    $full = Join-Path $env:SystemRoot 'MEMORY.DMP'
    if (Test-Path $full) { $m = Get-Item $full; $lines += "$full | $([math]::Round($m.Length / 1MB)) MB | $($m.LastWriteTime) (not copied; ask if needed)" }
    if (-not $lines) { $lines = @("No crash dumps since $since") }
    elseif (-not $IncludeDumps) { $lines += '', 'Application dumps were listed but not copied. Re-run with -IncludeDumps to include them.' }
    $lines | Set-Content (Join-Path $stage 'dumps.txt') -Encoding UTF8
}

Step 'MSI install logs' {
    # These exist only if the install was run with /l*v or Windows Installer logging policy is on.
    foreach ($dir in $env:TEMP, (Join-Path $env:SystemRoot 'Temp')) {
        Get-ChildItem $dir -File -Filter 'MSI*.log' -Force -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTime -ge $since } |
            # msiexec writes these as ANSI or UTF-16 depending on how logging was turned on; check both.
            Where-Object { (Select-String -LiteralPath $_.FullName -Pattern 'Fisheye' -Quiet -ErrorAction SilentlyContinue) -or
                           (Select-String -LiteralPath $_.FullName -Pattern 'Fisheye' -Quiet -Encoding Unicode -ErrorAction SilentlyContinue) } |
            ForEach-Object { Copy-Shared $_.FullName (Join-Path $stage "msi-logs\$($_.Name)") }
    }
}

New-Item -ItemType Directory -Path $OutputFolder -Force | Out-Null
$zip = Join-Path (Resolve-Path -LiteralPath $OutputFolder).ProviderPath "$name.zip"
# Entries are added one by one because ZipFile.CreateFromDirectory on .NET Framework writes '\' separators, which
# other unzip tools treat as part of the file name.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($f in Get-ChildItem -LiteralPath $stage -Recurse -File -Force) {
        $entry = $f.FullName.Substring($stage.TrimEnd('\').Length + 1).Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $archive.Dispose() }
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ''
Write-Host "Done. Send this file to support:" -ForegroundColor Green
Write-Host "  $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
