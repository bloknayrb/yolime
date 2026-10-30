using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace Yolime.E2E;

/// <summary>PR6: the History window, driven through UI Automation.</summary>
internal sealed partial class Scenarios
{
    private const string HistoryTitle = "yolime history";
    private const int CmdHistory = 8;

    public IEnumerable<Scenario> History() =>
    [
        new("History_Opens_NewestFirst_WithNamedRows", HistoryOpens),
        new("History_CopyOutput", HistoryCopyOutput),
        new("History_AddWord_SavesVocabulary", HistoryAddWord),
        new("History_RerunAsRaw_CopiesTheTranscript", HistoryRerun),
        new("History_LiveRefresh_ShowsANewRun", HistoryLiveRefresh),
        new("History_Reinsert_PastesIntoTheLastApp", HistoryReinsert),
        new("History_Delete_RemovesTheRow", HistoryDelete),
        new("History_RowMenu_ActsOnTheClickedRow", HistoryRowMenu),
        new("Latency_PasteAfterRewrite_Under300ms", PasteLatency),
    ];

    /// <summary>
    /// A regression guard, not the targets (those need live keys on the real desktop): once the text exists, putting it
    /// at the cursor takes well under 300 ms (median of the runs so far). PR7 found 5 s here, from the harness.
    /// </summary>
    private static string? PasteLatency()
    {
        var log = Directory.GetFiles(@"C:\e2edata\local\logs", "timings-*.jsonl").FirstOrDefault();
        if (log is null) return "no timing log";
        var gaps = new List<long>();
        using (var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
        {
            while (reader.ReadLine() is { } line)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(line);
                var t = doc.RootElement;
                if (t.GetProperty("Outcome").GetString() != "Completed" || !t.TryGetProperty("Output", out var o) || o.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                if (t.TryGetProperty("OutputDoneMs", out var done) && done.ValueKind == System.Text.Json.JsonValueKind.Number
                    && t.TryGetProperty("RewriteDoneMs", out var rw) && rw.ValueKind == System.Text.Json.JsonValueKind.Number)
                    gaps.Add(done.GetInt64() - rw.GetInt64());
            }
        }
        if (gaps.Count < 5) return $"only {gaps.Count} pasted runs with timings";
        gaps.Sort();
        var median = gaps[gaps.Count / 2];
        return median < 300 ? null : $"median paste after rewrite is {median} ms over {gaps.Count} runs";
    }

    private string? Dictate()
    {
        if (Prepare(form.Box1) is { } fail) return fail;
        var runs = RunCount();
        Input.Hold(Input.RCtrl, 1500);
        if (!WaitRun(runs)) return "no run finished";
        return Program.Wait(() => form.On(() => form.Box1.Text) == Transcript, 4000) ? null : "the dictation never pasted";
    }

    private static AutomationElement? OpenHistory(UIA3Automation automation)
    {
        if (TrayCommand(CmdHistory) is not null) return null;
        AutomationElement? window = null;
        Program.Wait(() => (window = automation.GetDesktop().FindFirstChild(cf => cf.ByName(HistoryTitle))) is not null, 8000);
        if (window is null) return null;
        Thread.Sleep(1200); // the first page loads off the UI thread
        return window;
    }

    private static AutomationElement[] Rows(AutomationElement window) =>
        window.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem));

    private static AutomationElement? Named(AutomationElement window, Func<string, bool> name) =>
        window.FindAllDescendants().FirstOrDefault(e => e.Properties.Name.ValueOrDefault is { } n && name(n));

    private static void SelectRow(AutomationElement row)
    {
        if (row.Patterns.SelectionItem.IsSupported) row.Patterns.SelectionItem.Pattern.Select();
        Thread.Sleep(500);
    }

    private string? WithHistory(Func<AutomationElement, string?> body)
    {
        using var automation = new UIA3Automation();
        if (OpenHistory(automation) is not { } window) return "History never opened";
        try
        {
            return body(window);
        }
        finally
        {
            CloseQuietly(window);
            Thread.Sleep(600);
        }
    }

    private string? HistoryOpens()
    {
        if (Dictate() is { } fail) return fail;
        if (Dictate() is { } fail2) return fail2;
        return WithHistory(window =>
        {
            var rows = Rows(window);
            if (rows.Length < 2) return $"only {rows.Length} rows";
            // Each row is named for Narrator with when, mode, app, status and its first line.
            var first = rows[0].Properties.Name.ValueOrDefault ?? "";
            if (!first.StartsWith("Today ", StringComparison.Ordinal) || !first.Contains("Dictate", StringComparison.Ordinal) || !first.Contains("Pasted", StringComparison.Ordinal))
                return $"first row is named '{first}'";
            return null;
        });
    }

    private string? HistoryCopyOutput()
    {
        SetClip(Sentinel);
        return WithHistory(window =>
        {
            var rows = Rows(window);
            if (rows.Length == 0) return "no rows";
            SelectRow(rows[0]);
            if (Named(window, n => n == "Copy output") is not { } copy) return "no Copy output";
            Activate(copy);
            return Program.Wait(() => ClipText() == Transcript, 3000) ? null : $"clipboard is '{ClipText()}'";
        });
    }

    /// <summary>
    /// The row menu acts on the row it was opened on, and selects it first: open it (Shift+F10) on a row that isn't the
    /// selected one, Copy output, and that row is now selected with its output on the clipboard.
    /// </summary>
    private string? HistoryRowMenu()
    {
        // Two fresh runs, so the second row is one whose output is known (an earlier scenario's Raw re-run would be).
        if (Dictate() is { } fail) return fail;
        if (Dictate() is { } fail2) return fail2;
        SetClip(Sentinel);
        return WithHistory(window =>
        {
            var rows = Rows(window);
            if (rows.Length < 2) return $"only {rows.Length} rows";
            SelectRow(rows[0]);
            rows[1].Focus();
            Thread.Sleep(300);
            if (rows[1].Patterns.SelectionItem.Pattern.IsSelected.Value) return "focusing the row already selected it: the test can't tell";
            Input.Down(Input.LShift);
            Input.Tap(Input.F10);
            Input.Up(Input.LShift);
            AutomationElement? copy = null;
            if (!Program.Wait(() => (copy = window.Automation.GetDesktop().FindFirstDescendant(cf => cf.ByName("Copy output").And(cf.ByControlType(ControlType.MenuItem)))) is not null, 3000))
                return "the row menu never opened";
            Activate(copy!);
            if (!Program.Wait(() => ClipText() == Transcript, 3000)) return $"clipboard is '{ClipText()}'";
            return Program.Wait(() => Rows(window)[1].Patterns.SelectionItem.Pattern.IsSelected.Value, 2000) ? null : "the menu didn't select its row";
        });
    }

    private string? HistoryAddWord() => WithHistory(window =>
    {
        var rows = Rows(window);
        if (rows.Length == 0) return "no rows";
        SelectRow(rows[0]);
        if (Named(window, n => n.StartsWith("Add word", StringComparison.Ordinal)) is not { } add) return "no Add word";
        Activate(add);
        if (!Program.Wait(() => Named(window, n => n == "Word to add to the vocabulary") is not null, 3000)) return "the Add word box never opened";
        var box = Named(window, n => n == "Word to add to the vocabulary")!;
        if (!box.Patterns.Value.IsSupported) return "the word box can't be typed into";
        box.Patterns.Value.Pattern.SetValue("Kolbington,");
        if (window.FindAllDescendants(cf => cf.ByName("Add")).FirstOrDefault() is not { } confirm) return "no Add button";
        Activate(confirm);
        return Program.Wait(() => Setting("vocabulary") is { } v && v.ToString().Contains("\"Kolbington\"", StringComparison.Ordinal), 3000)
            ? null : $"vocabulary is {Setting("vocabulary")}";
    });

    private string? HistoryRerun()
    {
        SetClip(Sentinel);
        return WithHistory(window =>
        {
            var rows = Rows(window);
            if (rows.Length == 0) return "no rows";
            SelectRow(rows[0]);
            var before = Rows(window).Length;
            if (Named(window, n => n == "Re-run as another mode") is not { } rerun) return "no Re-run";
            if (rerun.Patterns.ExpandCollapse.IsSupported) rerun.Patterns.ExpandCollapse.Pattern.Expand();
            else Activate(rerun);
            AutomationElement? raw = null;
            if (!Program.Wait(() => (raw = Named(window, n => n == "Raw (no AI)") ?? automationRoot(window)) is not null, 3000)) return "no Raw item";
            Activate(raw!);
            if (!Program.Wait(() => ClipText() == Spoken, 6000)) return $"clipboard is '{ClipText()}'";
            return Program.Wait(() => Rows(window).Length > before, 4000) ? null : "the re-run's row never appeared";
        });

        // A flyout's items live in a popup that isn't the window's descendant.
        static AutomationElement? automationRoot(AutomationElement window) =>
            window.Automation.GetDesktop().FindAllDescendants().FirstOrDefault(e => e.Properties.Name.ValueOrDefault == "Raw (no AI)");
    }

    private string? HistoryLiveRefresh()
    {
        using var automation = new UIA3Automation();
        if (OpenHistory(automation) is not { } window) return "History never opened";
        try
        {
            var before = Rows(window).Length;
            if (Dictate() is { } fail) return fail;
            return Program.Wait(() => Rows(window).Length > before, 5000) ? null : "the new run never appeared in the open window";
        }
        finally
        {
            CloseQuietly(window);
            Thread.Sleep(600);
        }
    }

    private string? HistoryReinsert()
    {
        // The form becomes the last app window; History opens over it; Re-insert pastes back into it.
        if (Prepare(form.Box1) is { } fail) return fail;
        return WithHistory(window =>
        {
            var rows = Rows(window);
            if (rows.Length == 0) return "no rows";
            SelectRow(rows[0]);
            if (Named(window, n => n.StartsWith("Re-insert", StringComparison.Ordinal)) is not { } reinsert) return "no Re-insert";
            Activate(reinsert);
            return Program.Wait(() => form.On(() => form.Box1.Text) == Transcript, 6000) ? null : $"box1 is '{form.On(() => form.Box1.Text)}'";
        });
    }

    private string? HistoryDelete() => WithHistory(window =>
    {
        var rows = Rows(window);
        if (rows.Length == 0) return "no rows";
        var name = rows[0].Properties.Name.ValueOrDefault;
        SelectRow(rows[0]);
        if (Named(window, n => n == "Delete") is not { } delete) return "no Delete";
        var count = rows.Length;
        Activate(delete);
        // A run with re-runs asks first; confirm if it does.
        if (Program.Wait(() => Named(window, n => n == "Delete all") is not null, 1500))
            Activate(Named(window, n => n == "Delete all")!);
        return Program.Wait(() => Rows(window).Length < count, 4000) ? null : $"still {Rows(window).Length} rows (was {count}, deleting '{name}')";
    });
}
