using Yolime.Core.History;
using Yolime.Core.Settings;

namespace Yolime.Core.Tests.History;

public class HistoryItemTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private static HistoryRow Row(
        DateTimeOffset? at = null, Mode mode = Mode.Dictate, RunOutcome outcome = RunOutcome.Completed, DeliveryKind delivery = DeliveryKind.Pasted,
        string? output = "Hello there", string? raw = "hello there", string? context = null, string? wav = null) => new()
    {
        Id = 7,
        RunId = Guid.NewGuid(),
        StartedUtc = at ?? Now.AddHours(-1),
        Mode = mode,
        ProcessName = "OUTLOOK",
        Output = output,
        RawText = raw,
        Context = context,
        Outcome = outcome,
        Delivery = delivery,
        PendingAudioFile = wav,
    };

    private static HistoryItem Item(HistoryRow row) => HistoryItem.From(row, p => p == "OUTLOOK" ? "Outlook" : p ?? "", Now, TimeZoneInfo.Utc);

    [Theory]
    [InlineData(2026, 10, 28, 9, 12, "Today 09:12")]
    [InlineData(2026, 10, 27, 14, 32, "Yesterday 14:32")]
    [InlineData(2026, 10, 2, 8, 5, "2 Oct 08:05")]
    [InlineData(2025, 12, 30, 23, 0, "30 Dec 2025")]
    public void Times_AreRelative(int y, int mo, int d, int h, int mi, string expected) =>
        Assert.Equal(expected, Item(Row(at: new DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.Zero))).When);

    [Fact]
    public void TheFirstLine_IsShortAndClean()
    {
        Assert.Equal("Hello there", Item(Row(output: "Hello there\nsecond line")).FirstLine);
        Assert.Equal("hidden", Item(Row(output: "\u202ehid\u200bden")).FirstLine);
        Assert.Equal("only raw", Item(Row(output: null, raw: "only raw")).FirstLine);
        Assert.Equal("(no text)", Item(Row(output: null, raw: null)).FirstLine);
        var longLine = Item(Row(output: new string('a', 200))).FirstLine;
        Assert.Equal(80, longLine.Length);
        Assert.EndsWith("\u2026", longLine, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RunOutcome.Completed, DeliveryKind.Pasted, "Pasted", HistorySeverity.Ok)]
    [InlineData(RunOutcome.Completed, DeliveryKind.Copied, "Copied", HistorySeverity.Ok)]
    [InlineData(RunOutcome.CompletedWithFallback, DeliveryKind.Copied, "Copied the transcript (Claude failed)", HistorySeverity.Warning)]
    [InlineData(RunOutcome.SttFailed, DeliveryKind.None, "Failed: transcription", HistorySeverity.Error)]
    [InlineData(RunOutcome.NoSpeech, DeliveryKind.None, "No speech", HistorySeverity.Warning)]
    [InlineData(RunOutcome.Cancelled, DeliveryKind.None, "Cancelled", HistorySeverity.Warning)]
    [InlineData(RunOutcome.Interrupted, DeliveryKind.None, "Interrupted", HistorySeverity.Error)]
    public void Statuses_AreWords(RunOutcome outcome, DeliveryKind delivery, string label, HistorySeverity severity)
    {
        var item = Item(Row(outcome: outcome, delivery: delivery));
        Assert.Equal(label, item.Status);
        Assert.Equal(severity, item.Severity);
    }

    [Fact]
    public void ReRuns_AreOfferedInTheOtherModes_ReplyOnlyWithAMessage()
    {
        Assert.Equal([Mode.Prompt, Mode.Raw], Item(Row(mode: Mode.Dictate)).RerunModes);
        Assert.Equal([Mode.Dictate, Mode.Prompt, Mode.Raw], Item(Row(mode: Mode.Reply, context: "see you Friday?")).RerunModes);
        Assert.Empty(Item(Row(raw: null)).RerunModes); // nothing transcribed, nothing to re-run
    }

    [Fact]
    public void Actions_FollowWhatTheRunHas()
    {
        var failed = Item(Row(outcome: RunOutcome.SttFailed, delivery: DeliveryKind.None, output: null, raw: null, wav: "x.wav"));
        Assert.True(failed.CanRetryAudio);
        Assert.True(failed.RecordingKept);
        Assert.False(failed.CanCopyOutput);
        Assert.False(failed.CanReinsert);

        var done = Item(Row());
        Assert.False(done.CanRetryAudio);
        Assert.True(done.CanCopyOutput);
        Assert.True(done.CanCopyRaw);
        Assert.True(done.CanReinsert);
        Assert.False(done.HasContext);
    }

    [Fact]
    public void AReRun_SaysSoToNarrator() =>
        Assert.Equal("Today 11:00, Dictate, re-run, Outlook, Pasted, Hello there", Item(Row() with { RerunOf = 3 }).AccessibleName);

    [Fact]
    public void TheNarratorName_SaysItAll() =>
        Assert.Equal("Today 11:00, Dictate, Outlook, Pasted, Hello there", Item(Row()).AccessibleName);
}

public class WordDiffTests
{
    [Fact]
    public void ChangedWords_AreMarked()
    {
        var d = WordDiff.Compute("the cat sat down", "the dog sat down");
        Assert.Equal(
            [new DiffSegment(DiffKind.Same, "the"), new DiffSegment(DiffKind.Removed, "cat"), new DiffSegment(DiffKind.Added, "dog"), new DiffSegment(DiffKind.Same, "sat down")],
            d.Segments);
        Assert.Equal((1, 1), (d.RemovedWords, d.AddedWords));
        Assert.False(d.TooLong);
        Assert.Equal("1 word removed, 1 added", d.Summary);
    }

    [Fact]
    public void IdenticalTexts_HaveNoChanges()
    {
        var d = WordDiff.Compute("same words here", "same words here");
        Assert.Equal([new DiffSegment(DiffKind.Same, "same words here")], d.Segments);
        Assert.Equal("No changes", d.Summary);
    }

    [Fact]
    public void FromNothing_EverythingIsAdded()
    {
        var d = WordDiff.Compute("", "new text");
        Assert.Equal([new DiffSegment(DiffKind.Added, "new text")], d.Segments);
        Assert.Equal("2 words added", d.Summary);
    }

    [Fact]
    public void LongTexts_AreNotCompared()
    {
        var big = string.Join(" ", Enumerable.Range(0, WordDiff.MaxTokens + 1));
        var d = WordDiff.Compute(big, big + " x");
        Assert.True(d.TooLong);
        Assert.Empty(d.Segments);
        Assert.Equal("Too long to compare", d.Summary);
    }
}

public class VocabularyAddTests
{
    [Theory]
    [InlineData("Kolb,", "Kolb")]
    [InlineData("  \"yolime\"  ", "yolime")]
    [InlineData("\u200bKubernetes", "Kubernetes")]
    [InlineData("Claude Code", "Claude Code")]
    public void ATerm_IsCleanedAndAdded(string typed, string saved)
    {
        var (s, result) = SettingsEdits.AddVocabulary(new YolimeSettings(), typed);
        Assert.Equal(VocabularyAdd.Added, result);
        Assert.Equal([saved], s.Vocabulary);
    }

    [Fact]
    public void ATermAlreadyThere_IsntAddedTwice()
    {
        var start = new YolimeSettings { Vocabulary = ["Kolb"] };
        var (s, result) = SettingsEdits.AddVocabulary(start, "kolb");
        Assert.Equal(VocabularyAdd.AlreadyThere, result);
        Assert.Same(start, s);
    }

    [Theory]
    [InlineData("", VocabularyAdd.Empty)]
    [InlineData(" ,. ", VocabularyAdd.Empty)]
    [InlineData("two\nlines", VocabularyAdd.MultiLine)]
    [InlineData("an entire sentence that someone selected by mistake here", VocabularyAdd.TooLong)]
    public void BadTerms_AreRefused(string typed, VocabularyAdd expected)
    {
        var start = new YolimeSettings();
        var (s, result) = SettingsEdits.AddVocabulary(start, typed);
        Assert.Equal(expected, result);
        Assert.Same(start, s);
    }

    [Fact]
    public void AFullList_SaysSo()
    {
        var full = new YolimeSettings { Vocabulary = Enumerable.Range(0, SettingsEdits.MaxVocabularyTerms).Select(i => "t" + i).ToList() };
        Assert.Equal(VocabularyAdd.Full, SettingsEdits.AddVocabulary(full, "one more").Result);
    }
}

public class HistorySearchTests
{
    private static HistoryRow Row(long id, string? output, string? raw = null, string? process = null) => new()
    {
        Id = id,
        RunId = Guid.NewGuid(),
        StartedUtc = DateTimeOffset.UnixEpoch,
        Mode = Mode.Dictate,
        Output = output,
        RawText = raw,
        ProcessName = process,
    };

    [Fact]
    public void Search_LooksAtOutputRawAndTheAppsName_IgnoringCase()
    {
        var rows = new[]
        {
            Row(1, "Meeting at NOON"),
            Row(2, null, raw: "meeting notes"),
            Row(3, "unrelated", process: "Code"),
            Row(4, "\u00c9t\u00e9 plans"),
        };
        string Display(string? p) => p == "Code" ? "Visual Studio Code" : p ?? "";
        Assert.Equal([1L, 2L], HistorySearch.Filter(rows, "meeting", Display).Select(r => r.Id));
        Assert.Equal([3L], HistorySearch.Filter(rows, "visual studio", Display).Select(r => r.Id));
        Assert.Equal([4L], HistorySearch.Filter(rows, "\u00e9T\u00c9", Display).Select(r => r.Id));
        Assert.Equal(4, HistorySearch.Filter(rows, "  ", Display).Count);
    }

    [Fact]
    public void Search_IsCapped()
    {
        var rows = Enumerable.Range(0, 600).Select(i => Row(i, "match")).ToList();
        Assert.Equal(HistorySearch.MaxHits, HistorySearch.Filter(rows, "match", p => p ?? "").Count);
    }
}
