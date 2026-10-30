namespace Yolime.E2E;

/// <summary>
/// The paste is offered by delayed rendering, and the clipboard is given back only after the target has read it
/// (docs/superpowers/plans/2026-10-28-delayed-render.md). None of these read the clipboard while a paste is
/// pending: the harness's own read would render it and end the wait.
/// </summary>
internal sealed partial class Scenarios
{
    /// <summary>A file the E2E app polls for: it then blocks its UI thread for the milliseconds written in it.</summary>
    private const string StallUi = "stall-ui";

    public IEnumerable<Scenario> DelayedRender() =>
    [
        new("SlowReader_GetsTheDictation", SlowReader),
        new("UiThreadStall_DoesNotBlockAPaste", UiThreadStall),
        new("TwoQuickDictations_IntoASlowReader", TwoQuickDictations),
    ];

    /// <summary>The target reads 1.5 s after the paste: it must get the dictation, and the clipboard comes back after.</summary>
    private string? SlowReader()
    {
        if (Prepare(form.Slow) is { } fail) return fail;
        var runs = RunCount();
        Input.Hold(Input.RCtrl, 1500);
        if (!WaitRun(runs)) return "no run finished";
        if (!Program.Wait(() => form.On(() => form.Slow.LastReadMs) >= 0, 8000)) return "the slow box never read the clipboard";
        var got = form.On(() => form.Slow.Text);
        if (got != Transcript) return $"the slow reader got '{got}'";
        return Program.Wait(() => ClipText() == Sentinel, 6000) ? null : $"clipboard not restored: '{ClipText()}'";
    }

    /// <summary>
    /// yolime's UI thread is blocked for 3 s across the slow read: the read is answered by the clipboard-owner thread,
    /// so it still takes well under half a second.
    /// </summary>
    private string? UiThreadStall()
    {
        if (Prepare(form.Slow) is { } fail) return fail;
        var done = Path.Combine(AppResults, StallUi + ".done");
        RetryIo(() => File.Delete(done));
        var runs = RunCount();
        Input.Hold(Input.RCtrl, 1500);
        if (!WaitRun(runs)) return "no run finished";
        File.WriteAllText(Path.Combine(AppResults, StallUi), "3000");
        if (!Program.Wait(() => form.On(() => form.Slow.LastReadMs) >= 0, 8000)) return "the slow box never read the clipboard";
        var (got, ms, readAt) = form.On(() => (form.Slow.Text, form.Slow.LastReadMs, form.Slow.LastReadAt));
        if (got != Transcript) return $"the slow reader got '{got}'";
        if (ms > 500) return $"the read took {ms} ms while yolime's UI thread was stalled";
        // Without this the test could pass with no stall at all: the read must have begun inside it.
        if (!Program.Wait(() => File.Exists(done), 6000)) return "the stall never ran";
        var span = RetryIo(() => File.ReadAllText(done)).Split(' ').Select(long.Parse).ToArray();
        if (readAt < span[0] || readAt > span[1]) return "the read didn't happen during the stall, so this proves nothing";
        return Program.Wait(() => ClipText() == Sentinel, 8000) ? null : $"clipboard not restored: '{ClipText()}'";
    }

    /// <summary>
    /// A second dictation while the first is still unread restores the first at once (ruling 10, accepted): this
    /// records what the slow box ends up with, and checks that the user's clipboard still comes back.
    /// </summary>
    private string? TwoQuickDictations()
    {
        if (Prepare(form.Slow) is { } fail) return fail;
        form.On(() => form.Slow.DelayMs = 4000);
        try
        {
            var runs = RunCount();
            Input.Hold(Input.RCtrl, 1500);
            if (!WaitRun(runs)) return "the first run never finished";
            Input.Hold(Input.RCtrl, 1500);
            if (!WaitRun(runs + 1)) return "the second run never finished";
            // Both pastes' timers fire after the second delivery; the first then reads whatever the clipboard holds.
            Program.Wait(() => form.On(() => form.Slow.Text).Length >= 2 * Transcript.Length, 10_000);
            return Program.Wait(() => ClipText() == Sentinel, 8000) ? null : $"clipboard not restored: '{ClipText()}'";
        }
        finally
        {
            form.On(() => form.Slow.DelayMs = 1500);
        }
    }
}
