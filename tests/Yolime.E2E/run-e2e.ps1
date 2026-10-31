# Runs the PR2 end-to-end scenarios inside Windows Sandbox (never on the real desktop).
# Builds the app's E2E build and the harness, stages them under artifacts/e2e (mapped read-only), launches the
# Sandbox, and waits for artifacts/e2e-results/DONE. Results: artifacts/e2e-results/e2e.json (+ .png on failure).
# Usage (pwsh, from the repo root):  ./tests/Yolime.E2E/run-e2e.ps1 [-Only Name1,Name2]
# -Only runs just the named scenarios (fresh-app ones included when named), e.g. to watch a new test fail on main.
# -Updates runs the in-app update scenarios instead, with their own stage (artifacts/e2e-updates) and results
# (artifacts/e2e-updates-results): E2E payloads 0.1.0 and 0.1.1, their SandboxE2E setups, and install.ps1 to seed them.
# -Tour runs the UI tour instead (docs/superpowers/plans/2026-10-30-ui-tour.md): images for the design pass, in
# artifacts/e2e-tour-results. -Areas overlay,cards,windows narrows it, so a fix PR re-runs only its area.
param([string[]]$Only, [switch]$Updates, [switch]$Tour, [string[]]$Areas)
$ErrorActionPreference = 'Stop'
if ($Tour -and ($Updates -or $Only)) { throw '-Tour runs on its own: not with -Updates or -Only' }
if ($Areas -and -not $Tour) { throw '-Areas is for -Tour' }
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$stage = Join-Path $repo $(if ($Updates) { 'artifacts\e2e-updates' } elseif ($Tour) { 'artifacts\e2e-tour' } else { 'artifacts\e2e' })
$results = Join-Path $repo $(if ($Updates) { 'artifacts\e2e-updates-results' } elseif ($Tour) { 'artifacts\e2e-tour-results' } else { 'artifacts\e2e-results' })

# A Sandbox launched while the previous one is still tearing down starts but never runs its logon command (seen
# twice as "no results"), so wait for every Sandbox process to be gone first.
# Sandboxes are listed and stopped through the wsb CLI: killing their processes (as this script used to) left state that
# made the next launch boot without running its logon command. (vmmemWindowsSandbox can stay up as a pre-warmed VM;
# it isn't a running Sandbox and isn't waited for.)
function Get-Sandboxes { @((wsb list --raw 2>$null | ConvertFrom-Json).WindowsSandboxEnvironments | ForEach-Object { if ($_ -is [string]) { $_ } else { $_.Id } }) }
function Stop-Sandboxes { foreach ($id in Get-Sandboxes) { wsb stop --id $id | Out-Null } }
function Wait-SandboxGone {
    Stop-Sandboxes
    $wait = (Get-Date).AddMinutes(3)
    while (((Get-Sandboxes).Count -gt 0 -or (Get-Process -Name WindowsSandboxRemoteSession -ErrorAction SilentlyContinue)) -and (Get-Date) -lt $wait) { Start-Sleep -Seconds 5 }
    if ((Get-Sandboxes).Count -gt 0) { throw 'A Windows Sandbox is still running and would not stop.' }
}
Wait-SandboxGone

# The app's E2E build is copied from build output: Directory.Build.props forbids publishing an E2E build.
# The update scenarios install a 0.1.0 and update it to 0.1.1, whatever the csproj's version is by now.
$pin = if ($Updates) { @('-p:Version=0.1.0') } else { @() }
dotnet build (Join-Path $repo 'src\Yolime.App') -p:YolimeE2E=true @pin | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'app build failed' }
dotnet publish (Join-Path $repo 'tests\Yolime.E2E') -c Release -o (Join-Path $stage 'harness') | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'harness publish failed' }

$appOut = Join-Path $repo 'src\Yolime.App\bin\e2e\Debug\net10.0-windows10.0.19041.0\win-x64'
Remove-Item -Recurse -Force (Join-Path $stage 'app'), (Join-Path $stage 'fixtures') -ErrorAction SilentlyContinue
if ($Updates) {
    # Two E2E payloads from the same output folder: 0.1.0 (pinned above) to install first, then 0.1.1 to update
    # to, and that one without Yolime.App.pri (setup refuses it before stopping anything). Each 0.1.1 is wrapped in the
    # SandboxE2E setup, which may run elevated and install an E2E payload; neither is ever shipped.
    Remove-Item -Recurse -Force (Join-Path $stage 'pay-0.1.0'), (Join-Path $stage 'setups'), (Join-Path $stage 'tools') -ErrorAction SilentlyContinue
    Copy-Item -Recurse $appOut (Join-Path $stage 'pay-0.1.0')
    $work = Join-Path $repo 'artifacts\e2e-updates-work'
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $work | Out-Null
    dotnet build (Join-Path $repo 'src\Yolime.App') -p:YolimeE2E=true -p:Version=0.1.1 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'app build (0.1.1) failed' }
    try {
        Copy-Item -Recurse $appOut (Join-Path $work 'pay-0.1.1')
        Copy-Item -Recurse (Join-Path $work 'pay-0.1.1') (Join-Path $work 'pay-nopri')
        Remove-Item (Join-Path $work 'pay-nopri\Yolime.App.pri')
        foreach ($variant in @(@{ Name = 'good'; From = 'pay-0.1.1' }, @{ Name = 'nopri'; From = 'pay-nopri' })) {
            $out = Join-Path $work "setup-$($variant.Name)"
            & (Join-Path $repo 'tools\build-setup.ps1') -PublishDir (Join-Path $work $variant.From) -OutDir $out -SandboxE2E -SkipCheck | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "build-setup failed ($($variant.Name))" }
            $to = Join-Path $stage "setups\$($variant.Name)"
            New-Item -ItemType Directory -Force $to | Out-Null
            # Named as a release names it: the feed's asset name is built from the version.
            Copy-Item (Get-Item (Join-Path $out '*-sandboxe2e.exe')).FullName (Join-Path $to 'yolime-setup-0.1.1.exe')
        }
    } finally {
        # Back to the csproj's version, so a later plain run doesn't stage 0.1.1 by accident.
        dotnet build (Join-Path $repo 'src\Yolime.App') -p:YolimeE2E=true | Out-Host
    }
    New-Item -ItemType Directory -Force (Join-Path $stage 'tools') | Out-Null
    Copy-Item (Join-Path $repo 'tools\install.ps1'), (Join-Path $repo 'tools\check-publish.ps1') (Join-Path $stage 'tools')
} else {
    Copy-Item -Recurse $appOut (Join-Path $stage 'app')
}
Copy-Item -Recurse (Join-Path $repo 'tests\fixtures\audio') (Join-Path $stage 'fixtures')
Remove-Item -Recurse -Force $results -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $results | Out-Null
# Split on commas too: "pwsh -File run-e2e.ps1 -Only A,B" passes "A,B" as one string, not an array.
if ($Only) { [IO.File]::WriteAllText((Join-Path $results 'only.txt'), (($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ }) -join "`n")) }
if ($Areas) { [IO.File]::WriteAllText((Join-Path $results 'areas.txt'), (($Areas | ForEach-Object { $_ -split ',' } | Where-Object { $_ }) -join "`n")) }

# Networking, clipboard sharing, the host mic and camera, and printers are all off: the run is sealed from the host.
$wsb = @"
<Configuration>
  <Networking>Disable</Networking>
  <ClipboardRedirection>Disable</ClipboardRedirection>
  <AudioInput>Disable</AudioInput>
  <VideoInput>Disable</VideoInput>
  <PrinterRedirection>Disable</PrinterRedirection>
  <MappedFolders>
    <MappedFolder><HostFolder>$stage</HostFolder><SandboxFolder>C:\e2e</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$results</HostFolder><SandboxFolder>C:\results</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
  <LogonCommand><Command>C:\e2e\harness\Yolime.E2E.exe$(if ($Updates) { ' --updates' } elseif ($Tour) { ' --tour' })</Command></LogonCommand>
</Configuration>
"@
$wsbPath = Join-Path $repo $(if ($Updates) { 'artifacts\e2e-updates.wsb' } elseif ($Tour) { 'artifacts\e2e-tour.wsb' } else { 'artifacts\e2e.wsb' })
[IO.File]::WriteAllText($wsbPath, $wsb)
# Sometimes a Sandbox boots but never runs its logon command. The harness writes "started" first thing, so a launch
# with no marker after 4 minutes is retried once rather than waited out.
for ($launch = 1; $launch -le 2; $launch++) {
    Start-Process 'C:\Windows\System32\WindowsSandbox.exe' -ArgumentList $wsbPath
    $boot = (Get-Date).AddMinutes(4)
    while (-not (Test-Path (Join-Path $results 'started')) -and (Get-Date) -lt $boot) { Start-Sleep -Seconds 5 }
    if (Test-Path (Join-Path $results 'started')) { break }
    Write-Host "Sandbox launch $launch never started the harness; relaunching"
    Stop-Sandboxes
    Wait-SandboxGone
}

# Each update scenario installs a fresh copy and runs a real setup, so they get longer; the tour launches four apps.
$deadline = (Get-Date).AddMinutes($(if ($Updates) { 35 } elseif ($Tour) { 40 } else { 18 }))
while (-not (Test-Path (Join-Path $results 'DONE')) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 5 }
Stop-Sandboxes
if (-not (Test-Path (Join-Path $results 'e2e.json'))) { throw 'no results: the harness did not finish' }
# Only data is read back from the Sandbox, never executed.
$report = Get-Content (Join-Path $results 'e2e.json') -Raw | ConvertFrom-Json
$report | Format-Table name, pass, failure -AutoSize -Wrap | Out-Host
if ($report | Where-Object { -not $_.pass }) { exit 1 }
