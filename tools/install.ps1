# Installs yolime for daily use in %LOCALAPPDATA%\Programs\Yolime, or upgrades or uninstalls it.
#
#   pwsh -File tools\install.ps1                      # publish a Release build and install it, then start it
#   powershell -ExecutionPolicy Bypass -File tools\install.ps1   # the same, from Windows PowerShell 5.1
#   tools\install.ps1 -From <publish folder>          # install an existing publish folder (no .NET SDK needed)
#   tools\install.ps1 -NoLaunch                       # don't start it afterwards
#   tools\install.ps1 -Force                          # if yolime is running, ask it to exit (then stop it)
#   tools\install.ps1 -StartWithWindows on|off        # also turn start-with-Windows on or off (default: leave it)
#   tools\install.ps1 -Uninstall [-Force]             # remove the program; your settings, history and keys stay
#   tools\install.ps1 -Uninstall -RemoveData          # ...and your settings, history, recordings and saved keys
#
# Exit codes: 0 done, 2 yolime is running (nothing changed; exit it or add -Force), 3 uninstalled but -RemoveData
# kept some data (the last line says what), 1 anything else. -ResultFile gets the outcome as a word as well.
# yolime-setup.exe (installer\yolime.iss) runs this script; -Move and -SummaryFile are for it.
# Never elevated: yolime must not run as admin, and an elevated shell may be another user's profile.
# Pure ASCII, and runs on Windows PowerShell 5.1 and PowerShell 7.
param(
    [string]$From,
    [switch]$Force,
    [switch]$NoLaunch,
    [switch]$Uninstall,
    [switch]$RemoveData,
    [ValidateSet('', 'on', 'off')][string]$StartWithWindows = '',
    # Move -From into place instead of copying it (setup's extracted copy, on the same volume).
    [switch]$Move,
    # Also write the closing summary here, as plain UTF-8 lines.
    [string]$SummaryFile,
    # Also write one outcome word here on every exit (ok, running, files-in-use, check-failed, swap-failed, data-kept,
    # error): yolime reads it after an in-app update.
    [string]$ResultFile,
    # Windows Sandbox's only account is an administrator, so the install test needs this. Never use it otherwise.
    [switch]$AllowElevated,
    # The Sandbox update tests install E2E builds; check-publish refuses them otherwise. Never use it otherwise.
    [switch]$AllowE2EPayload
)
$ErrorActionPreference = 'Stop'

function Write-Result([string]$Outcome) {
    if (-not $ResultFile) { return }
    # Silent on failure: printing here would become setup's "last line" and hide the real reason.
    try { [IO.File]::WriteAllText($ResultFile, $Outcome) } catch { }
}

# Set once the new copy is in place: from then on yolime is updated, whatever goes wrong with the shortcut or summary.
$script:Swapped = $false

# Every exit goes through here, so the result file always says how it ended.
function Finish([int]$Code, [string]$Outcome) {
    Write-Result $Outcome
    exit $Code
}

# Anything thrown that nothing caught: still say so before PowerShell ends the script with exit 1.
trap {
    Write-Result $(if ($script:Swapped) { 'ok' } else { 'error' })
    break
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $AllowElevated -and (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Run this from a normal (not "Run as administrator") PowerShell: yolime must not run elevated, and an'
    Write-Host 'elevated shell may install into another account.'
    Finish 1 'error'
}

# Known folders, not environment variables: the Programs folder can be redirected on its own.
$localAppData = [Environment]::GetFolderPath('LocalApplicationData')
$programs = Join-Path $localAppData 'Programs'
$dir = Join-Path $programs 'Yolime'
$newDir = "$dir.new"
$oldDir = "$dir.old"
$exe = Join-Path $dir 'Yolime.App.exe'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'yolime.lnk'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$approvedKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
$valueName = 'yolime'
$check = Join-Path $PSScriptRoot 'check-publish.ps1'

Add-Type -Namespace YolimeInstall -Name Native -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
'@

function Test-Under([string]$Path, [string]$Folder) {
    if (-not $Path) { return $false }
    try { $full = [IO.Path]::GetFullPath($Path) } catch { return $false }
    return $full.StartsWith(([IO.Path]::GetFullPath($Folder).TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)
}

# Running copies: "ours" (under the install folders, or a path we can't read) block an install; others don't.
function Get-Copies {
    $ours = @(); $others = @()
    $session = (Get-Process -Id $PID).SessionId
    foreach ($p in @(Get-Process -Name 'Yolime.App' -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session })) {
        $path = $null
        try { $path = $p.Path } catch { }
        if (-not $path -or (Test-Under $path $dir) -or (Test-Under $path $newDir) -or (Test-Under $path $oldDir)) { $ours += $p }
        else { $others += $p }
    }
    return @{ Ours = $ours; Others = $others }
}

# Asks a copy to exit the way the tray's Exit does (WM_COMMAND 3 to its "yolime-tray" window), so it restores the
# clipboard and removes its tray icon; stops it after 5 s if it hasn't gone.
function Stop-Copy($Process) {
    $asked = $false
    # The title too: PowerShell passes $null to a string parameter as "", which would match no window.
    $hwnd = [IntPtr]::Zero
    while (($hwnd = [YolimeInstall.Native]::FindWindowEx([IntPtr]::Zero, $hwnd, 'yolime-tray', 'yolime')) -ne [IntPtr]::Zero) {
        [uint32]$owner = 0
        [void][YolimeInstall.Native]::GetWindowThreadProcessId($hwnd, [ref]$owner)
        if ($owner -eq $Process.Id) { $asked = [YolimeInstall.Native]::PostMessage($hwnd, 0x0111, [IntPtr]3, [IntPtr]::Zero) }
    }
    if ($asked) { Write-Host "Asked yolime (pid $($Process.Id)) to exit." }
    else { Write-Host "Found no tray window for yolime (pid $($Process.Id)) to ask." }
    # Its exit gives the clipboard back first, which can take a few seconds.
    $clock = [Diagnostics.Stopwatch]::StartNew()
    # yolime's own exit can take ~9 s (a history prune, then handing the clipboard back).
    if ($asked -and $Process.WaitForExit(15000)) { Write-Host "It exited in $([int]$clock.Elapsed.TotalMilliseconds) ms."; return }
    # No tray window to ask can mean a copy already on its way out (it removes the window first): give it a moment
    # before stopping it, rather than cutting its clipboard hand-back short.
    if (-not $asked -and $Process.WaitForExit(5000)) { Write-Host "It exited on its own in $([int]$clock.Elapsed.TotalMilliseconds) ms."; return }
    if (-not $Process.HasExited) {
        Write-Host "yolime (pid $($Process.Id)) didn't exit; stopping it."
        Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
        [void]$Process.WaitForExit(5000)
    }
}

function Stop-OrRefuse {
    $copies = Get-Copies
    if ($copies.Ours.Count -eq 0) { return }
    if (-not $Force) {
        foreach ($p in $copies.Ours) {
            $path = '(path unreadable)'
            try { if ($p.Path) { $path = $p.Path } } catch { }
            Write-Host "yolime is running: $path"
        }
        Write-Host 'Exit it from the tray (right-click the yolime icon > Exit) and run this again, or add -Force.'
        Finish 2 'running'
    }
    foreach ($p in $copies.Ours) { Stop-Copy $p }
    if ((Get-Copies).Ours.Count -gt 0) {
        Write-Host 'yolime is still running; nothing was changed. If you started it as administrator, exit it from its tray.'
        Finish 1 'running'
    }
}

# Defender scanning new files, or an Explorer window, can hold a folder for a moment.
function Invoke-Retry([scriptblock]$Action) {
    for ($i = 1; ; $i++) {
        try { & $Action; return } catch { if ($i -ge 5) { throw }; Start-Sleep -Milliseconds (300 * $i) }
    }
}

function Test-Reparse([string]$Path) {
    return (Test-Path -LiteralPath $Path) -and (((Get-Item -LiteralPath $Path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
}

function Get-RunTarget {
    $value = (Get-ItemProperty -Path $runKey -Name $valueName -ErrorAction SilentlyContinue).$valueName
    if (-not $value) { return $null }
    $value = $value.Trim()
    if ($value.StartsWith('"')) {
        $end = $value.IndexOf('"', 1)
        if ($end -gt 0) { return $value.Substring(1, $end - 1) }
        return $value.Trim('"')
    }
    return $value
}

function Get-SmartAppControlNote {
    $state = (Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy' -Name 'VerifiedAndReputablePolicyState' -ErrorAction SilentlyContinue).VerifiedAndReputablePolicyState
    if ($state -eq 1) { return 'Warning: Smart App Control is on. It may block yolime (an unsigned app with a keyboard hook); there is no per-app exception.' }
    if ($state -eq 2) { return 'Note: Smart App Control is in evaluation mode. Windows may turn it on later and block yolime (unsigned, with a keyboard hook); there is no exception.' }
    return $null
}

$asideDir = "$dir.uninstall"
foreach ($folder in $programs, $dir, $newDir, $oldDir, $asideDir, $shortcut) {
    if (Test-Reparse $folder) { Write-Host "$folder is a link or junction; refusing to touch it."; Finish 1 'error' }
}

# Deletes a folder tree, removing any link or junction inside without entering it. Remove-Item on 5.1 follows
# junctions, .NET Framework's recursive Directory.Delete gives up on one, and cmd's rmdir reparses the path (a user
# name with & or ^ in it).
function Remove-Tree([string]$Path) {
    foreach ($item in (New-Object IO.DirectoryInfo($Path)).GetFileSystemInfos()) {
        $isLink = ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
        $item.Attributes = $item.Attributes -band (-bnot [IO.FileAttributes]::ReadOnly)
        if ($item -is [IO.DirectoryInfo]) {
            # A junction goes as itself (its target is untouched); a real folder is emptied and removed by the call.
            if ($isLink) { [IO.Directory]::Delete($item.FullName, $false) } else { Remove-Tree $item.FullName }
        } else {
            [IO.File]::Delete($item.FullName)
        }
    }
    [IO.Directory]::Delete($Path, $false)
}

# Deletes yolime's data folders and saved keys, only when no copy of yolime runs (a dev copy uses the same ones).
# Returns $true when everything is gone; otherwise its last line says what was kept.
function Remove-Data {
    if (@(Get-Process -Name 'Yolime.App' -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq (Get-Process -Id $PID).SessionId }).Count -gt 0) {
        Write-Host 'Your data and saved keys were kept: another copy of yolime is running and uses them. Exit it, then run tools\install.ps1 -Uninstall -RemoveData, or use Settings > Advanced > Reset yolime in that copy.'
        return $false
    }
    $kept = @()
    foreach ($pair in @(@('ApplicationData', 'settings and prompts'), @('LocalApplicationData', 'history, recordings and logs'))) {
        $root = [Environment]::GetFolderPath($pair[0])
        # Like the app's own Reset: an unresolved known folder would make "Yolime" relative to this shell's folder.
        if (-not $root -or -not [IO.Path]::IsPathRooted($root)) { $kept += "$($pair[0]) (the folder couldn't be found)"; continue }
        $folder = Join-Path $root 'Yolime'
        if (-not (Test-Path -LiteralPath $folder)) { continue }
        if (Test-Reparse $folder) { Write-Host "$folder is a link or junction; refusing to delete through it."; $kept += $folder; continue }
        try { Remove-Tree $folder } catch { Write-Host "Couldn't delete ${folder}: $($_.Exception.Message)" }
        if (Test-Path -LiteralPath $folder) { $kept += $folder } else { Write-Host "Deleted $folder ($($pair[1]))." }
    }
    foreach ($k in 'OPENAI_API_KEY', 'GROQ_API_KEY', 'YOLIME_STT_API_KEY', 'YOLIME_ANTHROPIC_API_KEY') {
        cmdkey "/delete:yolime/$k" 2>&1 | Out-Null
        # /list echoes the target even when there's none; a stored credential has a Target: line.
        if ((cmdkey "/list:yolime/$k" | Out-String) -match "Target:.*yolime/$k") { $kept += "the saved key yolime/$k" }
    }
    if ($kept.Count -eq 0) { Write-Host 'Deleted the saved keys (Credential Manager, yolime/*).'; return $true }
    Write-Host ('Not deleted: ' + ($kept -join '; ') + '.')
    return $false
}

if ($Uninstall) {
    Stop-OrRefuse
    $removed = $false
    if (Test-Path -LiteralPath $dir) {
        # Renamed aside first: it proves nothing holds the folder before autostart and the shortcut go.
        if (Test-Path -LiteralPath $asideDir) { Invoke-Retry { Remove-Item -LiteralPath $asideDir -Recurse -Force } }
        try { Invoke-Retry { Rename-Item -LiteralPath $dir -NewName (Split-Path $asideDir -Leaf) } }
        catch { Write-Host "Couldn't remove $dir (something has it open): $($_.Exception.Message)"; Finish 1 'files-in-use' }
        $removed = $true
    }
    if (Test-Path -LiteralPath $shortcut) { Remove-Item -LiteralPath $shortcut -Force }
    if (Test-Under (Get-RunTarget) $dir) {
        Remove-ItemProperty -Path $runKey -Name $valueName -ErrorAction SilentlyContinue
        Remove-ItemProperty -Path $approvedKey -Name $valueName -ErrorAction SilentlyContinue
        Write-Host 'Start with Windows: removed.'
    }
    if ($removed) {
        try { Invoke-Retry { Remove-Item -LiteralPath $asideDir -Recurse -Force } }
        catch { Write-Host "Warning: $asideDir couldn't be deleted yet; delete it later." }
        foreach ($stray in $newDir, $oldDir) { if (Test-Path -LiteralPath $stray) { Remove-Item -LiteralPath $stray -Recurse -Force -ErrorAction SilentlyContinue } }
        Write-Host "yolime is uninstalled from $dir."
    } else {
        Write-Host "yolime wasn't installed in $dir."
    }
    # Only after the program is gone: a failed removal above exits first, so a re-run can finish both.
    # Exit 3: the program is gone, but some data was kept (the last line says what), so setup can say so.
    if ($RemoveData) { if (Remove-Data) { Finish 0 'ok' } else { Finish 3 'data-kept' } }
    $appData = [Environment]::GetFolderPath('ApplicationData')
    Write-Host ''
    Write-Host 'Your data is still here (Settings > Advanced > Reset yolime removes it, before uninstalling):'
    Write-Host "  $appData\Yolime            settings and prompts"
    Write-Host "  $localAppData\Yolime       history.db, pending\ (voice recordings kept up to 7 days for Retry), logs\"
    Write-Host '  Credential Manager         yolime/OPENAI_API_KEY, yolime/GROQ_API_KEY, yolime/YOLIME_STT_API_KEY, yolime/YOLIME_ANTHROPIC_API_KEY'
    Write-Host 'To remove it by hand:'
    Write-Host "  Remove-Item -Recurse -Force '$appData\Yolime', '$localAppData\Yolime'"
    Write-Host '  foreach ($k in "OPENAI_API_KEY", "GROQ_API_KEY", "YOLIME_STT_API_KEY", "YOLIME_ANTHROPIC_API_KEY") { cmdkey /delete:"yolime/$k" }'
    Write-Host 'Environment variables you set yourself are untouched.'
    Finish 0 'ok'
}

# Build.
if (-not $From) {
    $repo = Split-Path $PSScriptRoot -Parent
    $From = Join-Path $repo 'artifacts\publish'
    # Emptied first: publish doesn't delete stale files, and what's installed must be exactly this build.
    if (Test-Path -LiteralPath $From) { Remove-Item -LiteralPath $From -Recurse -Force }
    & dotnet publish (Join-Path $repo 'src\Yolime.App') -c Release -o $From
    if ($LASTEXITCODE -ne 0) { Write-Host 'dotnet publish failed.'; Finish 1 'error' }
}
$From = (Resolve-Path -LiteralPath $From).Path
$checkArgs = @{ Folder = $From }
if ($AllowE2EPayload) { $checkArgs.AllowE2EPayload = $true }
& $check @checkArgs
if ($LASTEXITCODE -ne 0) { Finish 1 'check-failed' }

Stop-OrRefuse

# An earlier run interrupted between its two renames leaves only Yolime.old: put it back first.
if (-not (Test-Path -LiteralPath $dir) -and (Test-Path -LiteralPath $oldDir)) { Invoke-Retry { Rename-Item -LiteralPath $oldDir -NewName 'Yolime' } }
foreach ($stale in $newDir, $oldDir, $asideDir) {
    if (Test-Path -LiteralPath $stale) {
        try { Invoke-Retry { Remove-Item -LiteralPath $stale -Recurse -Force } }
        catch { Write-Host "Couldn't clear $stale (something has it open); nothing was changed."; Finish 1 'files-in-use' }
    }
}

# Copy the build's contents into an empty Yolime.new (Copy-Item would nest a folder copied onto an existing one).
New-Item -ItemType Directory -Force -Path $programs | Out-Null
$moved = $false
if ($Move) {
    # A rename when -From is on the same volume (setup's {tmp} is), which saves copying ~280 MB a second time.
    try { Move-Item -LiteralPath $From -Destination $newDir; $moved = $true } catch { Write-Host "Couldn't move the build into place ($($_.Exception.Message)); copying it." }
}
if (-not $moved) {
    if (Test-Path -LiteralPath $newDir) { Invoke-Retry { Remove-Item -LiteralPath $newDir -Recurse -Force } }
    New-Item -ItemType Directory -Path $newDir | Out-Null
    Copy-Item -Path (Join-Path $From '*') -Destination $newDir -Recurse -Force
}
$checkArgs.Folder = $newDir
& $check @checkArgs
if ($LASTEXITCODE -ne 0) { Remove-Item -LiteralPath $newDir -Recurse -Force; Finish 1 'check-failed' }

$upgrade = Test-Path -LiteralPath $dir
if ($upgrade) {
    # Something holding a file in the install folder (Explorer, a scan, a stray copy) blocks this rename: the previous
    # install is untouched, so leave it as it is.
    try { Invoke-Retry { Rename-Item -LiteralPath $dir -NewName 'Yolime.old' } }
    catch {
        Write-Host "Couldn't move the previous install aside (something has a file in it open): $($_.Exception.Message). The previous install is unchanged."
        Remove-Item -LiteralPath $newDir -Recurse -Force -ErrorAction SilentlyContinue
        Finish 1 'swap-failed'
    }
}
try {
    Invoke-Retry { Rename-Item -LiteralPath $newDir -NewName 'Yolime' }
    $script:Swapped = $true
} catch {
    $why = $_.Exception.Message
    if ($upgrade) {
        try {
            Invoke-Retry { Rename-Item -LiteralPath $oldDir -NewName 'Yolime' }
            Write-Host "Couldn't put the new copy in place: $why. The previous install is unchanged."
        } catch {
            Write-Host "Couldn't put the new copy in place ($why), nor the previous one back. Run this again: it restores $oldDir first."
        }
    } else {
        Write-Host "Couldn't put the new copy in place: $why"
    }
    Finish 1 'swap-failed'
}
if ($upgrade) {
    try { Invoke-Retry { Remove-Item -LiteralPath $oldDir -Recurse -Force } }
    catch { Write-Host "Warning: $oldDir couldn't be deleted yet; the next install removes it." }
}

# Only now that the swap succeeded: the shortcut, and an existing start-with-Windows entry follows the install.
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $dir
$link.Description = 'yolime: hold a key, speak, and the text is typed for you'
$link.Save()
$startup = 'off (turn it on in Settings > General)'
if ($StartWithWindows -eq 'off') {
    Remove-ItemProperty -Path $runKey -Name $valueName -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $approvedKey -Name $valueName -ErrorAction SilentlyContinue
} elseif ($StartWithWindows -eq 'on' -or (Get-RunTarget)) {
    $previous = Get-RunTarget
    if (-not (Test-Path $runKey)) { New-Item -Path $runKey -Force | Out-Null }
    # Quoted, exactly as the app writes it (StartWithWindows.CommandFor), so Settings shows it as on.
    Set-ItemProperty -Path $runKey -Name $valueName -Value ('"' + $exe + '"')
    $approved = (Get-ItemProperty -Path $approvedKey -Name $valueName -ErrorAction SilentlyContinue).$valueName
    $disabled = $approved -and $approved.Length -gt 0 -and ($approved[0] -band 1) -eq 1
    if ($disabled -and $StartWithWindows -eq 'on') {
        # Asked for on: also undo Task Manager's "disabled", as the app's own switch does (StartupRegistry.Enable).
        Set-ItemProperty -Path $approvedKey -Name $valueName -Value ([byte[]](2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)) -Type Binary
        $disabled = $false
    }
    if ($disabled) { $startup = 'pointing at this install, but turned off in Task Manager > Startup apps' }
    else { $startup = 'on, pointing at this install' }
    if ($previous -and -not (Test-Under $previous $dir)) { $startup += " (was: $previous)" }
}

$running = 'no'
$others = (Get-Copies).Others
$otherNote = $null
if ($others.Count -gt 0) { $otherNote = "Note: another copy of yolime is running ($($others[0].Path)). It holds the single instance, so the installed copy can't start until you exit it." }
if ($NoLaunch) { $running = 'no (-NoLaunch)' }
elseif ($others.Count -gt 0) {
    $running = "no: another copy is running ($($others[0].Path)) and holds the single instance; exit it, then start yolime from the Start menu"
} else {
    $started = Start-Process -FilePath $exe -WorkingDirectory $dir -PassThru
    # Up to 15 s: a first start can be slow (Defender scans the new files). Still up after that means it started.
    [void]$started.WaitForExit(15000)
    $running = if (-not $started.HasExited) { 'yes (in the tray)' } else { 'no: it exited at start; see the logs in ' + (Join-Path $localAppData 'Yolime\logs') }
}

$summary = @(
    ($(if ($upgrade) { 'Upgraded' } else { 'Installed' }) + " yolime in $dir")
    "Start menu:         yolime"
    "Start with Windows: $startup"
    "Running:            $running"
)
if (-not $upgrade) {
    # One line: setup's finish page wraps it itself.
    $summary += 'First run: the welcome window walks through setup. Keys go in Settings > Keys, or in the environment variables OPENAI_API_KEY and YOLIME_ANTHROPIC_API_KEY (read when yolime starts).'
}
if ($otherNote) { $summary += $otherNote }
$sac = Get-SmartAppControlNote
if ($sac) { $summary += $sac }
Write-Host ''
foreach ($line in $summary) { Write-Host $line }
if ($SummaryFile) { [IO.File]::WriteAllLines($SummaryFile, [string[]]$summary, (New-Object Text.UTF8Encoding($false))) }
Finish 0 'ok'
