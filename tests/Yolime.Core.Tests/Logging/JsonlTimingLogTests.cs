using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Yolime.Core.Logging;

namespace Yolime.Core.Tests.Logging;

public sealed class JsonlTimingLogTests : IDisposable
{
    private readonly TempRoot _root = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _root.Dispose();

    private static RunTiming Sample(Guid id) => new()
    {
        RunId = id,
        Mode = Mode.Dictate,
        StartedUtc = new DateTimeOffset(2026, 10, 27, 11, 59, 0, TimeSpan.Zero),
        Outcome = RunOutcome.Completed,
        CaptureMs = 2100,
        AudioBytes = 67200,
        Provider = SttProviderKind.OpenAi,
        NewConnection = true,
        SttBodySentMs = 40,
        SttFirstByteMs = 610,
        SttDoneMs = 640,
        OutputDoneMs = 700,
        Stages = new Dictionary<Stage, StageStatus> { [Stage.Capture] = StageStatus.Ok, [Stage.Stt] = StageStatus.Ok },
        TranscriptChars = 44,
        OutputChars = 44,
        OutputWords = 9,
    };

    private string[] Lines() =>
        Directory.GetFiles(_root.Paths.LogsDir, "timings-*.jsonl").SelectMany(File.ReadAllLines).ToArray();

    [Fact]
    public void TimingLog_AppendsOneJsonObjectPerLine_RoundTrips()
    {
        var log = new JsonlTimingLog(_root.Paths, _time);
        var a = Sample(Guid.NewGuid());
        var b = Sample(Guid.NewGuid());
        log.Append(a);
        log.Append(b);

        var lines = Lines();
        Assert.Equal(2, lines.Length);
        var back = JsonlTimingLog.Parse(lines[0]);
        Assert.Equal(a.RunId, back.RunId);
        Assert.Equal(a.SttFirstByteMs, back.SttFirstByteMs);
        Assert.Equal(a.Stages, back.Stages);
        Assert.Contains("\"Dictate\"", lines[0]);
    }

    [Fact]
    public void TimingLog_WritesTheMicFields_AsANumberAndAName()
    {
        // tools/timing-report.ps1 groups by the kind's name.
        var log = new JsonlTimingLog(_root.Paths, _time);
        log.Append(Sample(Guid.NewGuid()) with { MicFirstAudioMs = 950, MicKind = Yolime.Core.Audio.MicKind.Bluetooth });
        var line = Assert.Single(Lines());
        Assert.Contains("\"MicFirstAudioMs\":950", line);
        Assert.Contains("\"MicKind\":\"Bluetooth\"", line);
        Assert.Equal(950, JsonlTimingLog.Parse(line).MicFirstAudioMs);
    }

    [Fact]
    public async Task TimingLog_ConcurrentAppends_ProduceWholeLines()
    {
        var log = new JsonlTimingLog(_root.Paths, _time);
        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => log.Append(Sample(Guid.NewGuid())))));

        var lines = Lines();
        Assert.Equal(50, lines.Length);
        foreach (var line in lines) JsonDocument.Parse(line).Dispose();
    }

    [Fact]
    public void TimingLog_RotatesAfter30Days()
    {
        Directory.CreateDirectory(_root.Paths.LogsDir);
        var old = Path.Combine(_root.Paths.LogsDir, "timings-20260926.jsonl");
        var kept = Path.Combine(_root.Paths.LogsDir, "timings-20260928.jsonl");
        File.WriteAllText(old, "{}\n");
        File.WriteAllText(kept, "{}\n");

        _ = new JsonlTimingLog(_root.Paths, _time);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(kept));
    }

    [Fact]
    public void ALineFromBeforeTheOpenAndHistoryFields_StillParses()
    {
        var old = """{"RunId":"00000000-0000-0000-0000-000000000001","Mode":"Dictate","StartedUtc":"2026-10-27T11:59:00+00:00","Outcome":"Completed","Output":{"GateWaitMs":1,"ModifierWaitMs":2,"SnapshotMs":3,"FlushMs":4,"ClipboardSaveMs":5,"ClipboardSetMs":6,"PasteMs":7}}""";
        var t = JsonlTimingLog.Parse(old);
        Assert.Equal(0, t.HistoryWriteMs);
        Assert.Null(t.HistoryBeforePasteMs);
        Assert.Equal(new OutputBreakdown(1, 2, 3, 4, 5, 6, 7, 0, 0), t.Output);
    }

    [Fact]
    public void TheOpenAndHistoryFields_RoundTrip()
    {
        var t = Sample(Guid.NewGuid()) with
        {
            HistoryWriteMs = 12,
            HistoryBeforePasteMs = 8,
            Output = new OutputBreakdown(1, 2, 3, 4, 5, 6, 7, ClipboardOpenMs: 40, ClipboardOpenRetries: 2),
        };
        new JsonlTimingLog(_root.Paths, _time).Append(t);
        var back = JsonlTimingLog.Parse(Lines().Single());
        Assert.Equal(12, back.HistoryWriteMs);
        Assert.Equal(8, back.HistoryBeforePasteMs);
        Assert.Equal(t.Output, back.Output);
    }
}
