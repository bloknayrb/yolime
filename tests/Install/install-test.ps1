# Runs inside Windows Sandbox only (started by run-install-test.ps1): installs, upgrades and uninstalls yolime with
# tools\install.ps1 under Windows PowerShell 5.1, and checks each step. Results: C:\results\install.json, then DONE.
# Pure ASCII.
$ErrorActionPreference = 'Stop'
$results = 'C:\results'
$t = 'C:\t'
$report = New-Object System.Collections.ArrayList

function Add-Result([string]$Name, $Failure) {
    [void]$report.Add([ordered]@{ name = $Name; pass = ($null -eq $Failure); failure = $Failure })
}

$elevated = (New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$extra = @()
if ($elevated) { $extra = @('-AllowElevated') }

# Each install.ps1 run is its own process, so its exit code is real and an exit can't end this script.
function Invoke-Install([string[]]$Arguments, [switch]$Plain) {
    $all = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "$t\tools\install.ps1") + $Arguments
    if (-not $Plain) { $all += $extra }
    # Under 5.1 with Stop, a redirected stderr line would end this script: a crash in install.ps1 must be a result.
    $ErrorActionPreference = 'Continue'
    $out = & powershell.exe @all 2>&1 | Out-String
    $script:LastOutput = $out
    Add-Content -Path "$results\install-output.txt" -Value ("> install.ps1 " + ($Arguments -join ' ') + "`r`n" + $out)
    return $LASTEXITCODE
}

$local = [Environment]::GetFolderPath('LocalApplicationData')
$dir = Join-Path $local 'Programs\Yolime'
$exe = Join-Path $dir 'Yolime.App.exe'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'yolime.lnk'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

function Get-Running { @(Get-Process -Name 'Yolime.App' -ErrorAction SilentlyContinue) }
function Get-RunValue { (Get-ItemProperty -Path $runKey -Name 'yolime' -ErrorAction SilentlyContinue).yolime }
function Wait-Until([scriptblock]$Condition, [int]$Ms) {
    $end = [Environment]::TickCount + $Ms
    while ([Environment]::TickCount -lt $end) { if (& $Condition) { return $true }; Start-Sleep -Milliseconds 250 }
    return [bool](& $Condition)
}
function Stop-All { Get-Running | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }; Start-Sleep -Seconds 1 }

$approvedKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
function Get-ApprovedValue { (Get-ItemProperty -Path $approvedKey -Name 'yolime' -ErrorAction SilentlyContinue).yolime }

# A setup that shows a dialog would wait forever: each run gets a deadline, then is killed and fails its case.
function Wait-OrKill($Process, [string]$Name) {
    if ($Process.WaitForExit(300000)) { return $Process.ExitCode }
    $titles = @(Get-Process | Where-Object { $_.MainWindowTitle } | ForEach-Object { "$($_.ProcessName): $($_.MainWindowTitle)" }) -join '; '
    Add-Content -Path "$results\install-output.txt" -Value "> $Name timed out; windows: $titles"
    Get-Process | Where-Object { $_.ProcessName -like 'yolime-setup*' -or $_.ProcessName -like '_iu*' -or $_.ProcessName -like 'unins*' } | Stop-Process -Force -ErrorAction SilentlyContinue
    return -1
}

function Read-SetupLog([string]$Log) {
    $script:LastSetupLog = ''
    if (Test-Path $Log) { $script:LastSetupLog = Get-Content $Log -Raw }
}

function Invoke-Setup([string]$Setup, [string[]]$Arguments, [string]$Name) {
    $log = "$results\setup-$Name.log"
    $p = Start-Process -FilePath $Setup -ArgumentList ($Arguments + @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$log")) -PassThru
    $code = Wait-OrKill $p "setup $Name"
    Read-SetupLog $log
    Add-Content -Path "$results\install-output.txt" -Value "> setup $Name ($([IO.Path]::GetFileName($Setup)) $($Arguments -join ' ')): exit $code"
    return $code
}

# Runs a setup the way yolime's updater does (/UPDATE /RESULT) and counts the Yolime.App processes started meanwhile
# (a WMI start trace: a second launch that hands over to the first and exits in milliseconds still counts).
function Invoke-Update([string]$Setup, [string]$Name, [string]$ResultFile) {
    $resultFile = if ($ResultFile) { $ResultFile } else { "$results\result-$Name.txt" }
    Remove-Item $resultFile -ErrorAction SilentlyContinue
    $source = "yolime-start-$Name"
    Register-WmiEvent -Query "SELECT * FROM Win32_ProcessStartTrace WHERE ProcessName = 'Yolime.App.exe'" -SourceIdentifier $source
    try {
        # Quoted here: Start-Process joins its arguments with spaces and quotes nothing (yolime's updater passes an
        # argument list, which does quote).
        $code = Invoke-Setup $Setup @('/UPDATE', "/RESULT=`"$resultFile`"") $Name
        Start-Sleep -Seconds 5 # DeinitializeSetup's launch is the last thing setup does; let its trace event arrive
        $script:UpdateStarts = @(Get-Event -SourceIdentifier $source -ErrorAction SilentlyContinue).Count
    } finally {
        Unregister-Event -SourceIdentifier $source -ErrorAction SilentlyContinue
        Remove-Event -SourceIdentifier $source -ErrorAction SilentlyContinue
    }
    $script:UpdateResult = if (Test-Path $resultFile) { (Get-Content $resultFile -Raw).Trim() } else { '(none)' }
    Add-Content -Path "$results\install-output.txt" -Value "> update $Name -> result $($script:UpdateResult), starts $($script:UpdateStarts)"
    return $code
}

# The uninstaller re-runs itself from %TEMP% (as _unins.tmp in Inno 6.7, _iu*.tmp in older ones); wait for that copy
# too, since its work is the uninstall.
function Invoke-Uninstall([string[]]$Arguments, [string]$Name) {
    if (-not (Test-Path $uninstaller)) { return -2 }
    $log = "$results\uninstall-$Name.log"
    $p = Start-Process -FilePath $uninstaller -ArgumentList ($Arguments + @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$log")) -PassThru
    $code = Wait-OrKill $p "uninstall $Name"
    [void](Wait-Until { @(Get-Process | Where-Object { $_.ProcessName -like '_iu*' -or $_.ProcessName -like '_unins*' -or $_.ProcessName -like 'unins0*' }).Count -eq 0 } 180000)
    Read-SetupLog $log
    Add-Content -Path "$results\install-output.txt" -Value "> uninstall $Name ($($Arguments -join ' ')): exit $code"
    return $code
}

# $null when the folder holds exactly the files of the reference folder, byte for byte; otherwise the first difference.
function Compare-Tree([string]$Folder, [string]$Reference) {
    if (-not (Test-Path $Folder)) { return "$Folder is missing" }
    $want = @{}
    $refRoot = (Resolve-Path $Reference).Path.TrimEnd('\') + '\'
    foreach ($f in Get-ChildItem -LiteralPath $Reference -Recurse -File -Force) { $want[$f.FullName.Substring($refRoot.Length)] = $f.FullName }
    $root = (Resolve-Path $Folder).Path.TrimEnd('\') + '\'
    $seen = 0
    foreach ($f in Get-ChildItem -LiteralPath $Folder -Recurse -File -Force) {
        $rel = $f.FullName.Substring($root.Length)
        if (-not $want.ContainsKey($rel)) { return "extra file $rel" }
        if ((Get-FileHash -LiteralPath $f.FullName).Hash -ne (Get-FileHash -LiteralPath $want[$rel]).Hash) { return "$rel differs from the build" }
        $seen++
    }
    if ($seen -ne $want.Count) { return "$($want.Count - $seen) files of the build are missing" }
    return $null
}

# cmdkey /list:<target> echoes the target even when there's none ("* NONE *"); a stored one has a Target: line.
function Test-Key { return ((cmdkey /list:yolime/OPENAI_API_KEY | Out-String) -match 'Target:.*yolime/OPENAI_API_KEY') }

function Test-Shortcut {
    if (-not (Test-Path $shortcut)) { return 'no Start menu shortcut' }
    $link = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
    if ($link.TargetPath -ne $exe) { return "shortcut target is '$($link.TargetPath)'" }
    if ($link.WorkingDirectory -ne $dir) { return "shortcut working folder is '$($link.WorkingDirectory)'" }
    if ($link.Description -ne 'yolime: hold a key, speak, and the text is typed for you') { return "shortcut description is '$($link.Description)'" }
    return $null
}

try {
    # 0. The guard against elevated installs (only testable here when this session is elevated).
    if ($elevated) {
        $code = Invoke-Install @('-From', "$t\pkg", '-NoLaunch') -Plain
        Add-Result 'Elevated_IsRefused' $(if ($code -ne 0 -and -not (Test-Path $dir)) { $null } else { "exit $code, installed: $(Test-Path $dir)" })
    }

    # 1. The check can fail: an E2E build is refused, and nothing is installed.
    $code = Invoke-Install @('-From', "$t\e2epkg", '-NoLaunch')
    Add-Result 'E2EBuild_IsRefused' $(if ($code -ne 0 -and -not (Test-Path $dir) -and $script:LastOutput -match 'contains E2E-only code') { $null } else { "exit $code, installed: $(Test-Path $dir)" })

    # 2. The start check can see the bug: a copy without Yolime.App.pri doesn't stay running. (Installed by hand, since
    #    install.ps1 itself refuses it.)
    $nopri = 'C:\nopri'
    Copy-Item -Path "$t\pkg" -Destination $nopri -Recurse
    Remove-Item "$nopri\Yolime.App.pri"
    Start-Process -FilePath "$nopri\Yolime.App.exe" -WorkingDirectory $nopri
    Start-Sleep -Seconds 20
    $alive = @(Get-Running | Where-Object { $_.Path -like "$nopri\*" }).Count -gt 0
    Add-Result 'NoPri_DoesNotStart' $(if (-not $alive) { $null } else { 'a build without Yolime.App.pri kept running: the start check would prove nothing' })
    Stop-All

    # 3. First install: files, shortcut, the Run value re-pointed (quoted), and the app running from the install.
    if (-not (Test-Path $runKey)) { New-Item -Path $runKey | Out-Null }
    Set-ItemProperty -Path $runKey -Name 'yolime' -Value '"C:\dev\Yolime.App.exe"'
    $code = Invoke-Install @('-From', "$t\pkg")
    $fail = $null
    if ($code -ne 0) { $fail = "exit $code" }
    elseif (-not (Test-Path $exe) -or -not (Test-Path (Join-Path $dir 'Yolime.App.pri'))) { $fail = 'files missing' }
    elseif (-not (Test-Path $shortcut)) { $fail = 'no Start menu shortcut' }
    elseif ((Get-RunValue) -ne ('"' + $exe + '"')) { $fail = "Run value is '$(Get-RunValue)'" }
    else {
        Start-Sleep -Seconds 20
        $p = @(Get-Running | Where-Object { $_.Path -eq $exe })
        if ($p.Count -eq 0) { $fail = 'not running 20 s after install' }
        elseif (-not (Wait-Until { (Get-Process -Id $p[0].Id).MainWindowTitle -ne '' } 10000)) { $fail = 'running, but no window (the welcome window)' }
    }
    Add-Result 'Install_StartsFromTheInstallFolder' $fail

    # 4. While it runs, a plain install is refused and leaves the files alone.
    $hash = (Get-FileHash $exe).Hash
    $code = Invoke-Install @('-From', "$t\pkg", '-NoLaunch')
    Add-Result 'WhileRunning_IsRefused' $(if ($code -eq 2 -and (Get-FileHash $exe).Hash -eq $hash -and (Get-Running).Count -gt 0) { $null } else { "exit $code" })

    # 5. -Force: asks it to exit, upgrades, relaunches; the old copy is gone.
    $before = @(Get-Running | ForEach-Object { $_.Id })
    $code = Invoke-Install @('-From', "$t\pkg", '-Force')
    $fail = $null
    if ($code -ne 0) { $fail = "exit $code" }
    elseif ($script:LastOutput -notmatch 'It exited in') { $fail = 'yolime had to be killed: the clean exit (tray Exit) never landed' }
    elseif (Test-Path "$dir.old") { $fail = 'Yolime.old left behind' }
    elseif (Test-Path "$dir.new") { $fail = 'Yolime.new left behind' }
    elseif (-not (Wait-Until { @(Get-Running | Where-Object { $_.Path -eq $exe -and $before -notcontains $_.Id }).Count -gt 0 } 10000)) { $fail = 'the new copy is not running' }
    Add-Result 'ForceUpgrade_Relaunches' $fail

    # 6. Uninstall -Force: program, shortcut and Run value gone; the user's data stays.
    $code = Invoke-Install @('-Uninstall', '-Force')
    $fail = $null
    if ($code -ne 0) { $fail = "exit $code" }
    elseif ($script:LastOutput -notmatch 'It exited in') { $fail = 'yolime had to be killed: the clean exit (tray Exit) never landed' }
    elseif ((Get-Running).Count -gt 0) { $fail = 'still running' }
    elseif (Test-Path $dir) { $fail = 'install folder still there' }
    elseif (Test-Path $shortcut) { $fail = 'shortcut still there' }
    elseif (Get-RunValue) { $fail = "Run value still there: $(Get-RunValue)" }
    elseif (-not (Test-Path (Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Yolime'))) { $fail = '%APPDATA%\Yolime was removed' }
    elseif (-not (Test-Path (Join-Path $local 'Yolime'))) { $fail = '%LOCALAPPDATA%\Yolime was removed' }
    Add-Result 'Uninstall_KeepsData' $fail

    # 7. Start with Windows from the script: on writes the quoted value and re-enables a Task Manager "off"; off removes both.
    # -Force only creates missing parents here: the key doesn't exist yet, so there are no values to lose.
    if (-not (Test-Path $approvedKey)) { New-Item -Path $approvedKey -Force | Out-Null }
    Set-ItemProperty -Path $approvedKey -Name 'yolime' -Value ([byte[]](3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)) -Type Binary
    $code = Invoke-Install @('-From', "$t\pkg", '-NoLaunch', '-StartWithWindows', 'on')
    $fail = $null
    if ($code -ne 0) { $fail = "on: exit $code" }
    elseif ((Get-RunValue) -ne ('"' + $exe + '"')) { $fail = "on: Run value is '$(Get-RunValue)'" }
    elseif (((Get-ApprovedValue)[0] -band 1) -ne 0) { $fail = 'on: still disabled in Task Manager' }
    else {
        $code = Invoke-Install @('-From', "$t\pkg", '-NoLaunch', '-StartWithWindows', 'off')
        if ($code -ne 0) { $fail = "off: exit $code" }
        elseif (Get-RunValue) { $fail = "off: Run value still there: $(Get-RunValue)" }
        elseif ($null -ne (Get-ApprovedValue)) { $fail = 'off: StartupApproved value still there' }
    }
    Add-Result 'Script_StartWithWindows_OnAndOff' $fail
    [void](Invoke-Install @('-Uninstall', '-Force'))

    # The -RemoveData guard: %APPDATA%\Yolime itself a junction is refused, and its target survives.
    $roaming = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Yolime'
    $canary2 = 'C:\canary2'
    New-Item -ItemType Directory -Force $canary2 | Out-Null
    Set-Content -Path "$canary2\keep.txt" -Value 'keep'
    if (Test-Path $roaming) { Rename-Item $roaming 'Yolime.real' }
    cmd /c mklink /J "$roaming" "$canary2" | Out-Null
    [void](Invoke-Install @('-From', "$t\pkg", '-NoLaunch'))
    $code = Invoke-Install @('-Uninstall', '-Force', '-RemoveData')
    $fail = $null
    if (-not (Test-Path "$canary2\keep.txt")) { $fail = 'the junction target was emptied' }
    elseif (Test-Path $dir) { $fail = "the program wasn't removed (exit $code)" }
    elseif ($script:LastOutput -notmatch 'link or junction') { $fail = 'no refusal message' }
    elseif ($code -ne 3) { $fail = "exit $code, not 3 (uninstalled, data kept)" }
    Add-Result 'RemoveData_JunctionAtAppData_IsRefused' $fail
    cmd /c rmdir "$roaming" | Out-Null
    if (Test-Path "$roaming.real") { Rename-Item "$roaming.real" 'Yolime' }

    # ---- yolime-setup.exe ----
    $version = (Get-Content "$t\setup\version.txt" -Raw).Trim()
    $shipped = "$t\setup\yolime-setup-$version.exe"
    $sandboxSetup = "$t\setup\yolime-setup-$version-sandboxtest.exe"
    $setupDir = Join-Path $local 'Programs\Yolime Setup'
    $uninstaller = Join-Path $setupDir 'unins000.exe'
    $appsKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{A601F07C-0247-4CB7-ACCC-3B0F33A6BE52}_is1'
    $localData = Join-Path $local 'Yolime'

    # 8. The shipped setup has no Sandbox bypass: here (elevated) it refuses and writes nothing.
    if ($elevated) {
        $code = Invoke-Setup $shipped @() 'shipped-elevated'
        $fail = $null
        if ($code -eq 0) { $fail = 'exit 0' }
        elseif ((Test-Path $dir) -or (Test-Path $setupDir) -or (Test-Path $appsKey)) { $fail = "it installed (exit $code)" }
        Add-Result 'SetupShipped_Elevated_IsRefused' $fail
    } else {
        Add-Result 'SetupShipped_Elevated_IsRefused' 'the Sandbox session is not elevated, so this case proves nothing'
    }

    # 9. A fresh install: exactly the build, the shortcut, the Apps entry, start with Windows on (re-enabled), running.
    Set-ItemProperty -Path $approvedKey -Name 'yolime' -Value ([byte[]](3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)) -Type Binary
    # Silent setups leave start-with-Windows alone unless told (the wizard's box defaults to on for a fresh install).
    $code = Invoke-Setup $sandboxSetup @('/STARTUP=on') 'install'
    $fail = $null
    if ($code -ne 0) { $fail = "exit $code" }
    elseif ($d = Compare-Tree $dir "$t\pkg") { $fail = $d }
    elseif ($s = Test-Shortcut) { $fail = $s }
    elseif ((Get-ItemProperty $appsKey -ErrorAction SilentlyContinue).DisplayVersion -ne $version) { $fail = "Apps entry version is '$((Get-ItemProperty $appsKey -ErrorAction SilentlyContinue).DisplayVersion)'" }
    elseif (-not (Test-Path $uninstaller)) { $fail = 'no uninstaller' }
    elseif ((Get-RunValue) -ne ('"' + $exe + '"')) { $fail = "Run value is '$(Get-RunValue)'" }
    elseif (((Get-ApprovedValue)[0] -band 1) -ne 0) { $fail = 'start with Windows still disabled in Task Manager' }
    else {
        $p = @(Get-Running | Where-Object { $_.Path -eq $exe })
        if ($p.Count -eq 0) { $fail = 'not running after setup' }
        elseif (-not (Wait-Until { (Get-Process -Id $p[0].Id).MainWindowTitle -ne '' } 10000)) { $fail = 'running, but no window (the welcome window)' }
    }
    Add-Result 'Setup_Install' $fail

    # 10. Upgrade while it runs: closed cleanly, the folder is exactly the build again (an altered file and a stale one
    #     fixed), a new copy runs.
    $before = @(Get-Running | ForEach-Object { $_.Id })
    Add-Content -Path (Join-Path $dir 'Yolime.App.runtimeconfig.json') -Value ' '
    Set-Content -Path (Join-Path $dir 'stale.txt') -Value 'stale'
    $code = Invoke-Setup $sandboxSetup @() 'upgrade'
    $fail = $null
    if ($code -ne 0) { $fail = "exit $code" }
    elseif ($d = Compare-Tree $dir "$t\pkg") { $fail = $d }
    elseif ($script:LastSetupLog -notmatch 'It exited in') { $fail = 'yolime had to be killed: the clean exit never landed' }
    elseif (-not (Wait-Until { @(Get-Running | Where-Object { $_.Path -eq $exe -and $before -notcontains $_.Id }).Count -gt 0 } 10000)) { $fail = 'the new copy is not running' }
    Add-Result 'Setup_UpgradeWhileRunning' $fail

    # 11. The script over a setup install: the folder is the build, the Apps entry and its uninstaller survive.
    $code = Invoke-Install @('-From', "$t\pkg", '-Force', '-NoLaunch')
    $fail = $null
    if ($code -ne 0) { $fail = "exit $code" }
    elseif ($d = Compare-Tree $dir "$t\pkg") { $fail = $d }
    elseif (-not (Test-Path $appsKey) -or -not (Test-Path $uninstaller)) { $fail = 'the Apps entry or its uninstaller is gone' }
    Add-Result 'Script_OverSetup' $fail

    # 12. /DIR= can't move setup's folder.
    New-Item -ItemType Directory -Force 'C:\victim' | Out-Null
    Set-Content -Path 'C:\victim\keep.txt' -Value 'keep'
    $code = Invoke-Setup $sandboxSetup @('/DIR=C:\victim') 'dir-override'
    $fail = $null
    if ($code -eq 0) { $fail = 'exit 0' }
    elseif (Test-Path 'C:\victim\unins000.exe') { $fail = 'setup installed into C:\victim' }
    elseif (-not (Test-Path 'C:\victim\keep.txt')) { $fail = 'C:\victim was emptied' }
    elseif ((Get-ItemProperty $appsKey -ErrorAction SilentlyContinue).InstallLocation -notlike "$setupDir*") { $fail = "the Apps entry now points at '$((Get-ItemProperty $appsKey -ErrorAction SilentlyContinue).InstallLocation)'" }
    Add-Result 'Setup_DirOverride_IsRefused' $fail

    # 13. An uninstall the script refuses (a file held open) keeps the Apps entry, its uninstaller, and the program.
    Stop-All
    $held = [IO.File]::Open((Join-Path $dir 'Yolime.App.runtimeconfig.json'), 'Open', 'Read', 'None')
    try { $code = Invoke-Uninstall @() 'uninstall-held' } finally { $held.Dispose() }
    $fail = $null
    if (-not (Test-Path $appsKey)) { $fail = 'the Apps entry was removed' }
    elseif (-not (Test-Path $uninstaller)) { $fail = 'the uninstaller was removed' }
    elseif (-not (Test-Path $exe)) { $fail = 'the program was removed' }
    Add-Result 'Setup_UninstallFails_KeepsTheEntry' $fail

    # 14. Uninstall from the Apps entry: everything of the program goes; data and a Run value for another copy stay.
    Set-ItemProperty -Path $runKey -Name 'yolime' -Value '"C:\dev\Yolime.App.exe"'
    $code = Invoke-Uninstall @() 'uninstall'
    $fail = $null
    if (Test-Path $appsKey) { $fail = "the Apps entry is still there (exit $code)" }
    elseif (Test-Path $dir) { $fail = 'the program is still there' }
    elseif (Test-Path $setupDir) { $fail = "setup's folder is still there" }
    elseif (Test-Path $shortcut) { $fail = 'the shortcut is still there' }
    elseif ((Get-RunValue) -ne '"C:\dev\Yolime.App.exe"') { $fail = "the other copy's Run value is '$(Get-RunValue)'" }
    elseif (-not (Test-Path $roaming) -or -not (Test-Path $localData)) { $fail = 'data was removed' }
    Add-Result 'Setup_Uninstall_KeepsData' $fail
    Remove-ItemProperty -Path $runKey -Name 'yolime' -ErrorAction SilentlyContinue

    # 15. Setup over a script install: the folder is the build, and it now has an Apps entry.
    [void](Invoke-Install @('-From', "$t\pkg", '-NoLaunch'))
    $code = Invoke-Setup $sandboxSetup @() 'over-script'
    $fail = $null
    if ($code -ne 0) { $fail = "exit $code" }
    elseif ($d = Compare-Tree $dir "$t\pkg") { $fail = $d }
    elseif (-not (Test-Path $appsKey) -or -not (Test-Path $uninstaller)) { $fail = 'no Apps entry or uninstaller' }
    Add-Result 'Setup_OverScript' $fail

    # 16. /REMOVEDATA while a copy from elsewhere runs: the program goes, the data (and its keys) stay.
    Stop-All
    cmdkey /generic:yolime/OPENAI_API_KEY /user:yolime /pass:sk-test | Out-Null
    $devcopy = 'C:\devcopy'
    Copy-Item -Path "$t\pkg" -Destination $devcopy -Recurse
    Start-Process -FilePath "$devcopy\Yolime.App.exe" -WorkingDirectory $devcopy
    [void](Wait-Until { @(Get-Running | Where-Object { $_.Path -like "$devcopy\*" }).Count -gt 0 } 15000)
    $code = Invoke-Uninstall @('/REMOVEDATA') 'removedata-other-copy'
    $fail = $null
    if (Test-Path $dir) { $fail = "the program is still there (exit $code)" }
    elseif (-not (Test-Path $roaming) -or -not (Test-Path $localData)) { $fail = 'data was removed while another copy ran' }
    elseif ($script:LastSetupLog -notmatch 'Data kept: ') { $fail = "the uninstaller didn't report the kept data" }
    elseif (-not (Test-Key)) { $fail = 'the key was removed while another copy ran' }
    Add-Result 'Setup_RemoveData_RefusedWhileAnotherCopyRuns' $fail
    Stop-All

    # 17. A junction at the install folder is refused, and its target survives.
    New-Item -ItemType Directory -Force 'C:\target' | Out-Null
    Set-Content -Path 'C:\target\keep.txt' -Value 'keep'
    cmd /c mklink /J "$dir" 'C:\target' | Out-Null
    $code = Invoke-Setup $sandboxSetup @() 'reparse'
    $fail = $null
    if ($code -eq 0) { $fail = 'exit 0' }
    elseif (-not (Test-Path 'C:\target\keep.txt')) { $fail = 'the junction target was emptied' }
    elseif (Test-Path $appsKey) { $fail = 'an Apps entry was written' }
    Add-Result 'Setup_ReparseAtApp_IsRefused' $fail
    cmd /c rmdir "$dir" | Out-Null

    # 18. /REMOVEDATA: data folders and keys go; a junction inside the data is removed without emptying its target.
    [void](Invoke-Setup $sandboxSetup @() 'install-2')
    Stop-All
    cmdkey /generic:yolime/OPENAI_API_KEY /user:yolime /pass:sk-test | Out-Null
    New-Item -ItemType Directory -Force 'C:\canary' | Out-Null
    Set-Content -Path 'C:\canary\keep.txt' -Value 'keep'
    cmd /c mklink /J "$localData\link" 'C:\canary' | Out-Null
    $code = Invoke-Uninstall @('/REMOVEDATA') 'removedata'
    $fail = $null
    if (Test-Path $appsKey) { $fail = "the Apps entry is still there (exit $code)" }
    elseif ((Test-Path $roaming) -or (Test-Path $localData)) { $fail = 'a data folder is still there' }
    elseif (Test-Key) { $fail = 'the key is still there' }
    elseif (-not (Test-Path 'C:\canary\keep.txt')) { $fail = 'the junction target inside the data was emptied' }
    Add-Result 'Setup_Uninstall_RemoveData' $fail

    # 19. The uninstall files are missing: the Apps entry can still be removed.
    [void](Invoke-Setup $sandboxSetup @() 'install-3')
    Stop-All
    Remove-Item -LiteralPath (Join-Path $setupDir 'install.ps1')
    $code = Invoke-Uninstall @() 'uninstall-missing'
    Add-Result 'Setup_UninstallFilesMissing_RemovesTheEntry' $(if (-not (Test-Path $appsKey)) { $null } else { "the Apps entry is still there (exit $code)" })
    # That left the program installed (the entry went, the files stayed); start the update cases from a clean install.
    [void](Invoke-Install @('-Uninstall', '-Force'))

    # ---- /UPDATE: what yolime's own updater runs ----
    # 20. A good update while yolime runs: it's asked to exit, the install happens without launching, and setup's
    #     DeinitializeSetup starts it again: exactly once (no second launch handing over and saying "already running").
    [void](Invoke-Setup $sandboxSetup @() 'update-seed')
    [void](Wait-Until { @(Get-Running | Where-Object { $_.Path -eq $exe }).Count -gt 0 } 20000)
    $code = Invoke-Update $sandboxSetup 'update-ok'
    $fail = $null
    if ($code -ne 0) { $fail = "exit $code" }
    elseif ($script:UpdateResult -ne 'ok') { $fail = "result '$($script:UpdateResult)'" }
    elseif ($script:UpdateStarts -ne 1) { $fail = "yolime was started $($script:UpdateStarts) times" }
    elseif (-not (Wait-Until { @(Get-Running | Where-Object { $_.Path -eq $exe }).Count -gt 0 } 10000)) { $fail = 'not running afterwards' }
    Add-Result 'Update_Success_StartsItOnce' $fail

    # 21. A payload the script refuses (no Yolime.App.pri): refused before yolime is closed, so it keeps running and
    #     nothing is started.
    $before = @(Get-Running | ForEach-Object { $_.Id })
    $code = Invoke-Update "$t\setup\nopri\yolime-setup-$version-sandboxtest.exe" 'update-nopri'
    $fail = $null
    if ($script:UpdateResult -ne 'check-failed') { $fail = "result '$($script:UpdateResult)' (exit $code)" }
    elseif ($script:UpdateStarts -ne 0) { $fail = "yolime was started $($script:UpdateStarts) times" }
    elseif (@(Get-Running | Where-Object { $before -contains $_.Id }).Count -eq 0) { $fail = 'the running copy was closed' }
    Add-Result 'Update_RefusedPayload_KeepsItRunning' $fail

    # 22. A failure after yolime was closed (a file held open blocks moving the old install aside): the old copy is
    #     started again, once. This is the case the relaunch exists for.
    # The update carries the same build, so a marker in the old folder is what tells old from new afterwards.
    Set-Content -Path (Join-Path $dir 'old-install-marker.txt') -Value 'old'
    $held = [IO.File]::Open((Join-Path $dir 'Yolime.App.runtimeconfig.json'), 'Open', 'Read', 'Read')
    try { $code = Invoke-Update $sandboxSetup 'update-held' } finally { $held.Dispose() }
    $fail = $null
    if ($script:UpdateResult -ne 'swap-failed') { $fail = "result '$($script:UpdateResult)' (exit $code)" }
    elseif ($script:UpdateStarts -ne 1) { $fail = "yolime was started $($script:UpdateStarts) times" }
    elseif (-not (Test-Path (Join-Path $dir 'old-install-marker.txt'))) { $fail = 'the old install was replaced' }
    elseif (-not (Wait-Until { @(Get-Running | Where-Object { $_.Path -eq $exe }).Count -gt 0 } 10000)) { $fail = 'not running afterwards' }
    Add-Result 'Update_FailsAfterStop_StartsTheOldCopy' $fail

    # 23. The shipped setup run elevated under /UPDATE refuses, says so in the result, and starts nothing, even with
    #     yolime closed (so the relaunch would otherwise have run): it must never start yolime elevated.
    Stop-All
    if ($elevated) {
        $code = Invoke-Update $shipped 'update-elevated'
        $fail = $null
        if ($script:UpdateResult -ne 'setup-failed') { $fail = "result '$($script:UpdateResult)' (exit $code)" }
        elseif ($script:UpdateStarts -ne 0) { $fail = "yolime was started $($script:UpdateStarts) times" }
        Add-Result 'Update_Elevated_RefusedAndStartsNothing' $fail
    }

    # 24. Only the SandboxE2E variant may install an E2E payload: the elevated-capable test variant refuses it.
    $code = Invoke-Setup "$t\setup\e2epayload\yolime-setup-$version-sandboxtest.exe" @() 'e2e-payload'
    Add-Result 'SetupSandboxTest_RefusesE2EPayload' $(if ($code -ne 0 -and $script:LastSetupLog -match 'contains E2E-only code') { $null } else { "exit $code" })

    # 25. A thrown error still writes the result file (here: a -From folder that doesn't exist).
    $resultFile = "$results\result-thrown.txt"
    [void](Invoke-Install @('-From', 'C:\no-such-folder', '-NoLaunch', '-ResultFile', $resultFile))
    Add-Result 'Script_ThrownError_WritesResult' $(if ((Test-Path $resultFile) -and (Get-Content $resultFile -Raw).Trim() -eq 'error') { $null } else { 'no "error" result' })

    # 26. The SandboxE2E variant (what the in-app update tests use) installs an E2E payload under /UPDATE, with its
    #     result written to a path with a space in it (as %LOCALAPPDATA% has for some user names).
    Stop-All
    New-Item -ItemType Directory -Force "$results\with space" | Out-Null
    $code = Invoke-Update "$t\setup\sandboxe2e\yolime-setup-$version-sandboxe2e.exe" 'update-e2e' "$results\with space\result.txt"
    $fail = $null
    if ($script:UpdateResult -ne 'ok') { $fail = "result '$($script:UpdateResult)' (exit $code)" }
    elseif ((Get-FileHash (Join-Path $dir 'Yolime.App.dll')).Hash -ne (Get-FileHash "$t\e2epkg\Yolime.App.dll").Hash) { $fail = 'the installed Yolime.App.dll is not the E2E build' }
    Add-Result 'SetupSandboxE2E_InstallsAnE2EPayload_ResultPathWithSpace' $fail
} catch {
    Add-Result 'harness' ($_.Exception.GetType().Name + ': ' + $_.Exception.Message)
} finally {
    Stop-All
    [IO.File]::WriteAllText("$results\install.json", ($report | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText("$results\DONE", 'done')
}
