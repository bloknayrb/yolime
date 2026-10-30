using System.Globalization;
using Yolime.Core.Clipboard;

namespace Yolime.Core.History;

public enum HistorySeverity { Ok, Warning, Error }

/// <summary>
/// One row of the History window's list, and what can be done with it. Pure, so every label and every enable rule is
/// tested here and the window only draws it.
/// </summary>
public sealed record HistoryItem(
    long Id,
    string When,
    Mode Mode,
    string App,
    string FirstLine,
    string Status,
    HistorySeverity Severity,
    long? RerunOf,
    bool CanCopyOutput,
    bool CanCopyRaw,
    bool CanReinsert,
    bool CanRetryAudio,
    bool RecordingKept,
    bool HasContext,
    IReadOnlyList<Mode> RerunModes,
    bool OutputMatchesTranscript = false)
{
    public const int FirstLineLength = 80;

    private static readonly RunOutcome[] Retryable = [RunOutcome.SttFailed, RunOutcome.CaptureFailed, RunOutcome.NoSpeech, RunOutcome.Interrupted, RunOutcome.Cancelled];

    /// <summary>What Narrator reads for the row: everything the list shows, in order.</summary>
    public string AccessibleName => $"{When}, {Mode}, {(RerunOf is null ? "" : "re-run, ")}{App}, {Status}, {FirstLine}";

    public static HistoryItem From(HistoryRow row, Func<string?, string> displayName, DateTimeOffset now, TimeZoneInfo zone)
    {
        var (status, severity) = StatusOf(row);
        var hasRaw = !string.IsNullOrWhiteSpace(row.RawText);
        var hasOutput = !string.IsNullOrWhiteSpace(row.Output);
        var hasContext = !string.IsNullOrEmpty(row.Context);
        var kept = row.PendingAudioFile is not null && !row.AudioExpired;
        return new HistoryItem(
            row.Id,
            RelativeTime(row.StartedUtc, now, zone),
            row.Mode,
            displayName(row.ProcessName) is { Length: > 0 } name ? name : row.ProcessName ?? "",
            FirstLineOf(row.Output ?? row.RawText),
            status,
            severity,
            row.RerunOf,
            CanCopyOutput: hasOutput,
            CanCopyRaw: hasRaw,
            CanReinsert: hasOutput,
            CanRetryAudio: kept && Retryable.Contains(row.Outcome),
            RecordingKept: kept,
            HasContext: hasContext,
            // Reply needs a message to answer, and nothing can be re-run without a transcript.
            RerunModes: !hasRaw ? [] : Enum.GetValues<Mode>().Where(m => m != row.Mode && (m != Mode.Reply || hasContext)).ToList(),
            // One box is enough when they're the same text, but not after a failed rewrite: that fallback must stay visible.
            OutputMatchesTranscript: hasOutput && hasRaw && row.Output == row.RawText && !row.FallbackUsed);
    }

    /// <summary>"Today 09:12", "Yesterday 14:32", "2 Oct 08:05", or "30 Dec 2025" for another year.</summary>
    public static string RelativeTime(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (local.Date == today) return "Today " + time;
        if (local.Date == today.AddDays(-1)) return "Yesterday " + time;
        return local.Year == today.Year
            ? local.ToString("d MMM ", CultureInfo.InvariantCulture) + time
            : local.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
    }

    private static string FirstLineOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(no text)";
        var line = ContextFilter.StripInvisible(text)
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? "";
        line = string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (line.Length == 0) return "(no text)";
        return line.Length <= FirstLineLength ? line : line[..(FirstLineLength - 1)] + "\u2026";
    }

    private static (string, HistorySeverity) StatusOf(HistoryRow row) => row.Outcome switch
    {
        RunOutcome.Completed => (row.Delivery is DeliveryKind.Copied ? "Copied" : "Pasted", HistorySeverity.Ok),
        RunOutcome.CompletedWithFallback => ("Copied the transcript (Claude failed)", HistorySeverity.Warning),
        RunOutcome.SttFailed => ("Failed: transcription", HistorySeverity.Error),
        RunOutcome.CaptureFailed => ("Failed: microphone", HistorySeverity.Error),
        RunOutcome.RewriteFailed => ("Failed: Claude", HistorySeverity.Error),
        RunOutcome.OutputFailed => ("Failed: paste", HistorySeverity.Error),
        RunOutcome.NoSpeech => ("No speech", HistorySeverity.Warning),
        RunOutcome.Cancelled => ("Cancelled", HistorySeverity.Warning),
        RunOutcome.Discarded => ("Discarded", HistorySeverity.Warning),
        RunOutcome.Interrupted => ("Interrupted", HistorySeverity.Error),
        RunOutcome.InProgress => ("In progress", HistorySeverity.Ok),
        _ => (row.Outcome.ToString(), HistorySeverity.Warning),
    };
}

public enum DiffKind { Same, Removed, Added }

public sealed record DiffSegment(DiffKind Kind, string Text);

/// <param name="Summary">For Narrator and as the pane's caption, since strike-through and underline aren't read out.</param>
public sealed record DiffResult(IReadOnlyList<DiffSegment> Segments, int RemovedWords, int AddedWords, bool TooLong, string Summary);

/// <summary>Word-level diff from the raw transcript to the output: what the rewrite changed.</summary>
public static class WordDiff
{
    /// <summary>Above this many words a side, the table would be too big to build on a click.</summary>
    public const int MaxTokens = 2000;

    public static DiffResult Compute(string before, string after)
    {
        var a = Tokens(before);
        var b = Tokens(after);
        if (a.Length > MaxTokens || b.Length > MaxTokens) return new([], 0, 0, true, "Too long to compare");

        // Longest common subsequence, then walk it to emit removals before additions at each change.
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        for (var j = b.Length - 1; j >= 0; j--)
            lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var tokens = new List<(DiffKind Kind, string Word)>();
        int x = 0, y = 0;
        while (x < a.Length || y < b.Length)
        {
            if (x < a.Length && y < b.Length && a[x] == b[y]) { tokens.Add((DiffKind.Same, a[x])); x++; y++; }
            else if (y >= b.Length || (x < a.Length && lcs[x + 1, y] >= lcs[x, y + 1])) { tokens.Add((DiffKind.Removed, a[x])); x++; }
            else { tokens.Add((DiffKind.Added, b[y])); y++; }
        }

        var segments = new List<DiffSegment>();
        foreach (var group in Runs(tokens)) segments.Add(new DiffSegment(group.Kind, string.Join(' ', group.Words)));
        var removed = tokens.Count(t => t.Kind == DiffKind.Removed);
        var added = tokens.Count(t => t.Kind == DiffKind.Added);
        return new(segments, removed, added, false, Summarise(removed, added));
    }

    private static string[] Tokens(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static IEnumerable<(DiffKind Kind, List<string> Words)> Runs(List<(DiffKind Kind, string Word)> tokens)
    {
        (DiffKind Kind, List<string> Words)? current = null;
        foreach (var (kind, word) in tokens)
        {
            if (current is { } c && c.Kind == kind)
            {
                c.Words.Add(word);
                continue;
            }
            if (current is { } done) yield return done;
            current = (kind, [word]);
        }
        if (current is { } last) yield return last;
    }

    private static string Summarise(int removed, int added)
    {
        static string Words(int n) => n == 1 ? "1 word" : $"{n} words";
        return (removed, added) switch
        {
            (0, 0) => "No changes",
            (_, 0) => Words(removed) + " removed",
            (0, _) => Words(added) + " added",
            _ => $"{Words(removed)} removed, {added} added",
        };
    }
}

/// <summary>The History window's search: in memory, so case folding works beyond ASCII (SQLite's LIKE doesn't).</summary>
public static class HistorySearch
{
    public const int MaxHits = 500;

    /// <summary>Matches the output, the raw transcript and the app's name as the list shows it. Blank matches all.</summary>
    public static IReadOnlyList<HistoryRow> Filter(IEnumerable<HistoryRow> rows, string term, Func<string?, string> displayName)
    {
        var t = term.Trim();
        if (t.Length == 0) return rows.Take(MaxHits).ToList();
        bool Has(string? s) => s is not null && s.Contains(t, StringComparison.OrdinalIgnoreCase);
        return rows.Where(r => Has(r.Output) || Has(r.RawText) || Has(r.ProcessName) || Has(displayName(r.ProcessName))).Take(MaxHits).ToList();
    }
}
