# Tests tools\install.ps1 inside Windows Sandbox (never on the real desktop: it installs and starts yolime).
# Publishes a Release build and an E2E build, stages them with the scripts under artifacts\install-test (mapped
# read-only), and runs tests\Install\install-test.ps1 there under Windows PowerShell 5.1.
# Results: artifacts\install-test-results\install.json (+ install-output.txt).
# Usage (pwsh, from the repo root):  ./tests/Install/run-install-test.ps1
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$stage = Join-Path $repo 'artifacts\install-test'
$results = Join-Path $repo 'artifacts\install-test-results'

function Get-Sandboxes { @((wsb list --raw 2>$null | ConvertFrom-Json).WindowsSandboxEnvironments | ForEach-Object { if ($_ -is [string]) { $_ } else { $_.Id } }) }
function Stop-Sandboxes { foreach ($id in Get-Sandboxes) { wsb stop --id $id | Out-Null } }
Stop-Sandboxes
$wait = (Get-Date).AddMinutes(3)
while (((Get-Sandboxes).Count -gt 0 -or (Get-Process -Name WindowsSandboxRemoteSession -ErrorAction SilentlyContinue)) -and (Get-Date) -lt $wait) { Start-Sleep -Seconds 5 }

Remove-Item -Recurse -Force $stage, $results -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force (Join-Path $stage 'tools'), (Join-Path $stage 'tests'), $results | Out-Null
dotnet publish (Join-Path $repo 'src\Yolime.App') -c Release -o (Join-Path $stage 'pkg') | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
dotnet build (Join-Path $repo 'src\Yolime.App') -p:YolimeE2E=true | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'E2E build failed' }
Copy-Item -Recurse (Join-Path $repo 'src\Yolime.App\bin\e2e\Debug\net10.0-windows10.0.19041.0\win-x64') (Join-Path $stage 'e2epkg')
Copy-Item (Join-Path $repo 'tools\install.ps1'), (Join-Path $repo 'tools\check-publish.ps1') (Join-Path $stage 'tools')
Copy-Item (Join-Path $PSScriptRoot 'install-test.ps1') (Join-Path $stage 'tests')
# Both setups wrap the very pkg the test compares against: the shipped one, and the Sandbox variant (the only one
# that may run elevated, since the Sandbox's one account is an administrator).
$setupOut = Join-Path $stage 'setup'
New-Item -ItemType Directory -Force $setupOut | Out-Null
# Hashtables: an array splat would pass "-SandboxTest" as a positional string (it bound to -Iscc).
foreach ($variant in @(@{ Dir = 'shipped'; Args = @{} }, @{ Dir = 'sandbox'; Args = @{ SandboxTest = $true } })) {
    $out = Join-Path $repo "artifacts\setup-test-$($variant.Dir)"
    $setupArgs = $variant.Args
    & (Join-Path $repo 'tools\build-setup.ps1') -PublishDir (Join-Path $stage 'pkg') -OutDir $out @setupArgs | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "build-setup failed ($($variant.Dir))" }
    Copy-Item (Join-Path $out '*') $setupOut -Force
}
# Two deliberately bad payloads for the update cases, in the elevated-capable Sandbox variant: one without
# Yolime.App.pri (the script refuses it before closing yolime), and the E2E build (which only the SandboxE2E variant
# may install, so this one must refuse it).
$nopri = Join-Path $stage 'pkg-nopri'
Copy-Item -Recurse (Join-Path $stage 'pkg') $nopri
Remove-Item (Join-Path $nopri 'Yolime.App.pri')
# And the SandboxE2E variant itself, which the in-app update tests use to install E2E builds.
foreach ($bad in @(@{ Dir = 'nopri'; From = $nopri; Args = @{ SandboxTest = $true } },
                   @{ Dir = 'e2epayload'; From = (Join-Path $stage 'e2epkg'); Args = @{ SandboxTest = $true } },
                   @{ Dir = 'sandboxe2e'; From = (Join-Path $stage 'e2epkg'); Args = @{ SandboxE2E = $true } })) {
    $out = Join-Path $repo "artifacts\setup-test-$($bad.Dir)"
    $variantArgs = $bad.Args
    & (Join-Path $repo 'tools\build-setup.ps1') -PublishDir $bad.From -OutDir $out -SkipCheck @variantArgs | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "build-setup failed ($($bad.Dir))" }
    $to = Join-Path $setupOut $bad.Dir
    New-Item -ItemType Directory -Force $to | Out-Null
    Copy-Item (Join-Path $out '*.exe') $to
}
Remove-Item -Recurse -Force $nopri
$version = (Get-Content (Join-Path $setupOut 'version.txt') -Raw).Trim()
foreach ($name in "yolime-setup-$version.exe", "yolime-setup-$version-sandboxtest.exe") {
    if (-not (Test-Path (Join-Path $setupOut $name))) { throw "missing $name" }
}

$wsb = @"
<Configuration>
  <Networking>Disable</Networking>
  <ClipboardRedirection>Disable</ClipboardRedirection>
  <AudioInput>Disable</AudioInput>
  <VideoInput>Disable</VideoInput>
  <PrinterRedirection>Disable</PrinterRedirection>
  <MappedFolders>
    <MappedFolder><HostFolder>$stage</HostFolder><SandboxFolder>C:\t</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$results</HostFolder><SandboxFolder>C:\results</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
  <LogonCommand><Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\t\tests\install-test.ps1</Command></LogonCommand>
</Configuration>
"@
$wsbPath = Join-Path $repo 'artifacts\install-test.wsb'
[IO.File]::WriteAllText($wsbPath, $wsb)
Start-Process 'C:\Windows\System32\WindowsSandbox.exe' -ArgumentList $wsbPath
$deadline = (Get-Date).AddMinutes(45)
while (-not (Test-Path (Join-Path $results 'DONE')) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 5 }
Stop-Sandboxes
if (-not (Test-Path (Join-Path $results 'install.json'))) { throw 'no results: the test did not finish' }
# Only data is read back from the Sandbox, never executed.
$report = Get-Content (Join-Path $results 'install.json') -Raw | ConvertFrom-Json
$report | Format-Table name, pass, failure -AutoSize -Wrap | Out-Host
if ($report | Where-Object { -not $_.pass }) { exit 1 }
