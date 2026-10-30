# Latency report from yolime's timing log: p50/p90 per mode and stage, against the plan's targets.
# The log holds numbers only (milliseconds, byte and character counts), never text, so this is safe to share.
# Usage (pwsh):  ./tools/timing-report.ps1                # the last 7 days on this machine
#                ./tools/timing-report.ps1 -Days 1
#                ./tools/timing-report.ps1 -LogsDir artifacts/e2e-results/logs/e2edata
param(
    [int]$Days = 7,
    [string]$LogsDir = (Join-Path $env:LOCALAPPDATA 'Yolime\logs')
)
$ErrorActionPreference = 'Stop'

function Percentile([double[]]$values, [double]$p) {
    if ($values.Count -eq 0) { return $null }
    $sorted = $values | Sort-Object
    $index = [math]::Ceiling($p / 100 * $sorted.Count) - 1
    return [math]::Round($sorted[[math]::Max(0, $index)])
}

# Release-to-paste targets from docs/plan.md ("Latency / cost targets"), as the upper bound in ms.
$targets = @{ Dictate = 1000; Prompt = 4000; Reply = 3000 }

$since = (Get-Date).ToUniversalTime().AddDays(-$Days)
$files = Get-ChildItem -Path $LogsDir -Filter 'timings-*.jsonl' -ErrorAction SilentlyContinue
if (-not $files) { Write-Host "No timing logs in $LogsDir"; exit 1 }

$all = @(foreach ($f in $files) {
    foreach ($line in Get-Content $f.FullName) {
        if (-not $line.Trim()) { continue }
        $t = $line | ConvertFrom-Json
        if ([datetime]$t.StartedUtc -lt $since) { continue }
        $t
    }
})
$runs = @($all | Where-Object { $_.Outcome -in 'Completed', 'CompletedWithFallback' })
if (-not $runs) { Write-Host "No completed runs in the last $Days days."; exit 0 }

"Completed runs since $($since.ToString('u')): $(@($runs).Count)"
""
foreach ($group in $runs | Group-Object Mode | Sort-Object Name) {
    $r = $group.Group
    $paste = @($r | Where-Object { $null -ne $_.OutputDoneMs } | ForEach-Object { [double]$_.OutputDoneMs })
    $p50 = Percentile $paste 50
    $p90 = Percentile $paste 90
    $target = $targets[$group.Name]
    $verdict = if ($target -and $null -ne $p50) { if ($p50 -le $target) { 'within target' } else { "over the $target ms target" } } else { '' }
    "{0} ({1} runs): release to paste p50 {2} ms, p90 {3} ms  {4}" -f $group.Name, $r.Count, $p50, $p90, $verdict
    foreach ($field in 'SttDoneMs', 'RewriteFirstTokenMs', 'RewriteDoneMs') {
        $v = @($r | Where-Object { $null -ne $_.$field } | ForEach-Object { [double]$_.$field })
        if ($v.Count) { "    {0,-22} p50 {1,6} ms   p90 {2,6} ms" -f $field, (Percentile $v 50), (Percentile $v 90) }
    }
    foreach ($m in $r | Where-Object { $_.RewriteModel } | Group-Object RewriteModel) {
        $v = @($m.Group | Where-Object { $null -ne $_.OutputDoneMs } | ForEach-Object { [double]$_.OutputDoneMs })
        "    with {0,-8} ({1,3} runs)  release to paste p50 {2} ms" -f $m.Name, $m.Count, (Percentile $v 50)
    }
    # Streamed uploads open their connection while the user speaks, so their cold count isn't a cost.
    foreach ($u in $r | Where-Object { $_.SttUpload } | Group-Object SttUpload | Sort-Object Name) {
        $v = @($u.Group | Where-Object { $null -ne $_.OutputDoneMs } | ForEach-Object { [double]$_.OutputDoneMs })
        $s = @($u.Group | Where-Object { $null -ne $_.SttDoneMs } | ForEach-Object { [double]$_.SttDoneMs })
        $b = @($u.Group | Where-Object { $null -ne $_.SttBodySentMs } | ForEach-Object { [double]$_.SttBodySentMs })
        $c = @($u.Group | Where-Object { $_.NewConnection }).Count
        "    {0,-15} ({1,3} runs)  release to paste p50 {2} ms, body sent p50 {3} ms, STT done p50 {4} ms, cold {5}" -f $u.Name, $u.Count, (Percentile $v 50), (Percentile $b 50), (Percentile $s 50), $c
    }
    $cold = @($r | Where-Object { $_.NewConnection }).Count
    "    cold STT connections   {0} of {1}" -f $cold, $r.Count
    $withBreakdown = @($r | Where-Object { $_.Output })
    if ($withBreakdown.Count) {
        $parts = foreach ($name in 'GateWaitMs', 'ModifierWaitMs', 'SnapshotMs', 'FlushMs', 'ClipboardSaveMs', 'ClipboardSetMs', 'PasteMs') {
            "{0} {1}" -f ($name -replace 'Ms$', ''), (Percentile @($withBreakdown | ForEach-Object { [double]$_.Output.$name }) 50)
        }
        "    paste steps (p50 ms)   " + ($parts -join ', ')
    }
    # Stalls are rare, so medians hide them: the tail and a count of the runs that hit one. Older lines lack the field.
    $opens = @($withBreakdown | Where-Object { $null -ne $_.Output.ClipboardOpenMs })
    if ($opens.Count) {
        $v = @($opens | ForEach-Object { [double]$_.Output.ClipboardOpenMs })
        $retried = @($opens | Where-Object { $_.Output.ClipboardOpenRetries -gt 0 }).Count
        "    clipboard open         p50 {0} ms, p90 {1} ms, max {2} ms; retried in {3} of {4} pastes" -f (Percentile $v 50), (Percentile $v 90), (Percentile $v 100), $retried, $opens.Count
    }
    $hist = @($r | Where-Object { $null -ne $_.HistoryBeforePasteMs })
    if ($hist.Count) {
        $v = @($hist | ForEach-Object { [double]$_.HistoryBeforePasteMs })
        $slow = @($hist | Where-Object { $_.HistoryBeforePasteMs -gt 50 }).Count
        "    history before paste   p50 {0} ms, p90 {1} ms, max {2} ms; over 50 ms in {3} of {4} runs" -f (Percentile $v 50), (Percentile $v 90), (Percentile $v 100), $slow, $hist.Count
        # The whole run's writes too: the ones after the paste don't delay it, but a slow disk shows here first.
        $w = @($hist | ForEach-Object { [double]$_.HistoryWriteMs })
        "    history, whole run     p50 {0} ms, p90 {1} ms, max {2} ms" -f (Percentile $w 50), (Percentile $w 90), (Percentile $w 100)
    }
    ""
}

# Key-down to the mic's first buffer, per kind of mic: what decides whether keeping the mic open is worth it. Every
# hold counts, whatever its outcome: a slow mic is exactly what turns a hold into NoSpeech. Older lines and retries
# have no value and are left out.
$mic = @($all | Where-Object { $null -ne $_.MicFirstAudioMs -and $_.MicKind })
if ($mic.Count) {
    "Mic start (key-down to first audio):"
    foreach ($kind in $mic | Group-Object MicKind | Sort-Object Name) {
        $v = @($kind.Group | ForEach-Object { [double]$_.MicFirstAudioMs })
        "    {0,-10} ({1,3} holds)  p50 {2,5} ms   p90 {3,5} ms" -f $kind.Name, $kind.Count, (Percentile $v 50), (Percentile $v 90)
    }
}
