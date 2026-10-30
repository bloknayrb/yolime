# Builds yolime-setup-<version>.exe (installer\yolime.iss) from a checked Release publish.
#
#   pwsh -File tools\build-setup.ps1                          # publish Release, check it, build artifacts\setup\*.exe
#   tools\build-setup.ps1 -PublishDir <folder>                # package an existing publish folder (checked first)
#   tools\build-setup.ps1 -OutDir <folder>                    # write the setup there (emptied first)
#   tools\build-setup.ps1 -Iscc <path to ISCC.exe>            # a specific Inno Setup compiler
#   tools\build-setup.ps1 -SandboxTest                        # the install test's variant; never ship it
#   tools\build-setup.ps1 -SandboxE2E -SkipCheck              # the update tests' variant (an E2E payload); never ship it
#
# Needs the .NET 10 SDK (unless -PublishDir) and Inno Setup 6.3+: winget install JRSoftware.InnoSetup --scope user
# Writes version.txt next to the setup. Pure ASCII, and runs on Windows PowerShell 5.1 and PowerShell 7.
param(
    [string]$PublishDir,
    [string]$OutDir,
    [string]$Iscc,
    [switch]$SandboxTest,
    [switch]$SandboxE2E,
    # Package the folder unchecked: only for the Sandbox tests' E2E or deliberately broken payloads.
    [switch]$SkipCheck
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
# An unchecked payload is only ever for a Sandbox variant: never under the shipped setup's name.
if ($SkipCheck -and -not ($SandboxTest -or $SandboxE2E)) { Write-Host '-SkipCheck needs -SandboxTest or -SandboxE2E.'; exit 1 }

if (-not $PublishDir) {
    $PublishDir = Join-Path $repo 'artifacts\publish'
    # Emptied first: publish doesn't delete stale files, and what's packaged must be exactly this build.
    if (Test-Path -LiteralPath $PublishDir) { Remove-Item -LiteralPath $PublishDir -Recurse -Force }
    & dotnet publish (Join-Path $repo 'src\Yolime.App') -c Release -o $PublishDir
    if ($LASTEXITCODE -ne 0) { Write-Host 'dotnet publish failed.'; exit 1 }
}
$PublishDir = (Resolve-Path -LiteralPath $PublishDir).Path
if (-not $SkipCheck) {
    & (Join-Path $PSScriptRoot 'check-publish.ps1') -Folder $PublishDir
    if ($LASTEXITCODE -ne 0) { exit 1 }
}

# The version has one source, <Version> in Yolime.App.csproj. The SDK stamps it into ProductVersion as
# "0.1.0+<commit>"; the setup shows the short commit so two builds of one version can be told apart.
$product = (Get-Item -LiteralPath (Join-Path $PublishDir 'Yolime.App.exe')).VersionInfo.ProductVersion
if ($product -notmatch '^(\d+\.\d+\.\d+)(\+([0-9a-fA-F]+))?') { Write-Host "Unexpected ProductVersion '$product'."; exit 1 }
$fileVersion = $Matches[1]
$version = $fileVersion
if ($Matches[3]) { $version += '+' + $Matches[3].Substring(0, [Math]::Min(7, $Matches[3].Length)) }

if (-not $Iscc) {
    foreach ($candidate in @(
        (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'))) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) { $Iscc = $candidate; break }
    }
}
if (-not $Iscc -or -not (Test-Path -LiteralPath $Iscc)) {
    Write-Host 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup --scope user'
    exit 1
}
Write-Host "Using $Iscc"

if (-not $OutDir) { $OutDir = Join-Path $repo 'artifacts\setup' }
# Emptied first, so a stale setup from an earlier build is never the one found next to version.txt.
if (Test-Path -LiteralPath $OutDir) { Remove-Item -LiteralPath $OutDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutDir | Out-Null
$OutDir = (Resolve-Path -LiteralPath $OutDir).Path

$isccArgs = @("/DPublishDir=$PublishDir", "/DAppVersion=$version", "/DFileVersion=$fileVersion", "/O$OutDir")
if ($SandboxE2E) { $isccArgs += '/DSandboxE2E' }
elseif ($SandboxTest) { $isccArgs += '/DSandboxTest' }
& $Iscc @isccArgs (Join-Path $repo 'installer\yolime.iss')
if ($LASTEXITCODE -ne 0) { Write-Host "ISCC failed (exit $LASTEXITCODE)."; exit 1 }

$name = "yolime-setup-$version" + $(if ($SandboxE2E) { '-sandboxe2e' } elseif ($SandboxTest) { '-sandboxtest' } else { '' }) + '.exe'
$setup = Join-Path $OutDir $name
if (-not (Test-Path -LiteralPath $setup)) { Write-Host "ISCC didn't write $name."; exit 1 }
[IO.File]::WriteAllText((Join-Path $OutDir 'version.txt'), $version, (New-Object Text.UTF8Encoding($false)))
Write-Host ''
Write-Host "Built $setup"
Write-Host "SHA-256: $((Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash)"
exit 0
