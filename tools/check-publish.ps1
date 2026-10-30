# Checks a yolime publish folder before it is installed (install.ps1) or after CI publishes it.
# - the files an unpackaged WinUI app needs to start, including Yolime.App.pri (a plain publish once left it out);
# - no E2E-only code: the markers are searched as UTF-16LE bytes, because that is how C# string literals sit in the DLL
#   (an ASCII search, or Select-String on Windows PowerShell 5.1, passes an E2E build).
# Usage: ./tools/check-publish.ps1 -Folder artifacts/publish     (exit 0 = good; otherwise prints why and exits 1)
# Pure ASCII, and runs on Windows PowerShell 5.1 and PowerShell 7.
#        -AllowE2EPayload skips the E2E-code check (the Sandbox update tests install E2E builds; never otherwise).
param(
    [Parameter(Mandatory = $true)][string]$Folder,
    [switch]$AllowE2EPayload
)
$ErrorActionPreference = 'Stop'
# .NET file calls resolve against the process directory, not PowerShell's location: make the path absolute first.
$Folder = (Resolve-Path -LiteralPath $Folder).Path

function Test-YolimePublish([string]$Path) {
    foreach ($name in 'Yolime.App.exe', 'Yolime.App.dll', 'Yolime.App.pri', 'Yolime.Core.dll') {
        if (-not (Test-Path -LiteralPath (Join-Path $Path $name) -PathType Leaf)) { return "missing $name" }
    }
    if ($AllowE2EPayload) { return $null }
    $bytes = [IO.File]::ReadAllBytes((Join-Path $Path 'Yolime.App.dll'))
    # Latin-1 maps every byte to one char, so IndexOf on the string is a byte search.
    $haystack = [Text.Encoding]::GetEncoding(28591).GetString($bytes)
    # A second guard after the build-time one, with some of ReleaseHasNoTestCodeTests' markers (that test checks more).
    # Whole strings: Release does contain "yolime-main".
    foreach ($marker in '--test-audio', 'yolime-main-e2e', '--e2e', 'e2e.json') {
        $needle = [Text.Encoding]::GetEncoding(28591).GetString([Text.Encoding]::Unicode.GetBytes($marker))
        if ($haystack.IndexOf($needle, [StringComparison]::Ordinal) -ge 0) { return "contains E2E-only code ($marker)" }
    }
    # Core holds the injected-key switch and the fake STT. These are member and type names, so they sit in the
    # metadata as ASCII. (Release has a private const named AcceptInjected, so look for the E2E setter.)
    $core = [Text.Encoding]::GetEncoding(28591).GetString([IO.File]::ReadAllBytes((Join-Path $Path 'Yolime.Core.dll')))
    foreach ($marker in 'set_AcceptInjected', 'FakeTranscriptionSession') {
        if ($core.IndexOf($marker, [StringComparison]::Ordinal) -ge 0) { return "contains E2E-only code (Yolime.Core.dll: $marker)" }
    }
    return $null
}

$problem = Test-YolimePublish $Folder
if ($problem) {
    Write-Host "Not a usable yolime build in ${Folder}: $problem"
    exit 1
}
Write-Host "Publish folder OK: $Folder"
exit 0
