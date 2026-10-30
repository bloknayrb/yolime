using System.Text.Json;

namespace Yolime.E2E;

internal sealed record Scenario(string Name, Func<string?> Run);

/// <summary>Each scenario returns null on success or a reason for failure.</summary>
internal sealed partial class Scenarios(TargetForm form)
{
    /// <summary>Twelve words or more, so Dictate goes to the (fake) rewriter rather than local cleanup.</summary>
    public const string Spoken = "please send the weekly report to the whole team before noon tomorrow";

    /// <summary>What the E2E rewriter returns for <see cref="Spoken"/>.</summary>
    private const string Transcript = "rewritten: " + Spoken;
    private const string Sentinel = "sentinel-clipboard";
    private const string AppResults = @"C:\results\app";

    public IEnumerable<Scenario> All() =>
    [
        new("Overlay_DrawsWithoutTakingFocus", OverlayDraws),
        new("Hold_Pastes", HoldPastes),
        new("ShortTap_Silent", ShortTapSilent),
        new("CtrlC_PassesThrough", CtrlCPassesThrough),
        new("Esc_Cancels", EscCancels),
        new("Password_Refused", PasswordRefused),
        new("ReadOnly_Copies", ReadOnlyCopies),
        new("TargetChanged_Copies", TargetChangedCopies),
        new("TapDuringProcessing_Cancels", TapDuringProcessingCancels),
        new("CopyDuringRestore_NotOverwritten", CopyDuringRestore),
        new("Image_Restored", ImageRestored),
        new("PromptViaShift_Rewrites", PromptViaShift),
        new("ReplyFromSelection_UsesIt", ReplyFromSelection),
        new("SecretOnClipboard_Skipped", SecretOnClipboardSkipped),
        new("FreshCopy_UsedAsReplyContext", FreshCopyUsed),
        new("Preview_EnterInserts", () => PreviewKey(Input.Enter, box: Spoken, clip: Sentinel, delivery: "Pasted")),
        new("Preview_EscDiscards", () => PreviewKey(Input.Esc, box: "", clip: Sentinel, delivery: "Discarded")),
        new("Preview_CCopies", () => PreviewKey(Input.C, box: "", clip: Spoken, delivery: "Copied")),
        new("Preview_StrayKeyEndsIt_KeyReachesTheApp", () => PreviewKey(Input.A, box: "a", clip: Sentinel, delivery: "Discarded")),
        new("UseRaw_PillCopies_FocusStays", UseRawPill),
        new("PendingPaste_ClickPastesThere", PendingPaste),
        new("UseRaw_HoldKeyTapCopies", UseRawHoldKeyTap),
        new("Card_EscDismisses_AndReachesTheApp", EscDismissesCard),
    ];

    /// <summary>Hold, tap Shift once (Dictate → Prompt), let go: the prompt is rewritten and pasted.</summary>
    private string? PromptViaShift()
    {
        if (Prepare(form.Box1) is { } fail) return fail;
        var runs = RunCount();
        HoldCycling(cycles: 1);
        if (!WaitRun(runs)) return "no run finished";
        if (LastRun("mode") != "Prompt") return $"mode {LastRun("mode")}";
        if (LastRun("rewriteStage") != "Ok") return $"rewrite {LastRun("rewriteStage")}";
        // Prompt appends the context below the text; a single-line box keeps the first line.
        return Program.Wait(() => form.On(() => form.Box1.Text).StartsWith(Transcript, StringComparison.Ordinal), 4000)
            ? null : $"box1 is '{form.On(() => form.Box1.Text)}'";
    }

    /// <summary>A selected message, two Shift taps (→ Reply): the reply replaces the selection, using it as context.</summary>
    private string? ReplyFromSelection()
    {
        if (Prepare(form.Box1) is { } fail) return fail;
        form.On(() =>
        {
            form.Box1.Text = "Are we still on for lunch Friday?";
            form.Box1.SelectAll();
        });
        var runs = RunCount();
        HoldCycling(cycles: 2);
        if (!WaitRun(runs)) return "no run finished";
        if (LastRun("mode") != "Reply") return $"mode {LastRun("mode")}";
        if (LastRun("contextSource") != "Selection") return $"context {LastRun("contextSource")}";
        if (LastRun("contextChars") != "33") return $"context was {LastRun("contextChars")} chars, not the whole selection";
        return Program.Wait(() => form.On(() => form.Box1.Text) == Transcript, 4000) ? null : $"box1 is '{form.On(() => form.Box1.Text)}'";
    }

    /// <summary>A key on the clipboard is never sent as Reply context, and the user is told.</summary>
    private string? SecretOnClipboardSkipped()
    {
        if (Prepare(form.Box1) is { } fail) return fail;
        SetClip("my key ghp_" + new string('a', 36));
        var runs = RunCount();
        HoldCycling(cycles: 2);
        if (!WaitRun(runs)) return "no run finished";
        if (LastRun("contextSource") is { } source) return $"context was used: {source}";
        if (LastRun("contextNote") != "Skipped") return $"note {LastRun("contextNote")}";
        return Notices().Contains("ContextSkipped") ? null : "no ContextSkipped notice";
    }

    /// <summary>No selection, but the user just copied a message: Reply uses that copy.</summary>
    private string? FreshCopyUsed()
    {
        if (Prepare(form.Box1) is { } fail) return fail;
        SetClip("Can you send the slides?");
        var runs = RunCount();
        HoldCycling(cycles: 2);
        if (!WaitRun(runs)) return "no run finished";
        if (LastRun("contextSource") != "Clipboard") return $"context {LastRun("contextSource")}";
        return LastRun("contextChars") == "24" ? null : $"context was {LastRun("contextChars")} chars";
    }

    /// <summary>
    /// Raw previews in the E2E settings: hold, cycle three times to Raw, let go, then answer the card with a key. The
    /// key must act on the card (or, for a stray key, reach the box), and the clipboard must be exactly as expected.
    /// </summary>
    private string? PreviewKey(ushort key, string box, string clip, string delivery)
    {
        if (Prepare(form.Box1) is { } fail) return fail;
        var runs = RunCount();
        var previews = Count("Preview");
        HoldCycling(cycles: 3);
        // Counted, not just "seen": the overlay log holds every earlier scenario's states too.
        if (!Program.Wait(() => Count("Preview") > previews, 6000)) return "the preview card never showed";
        Thread.Sleep(300);
        Input.Tap(key);
        if (!WaitRun(runs)) return "no run finished";
        if (LastRun("delivery") != delivery) return $"delivery {LastRun("delivery")}";
        Thread.Sleep(1500); // let any paste land and its clipboard restore finish
        if (form.On(() => form.Box1.Text) != box) return $"box1 is '{form.On(() => form.Box1.Text)}'";
        if (ClipText() != clip) return $"clipboard is '{ClipText()}'";
        return null;
    }

    /// <summary>After a rewritten paste, the "Use raw" pill copies the raw transcript, and the user's box keeps focus.</summary>
    private string? UseRawPill()
    {
        if (Prepare(form.Box1) is { } fail) return fail;
        Input.Hold(Input.RCtrl, 1500);
        if (!Program.Wait(() => form.On(() => form.Box1.Text) == Transcript, 8000)) return "never pasted";
        if (!Program.Wait(() => Overlay.FindPill() != 0 && Overlay.Visible(Overlay.FindPill()), 3000)) return "no Use raw pill";
        // Wait for the pill to stop moving: it is placed after it's shown, and a click on its first position misses.
        var last = Overlay.Centre(Overlay.FindPill());
        for (var i = 0; i < 20; i++)
        {
            Thread.Sleep(100);
            var now = Overlay.Centre(Overlay.FindPill());
            if (now == last) break;
            last = now;
        }
        var (x, y) = last;
        Overlay.ClickPhysical(x, y);
        if (!Program.Wait(() => ClipText() == Spoken, 3000)) return $"clipboard is '{ClipText()}'";
        if (!form.IsForeground) return "the pill took the foreground";
        return form.On(() => form.Box1.Focused) ? null : "box1 lost keyboard focus";
    }

    /// <summary>
    /// Bryan's overlay-14: a tap of the hold key copies the raw transcript while the pill is offered, as clicking it does,
    /// and the user's box keeps focus.
    /// </summary>
    private string? UseRawHoldKeyTap()
    {
        if (Prepare(form.Box1) is { } fail) return fail;
        Input.Hold(Input.RCtrl, 1500);
        if (!Program.Wait(() => form.On(() => form.Box1.Text) == Transcript, 8000)) return "never pasted";
        if (!Program.Wait(() => Overlay.FindPill() != 0 && Overlay.Visible(Overlay.FindPill()), 3000)) return "no Copy raw pill";
        Input.Tap(Input.RCtrl);
        if (!Program.Wait(() => ClipText() == Spoken, 3000)) return $"clipboard is '{ClipText()}'";
        if (form.On(() => form.Box1.Text) != Transcript) return "the tap changed the box";
        return form.On(() => form.Box1.Focused) ? null : "box1 lost keyboard focus";
    }

    /// <summary>
    /// Bryan's cards-8: Esc dismisses a card at once (well inside its own timer), and the Esc still reaches the user's app,
    /// since the card never has focus.
    /// </summary>
    private string? EscDismissesCard()
    {
        if (Prepare(form.Box1) is { } fail) return fail;
        var errors = Count("Error");
        File.WriteAllText(Path.Combine(AppResults, "show-card"), "DidntHear - false true");
        if (!Program.Wait(() => Count("Error") > errors, 3000)) return "the card never showed";
        Thread.Sleep(500);
        if (!Overlay.Visible(Overlay.Find())) return "the card isn't visible";
        var escs = form.EscDowns;
        Input.Tap(Input.Esc);
        if (!Program.Wait(() => !Overlay.Visible(Overlay.Find()), 1000)) return "Esc didn't dismiss the card";
        return Program.Wait(() => form.EscDowns > escs, 1000) ? null : "the Esc didn't reach the app";
    }

    /// <summary>A Reply from a selection in the read-only box waits for a click, then pastes where the user clicked.</summary>
    private string? PendingPaste()
    {
        if (Prepare(form.ReadOnlyBox) is { } fail) return fail;
        form.On(() =>
        {
            form.ReadOnlyBox.Text = "Are we still on for lunch?";
            form.ReadOnlyBox.SelectAll();
        });
        var runs = RunCount();
        var pending = Count("PendingPaste");
        HoldCycling(cycles: 2);
        if (!Program.Wait(() => Count("PendingPaste") > pending, 8000)) return "never asked where the reply should go";
        var centre = form.On(() => form.Box2.PointToScreen(new Point(form.Box2.Width / 2, form.Box2.Height / 2)));
        Input.Click(centre.X, centre.Y);
        if (!WaitRun(runs)) return "no run finished";
        if (LastRun("delivery") != "Pasted") return $"delivery {LastRun("delivery")}";
        return Program.Wait(() => form.On(() => form.Box2.Text) == Transcript, 3000) ? null : $"box2 is '{form.On(() => form.Box2.Text)}'";
    }

    /// <summary>How many times the overlay has entered a state. Read with sharing: the app appends as it goes.</summary>
    private static int Count(string state)
    {
        var path = Path.Combine(AppResults, "overlay.txt");
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return 0;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd().Split('\n').Count(l => l.StartsWith(state + " ", StringComparison.Ordinal));
            }
            catch (IOException)
            {
                Thread.Sleep(30);
            }
        }
        return 0;
    }

    private static void HoldCycling(int cycles)
    {
        Input.Down(Input.RCtrl);
        Thread.Sleep(500); // past the 300 ms shortcut window
        for (var i = 0; i < cycles; i++)
        {
            Input.Tap(Input.LShift);
            Thread.Sleep(150);
        }
        Thread.Sleep(900);
        Input.Up(Input.RCtrl);
    }

    /// <summary>
    /// The notch shows during a hold, is actually drawn (its prototype colour at its centre), and the form keeps focus.
    /// The spike's WinUI Window failed exactly this: visible, right size, never painted.
    /// </summary>
    private string? OverlayDraws()
    {
        if (Prepare(form.Box1) is { } fail) return fail;
        var runs = RunCount();
        Input.Down(Input.RCtrl);
        try
        {
            if (!Program.Wait(() => Overlay.Find() != 0 && Overlay.Visible(Overlay.Find()), 3000)) return "overlay never shown";
            Thread.Sleep(600); // let the island compose
            var (x, y) = Overlay.InsideLeftEdge(Overlay.Find());
            var px = Overlay.Pixel(x, y);
            if (!form.IsForeground) return "the overlay took focus";
            Screenshot.Save(Path.Combine(@"C:\results", "overlay-shown.png"));
            // The card's fill is the light (249) or dark (32) theme grey; COLORREF is 0x00BBGGRR.
            int r = (int)(px & 0xFF), g = (int)((px >> 8) & 0xFF), b = (int)((px >> 16) & 0xFF);
            bool Near(int v) => Math.Abs(r - v) <= 6 && Math.Abs(g - v) <= 6 && Math.Abs(b - v) <= 6;
            if (!Near(249) && !Near(32)) return $"overlay not drawn: pixel 0x{px:X6} at {x},{y}";
            return null;
        }
        finally
        {
            Input.Up(Input.RCtrl);
            // The hold becomes a dictation: let it finish (and its paste land) before the next scenario resets the box.
            WaitRun(runs);
            Thread.Sleep(1500);
        }
    }

    private string? HoldPastes()
    {
        if (Prepare(form.Box1) is { } fail) return fail;
        var runs = RunCount();
        Input.Hold(Input.RCtrl, 1500);
        if (!WaitRun(runs)) return "no run finished";
        // Until PR3 there's no rewriter, so Dictate pastes the raw transcript as its fallback.
        if (LastRun("outcome") is not ("Completed" or "CompletedWithFallback")) return $"outcome {LastRun("outcome")}";
        if (!Program.Wait(() => form.On(() => form.Box1.Text) == Transcript, 3000)) return $"box1 is '{form.On(() => form.Box1.Text)}'";
        if (!form.IsForeground) return "foreground left the form";
        if (!Program.Wait(() => ClipText() == Sentinel, 4000)) return $"clipboard not restored: '{ClipText()}'";
        return null;
    }

    private string? ShortTapSilent()
    {
        if (Prepare(form.Box1) is { } fail) return fail;
        var (runs, notices) = (RunCount(), NoticeCount());
        Input.Down(Input.RCtrl);
        Thread.Sleep(120);
        Input.Up(Input.RCtrl);
        Thread.Sleep(2500);
        if (RunCount() != runs) return "a run was started";
        if (NoticeCount() != notices) return "a notice was shown";
        return form.On(() => form.Box1.Text) == "" ? null : "text was pasted";
    }

    private string? CtrlCPassesThrough()
    {
        if (Prepare(form.Box1) is { } fail) return fail;
        var runs = RunCount();
        Input.Down(Input.RCtrl);
        Thread.Sleep(40);
        Input.Tap(Input.C);
        Thread.Sleep(40);
        Input.Up(Input.RCtrl);
        Thread.Sleep(1500);
        if (form.CtrlCDowns == 0) return "the form never saw Ctrl+C";
        return RunCount() == runs ? null : "a run was started";
    }

    private string? EscCancels()
    {
        if (Prepare(form.Box1) is { } fail) return fail;
        var runs = RunCount();
        Input.Down(Input.RCtrl);
        Thread.Sleep(700);
        Input.Tap(Input.Esc);
        Thread.Sleep(100);
        Input.Up(Input.RCtrl);
        Thread.Sleep(3000);
        if (form.EscDowns != 0) return "Esc reached the form";
        if (form.On(() => form.Box1.Text) != "") return "text was pasted";
        if (RunCount() > runs && LastRun("outcome") != "Cancelled") return $"outcome {LastRun("outcome")}";
        return null;
    }

    private string? PasswordRefused()
    {
        if (Prepare(form.Password) is { } fail) return fail;
        Input.Hold(Input.RCtrl, 1500);
        Thread.Sleep(3500);
        if (form.On(() => form.Password.Text) != "") return "text was typed into the password box";
        if (!Notices().Contains("PasswordRefused")) return "no PasswordRefused notice";
        return ClipText() == Sentinel ? null : $"clipboard changed: '{ClipText()}'";
    }

    private string? ReadOnlyCopies()
    {
        if (Prepare(form.ReadOnlyBox) is { } fail) return fail;
        var runs = RunCount();
        Input.Hold(Input.RCtrl, 1500);
        if (!WaitRun(runs)) return "no run finished";
        if (LastRun("delivery") != "Copied") return $"delivery {LastRun("delivery")}";
        return Program.Wait(() => ClipText() == Transcript, 3000) ? null : $"clipboard is '{ClipText()}'";
    }

    private string? TargetChangedCopies()
    {
        if (Prepare(form.Box1) is { } fail) return fail;
        var runs = RunCount();
        Input.Hold(Input.RCtrl, 1200);
        // The fake STT takes 1.5 s: move focus while the run is processing.
        form.On(() => form.Box2.Focus());
        if (!WaitRun(runs)) return "no run finished";
        if (LastRun("delivery") != "Copied") return $"delivery {LastRun("delivery")}";
        if (form.On(() => form.Box1.Text + form.Box2.Text) != "") return "text was pasted";
        return Program.Wait(() => ClipText() == Transcript, 3000) ? null : $"clipboard is '{ClipText()}'";
    }

    private string? TapDuringProcessingCancels()
    {
        if (Prepare(form.Box1) is { } fail) return fail;
        var runs = RunCount();
        Input.Hold(Input.RCtrl, 1200);
        Thread.Sleep(600); // past the 400 ms debounce
        Input.Down(Input.RCtrl);
        Thread.Sleep(80);
        Input.Up(Input.RCtrl);
        if (!WaitRun(runs)) return "no run finished";
        if (LastRun("outcome") != "Cancelled") return $"outcome {LastRun("outcome")}";
        Thread.Sleep(500);
        return form.On(() => form.Box1.Text) == "" ? null : "text was pasted";
    }

    private string? CopyDuringRestore()
    {
        if (Prepare(form.Box1) is { } fail) return fail;
        Input.Hold(Input.RCtrl, 1500);
        if (!Program.Wait(() => form.On(() => form.Box1.Text) == Transcript, 8000)) return "never pasted";
        SetClip("user copy");
        Thread.Sleep(3000);
        return ClipText() == "user copy" ? null : $"user's copy was overwritten: '{ClipText()}'";
    }

    private string? ImageRestored()
    {
        if (Prepare(form.Box1) is { } fail) return fail;
        using (var bmp = new Bitmap(4, 4))
        {
            using (var g = Graphics.FromImage(bmp)) g.Clear(Color.FromArgb(255, 200, 30, 40));
            form.On(() => Retry(() => Clipboard.SetDataObject(new DataObject(DataFormats.Bitmap, bmp), copy: true)));
        }
        Input.Hold(Input.RCtrl, 1500);
        if (!Program.Wait(() => form.On(() => form.Box1.Text) == Transcript, 8000)) return "never pasted";
        if (!Program.Wait(() => form.On(() => Retry(Clipboard.ContainsImage)), 4000)) return "image not restored";
        using var back = (Bitmap)form.On(() => Retry(Clipboard.GetImage))!;
        var p = back.GetPixel(1, 1);
        return p.R == 200 && p.G == 30 && p.B == 40 ? null : $"pixel changed: {p}";
    }

    private string? Prepare(TextBox box)
    {
        form.Reset();
        SetClip(Sentinel);
        // Let any restore from the previous scenario finish before this one starts.
        Thread.Sleep(1200);
        SetClip(Sentinel);
        return form.FocusBox(box) ? null : "couldn't give the form focus";
    }

    private static int RunCount() => Directory.Exists(AppResults) ? Directory.GetFiles(AppResults, "run-*.json").Length : 0;

    private static bool WaitRun(int before) => Program.Wait(() => RunCount() > before, 10_000);

    private static string? LastRun(string field)
    {
        var last = Directory.GetFiles(AppResults, "run-*.json").OrderBy(f => int.Parse(Path.GetFileNameWithoutExtension(f)[4..])).Last();
        return JsonDocument.Parse(File.ReadAllText(last)).RootElement.GetProperty(field).GetString();
    }

    private static string[] Notices()
    {
        var path = Path.Combine(AppResults, "notices.txt");
        return File.Exists(path) ? File.ReadAllLines(path) : [];
    }

    private static int NoticeCount() => Notices().Length;

    // On the form's UI thread, which pumps messages. The harness's main thread mostly sleeps in polling loops; as the
    // clipboard's owner it couldn't answer WM_DESTROYCLIPBOARD, so yolime's EmptyClipboard waited about 5 s for it
    // (PR7's timing breakdown found this: ClipboardSetMs ~5000 whenever the harness had copied last).
    private string? ClipText() => form.On(() => Retry(() => Clipboard.ContainsText() ? Clipboard.GetText() : null));

    private void SetClip(string text) => form.On(() => Retry(() => Clipboard.SetDataObject(text, copy: true)));

    private static T Retry<T>(Func<T> f)
    {
        for (var i = 0; ; i++)
        {
            try { return f(); }
            catch (System.Runtime.InteropServices.ExternalException) when (i < 10) { Thread.Sleep(50); }
        }
    }

    private static void Retry(Action a) => Retry(() => { a(); return 0; });
}
