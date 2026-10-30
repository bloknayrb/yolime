using System.Globalization;

namespace Yolime.E2E;

/// <summary>
/// fix/overlay-card-fit: every card shows all of its content. The app's "show-card" hook posts each notice the way a
/// finished run does, so the cards have their real text and buttons; two independent checks then look for anything
/// cut off: the island's own layout, from inside the app (probe-fit), and the buttons' UIA rectangles, from outside.
/// </summary>
internal sealed partial class Scenarios
{
    public IEnumerable<Scenario> CardFit() =>
    [
        new("Cards_FitTheirWindow", () => CardsFit(AppResults, CardTable())),
        new("Cards_ShrunkCard_IsCaught", ShrunkCardIsCaught),
        new("Cards_PreviewFits", PreviewFits),
    ];

    /// <summary>The preview card (Raw always previews here): its header and three buttons all show.</summary>
    private string? PreviewFits()
    {
        if (Prepare(form.Box1) is { } fail) return fail;
        var previews = Count("Preview");
        HoldCycling(cycles: 3);
        if (!Program.Wait(() => Count("Preview") > previews, 8000)) return "the preview card never showed";
        try { return CheckCard(AppResults, 3, null); }
        finally { Input.Tap(Input.Esc); Thread.Sleep(500); }
    }

    /// <summary>Every notice as a card, with the variants that change its text or its buttons.</summary>
    public static IEnumerable<string> CardTable()
    {
        // Kinds whose real card offers Retry (a kept recording); the rest show Dismiss only.
        var retryable = new HashSet<string> { "DidntHear", "OutOfCredit", "RateLimited", "ServiceUnreachable", "TranscriptionFailed", "MicLost", "Interrupted", "Cancelled" };
        var kinds = new[]
        {
            "NoKey", "KeyRejected", "OutOfCredit", "RateLimited", "ServiceUnreachable", "EndpointInvalid", "TranscriptionFailed", "MicDenied", "NoMic",
            "MicLost", "DidntHear", "Busy", "Copied", "CopiedAdminWindow", "PasswordRefused", "OutputFailed", "Interrupted", "Discarded", "CapWarning",
            "CapReached", "Cancelled", "RetryUnavailable", "ContextSkipped", "PreviewKept", "RawCopied", "HoldKeyReset", "PasteLastUnavailable",
            "ElevatedTarget", "MicFallback", "SettingsNotSaved",
        };
        foreach (var k in kinds) yield return $"{k} - false {retryable.Contains(k).ToString().ToLowerInvariant()}";
        // A rewrite failure's text depends on the mode and on where the transcript went, and Prompt/Reply can Retry.
        foreach (var k in new[] { "RewriteNoKey", "RewriteKeyRejected", "RewriteOutOfCredit", "RewriteRefused", "RewriteTooLong", "RewriteTimedOut", "RewriteUnavailable", "RewriteConfig", "RewriteEmpty" })
        {
            yield return $"{k} - false false";
            yield return $"{k} Dictate false false";
            yield return $"{k} Dictate true false";
            yield return $"{k} Prompt false true";
            yield return $"{k} Reply true true";
        }
    }

    /// <summary>Shows each card in turn and returns the cut-off ones, or null when every one fits.</summary>
    public static string? CardsFit(string results, IEnumerable<string> cards, double? expectTextScale = null)
    {
        var failures = new List<string>();
        var shown = 0;
        foreach (var card in cards)
        {
            var errors = CountIn(results, "Error");
            File.WriteAllText(Path.Combine(results, "show-card"), card);
            if (!Program.Wait(() => CountIn(results, "Error") > errors, 3000)) { failures.Add($"{card}: never shown"); continue; }
            shown++;
            var buttons = 1 + (card.EndsWith(" true", StringComparison.Ordinal) ? 1 : 0) + (card.StartsWith("MicDenied ", StringComparison.Ordinal) ? 1 : 0);
            if (CheckCard(results, buttons, expectTextScale) is { } why) failures.Add($"{card}: {why}");
        }
        if (failures.Count == 0) return null;
        return $"{failures.Count} of {shown} cards: " + string.Join("; ", failures.Take(6));
    }

    /// <summary>
    /// The negative control: with every card made 20 px too short, cutting through its button row as Bryan saw, each
    /// check must see it on its own.
    /// </summary>
    private string? ShrunkCardIsCaught()
    {
        if (!SetShrink(AppResults, 20)) return "the shrink hook didn't answer";
        try
        {
            var errors = CountIn(AppResults, "Error");
            File.WriteAllText(Path.Combine(AppResults, "show-card"), "DidntHear - false true");
            if (!Program.Wait(() => CountIn(AppResults, "Error") > errors, 3000)) return "the card never showed";
            var why = CheckCard(AppResults, 2, null, each: true);
            if (why is null) return "a card 20 px too short passed every check: the fit checks prove nothing";
            // Each oracle must see it on its own, or the one that doesn't is decoration.
            foreach (var oracle in new[] { "layout:", "uia:" })
                if (!why.Contains(oracle, StringComparison.Ordinal)) return $"the {oracle.TrimEnd(':')} check missed it ({why})";
            return null;
        }
        finally
        {
            // A reset that didn't land would leave later scenarios' cards short and failing for the wrong reason.
            if (!SetShrink(AppResults, 0)) throw new InvalidOperationException("the shrink reset didn't answer: later card checks would be wrong");
        }
    }

    private static bool SetShrink(string results, int px)
    {
        var done = Path.Combine(results, "shrink-card.done");
        RetryIo(() => File.Delete(done));
        File.WriteAllText(Path.Combine(results, "shrink-card"), px.ToString(CultureInfo.InvariantCulture));
        return Program.Wait(() => File.Exists(done), 3000);
    }

    /// <summary>
    /// Null when the card on screen shows all of itself. With <paramref name="each"/>, runs every check and reports
    /// each one that failed, tagged "layout:" or "uia:" (for the negative control).
    /// </summary>
    private static string? CheckCard(string results, int expectedButtons, double? expectTextScale, bool each = false)
    {
        Thread.Sleep(400); // the island lays out after the window is sized
        var hwnd = Overlay.Find();
        if (hwnd == 0 || !Overlay.Visible(hwnd)) return "the notch isn't visible";
        var problems = new List<string>();

        // 1. The island's own layout: numbers from the app.
        var probe = Probe(results);
        if (probe is null) return "the fit probe didn't answer";
        var (lowest, room, buttonsRight, width, scale, textScale) = probe.Value;
        if (scale < 1.05) return $"scale {scale:0.00}: at 100% the rounding this guards against can't happen, so this run proves nothing";
        if (expectTextScale is { } t && Math.Abs(textScale - t) > 0.01) return $"text scale is {textScale:0.00}, not {t:0.00}";
        if (lowest > room + 0.5) problems.Add($"layout: content ends at {lowest:0.0} DIP, below the card's {room:0.0}");
        if (buttonsRight > width + 0.5) problems.Add($"layout: buttons reach {buttonsRight:0.0} DIP, past the card's {width:0.0}");

        // 2. UIA: every button there, on screen, the same height, and above the card's bottom padding.
        var (l, top, r, b) = Overlay.WindowRect(hwnd);
        if (UiaButtons(hwnd, expectedButtons, scale, b - top) is { } uia) problems.Add("uia: " + uia);

        // No pixel check: the card's Border clips its content at the inner edge of its padding, so a cut-off row never
        // paints into the bottom padding and the pixels there look the same whether or not anything is cut (tried: it
        // passed a card 20 px too short). The failing card's image is saved below for a person instead.

        if (problems.Count == 0) return null;
        // The card as it was when it failed, for a person to look at (only the first failure is kept).
        var crop = Path.Combine(@"C:\results", "card-fail.png");
        if (!File.Exists(crop))
        {
            try { using var img = Overlay.Grab(l, top, r - l, b - top); img.Save(crop); }
            catch (Exception) { }
        }
        var where = $" [window {r - l}x{b - top} px, island scale {scale:0.00}, text {textScale:0.00}, layout {_probeDetail}]";
        return (each ? string.Join(" | ", problems) : problems[0]) + where;
    }

    private static string? UiaButtons(nint hwnd, int expected, double scale, int physicalHeight)
    {
        using var automation = new FlaUI.UIA3.UIA3Automation();
        var window = automation.FromHandle(hwnd);
        var buttons = window.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button));
        if (buttons.Length != expected) return $"{buttons.Length} buttons, expected {expected}";
        var frame = window.BoundingRectangle;
        var unitsPerDip = frame.Height / Math.Max(1.0, physicalHeight / scale);
        var heights = new HashSet<int>();
        foreach (var button in buttons)
        {
            var rect = button.BoundingRectangle;
            if (rect.IsEmpty || button.IsOffscreen) return $"'{button.Name}' is offscreen or empty";
            // UIA reports a clipped button's visible part, which ends neatly inside the card: a partly cut-off row shows
            // up as buttons shorter than any real one (28.8 DIP at normal text size, more when larger).
            if (rect.Height < 24 * unitsPerDip) return $"'{button.Name}' is only {rect.Height / unitsPerDip:0.0} DIP tall: cut off";
            // Its bottom must leave the card's bottom padding (10 DIP) clear, give or take a couple of units of rounding.
            // UIA's units are the harness's (it isn't DPI-aware), so the padding is converted through the frame itself.
            if (rect.Bottom > frame.Bottom - 10 * unitsPerDip + 2) return $"'{button.Name}' reaches the card's bottom edge";
            heights.Add(rect.Height);
        }
        return heights.Count <= 1 || heights.Max() - heights.Min() <= 1 ? null : $"button heights differ ({string.Join(",", heights)})";
    }

    /// <summary>
    /// The app writes these hook files and Defender scans each new one, so a moment's sharing violation is normal:
    /// retry it rather than fail the scenario over it (seen: probe-fit.done "used by another process"; a delete-pending
    /// file shows as access denied instead).
    /// </summary>
    private static T RetryIo<T>(Func<T> io)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return io(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20) { Thread.Sleep(50); }
        }
    }

    private static void RetryIo(Action io) => RetryIo(() => { io(); return 0; });

    /// <summary>The probe's per-child layout ("rootHeight;index:top+height,..."), for failure messages.</summary>
    private static string _probeDetail = "";

    private static (double Lowest, double Room, double ButtonsRight, double Width, double Scale, double TextScale)? Probe(string results)
    {
        var done = Path.Combine(results, "probe-fit.done");
        RetryIo(() => File.Delete(done));
        File.WriteAllText(Path.Combine(results, "probe-fit"), "probe");
        if (!Program.Wait(() => File.Exists(done), 3000)) return null;
        var parts = RetryIo(() => File.ReadAllText(done)).Trim().Split(' ');
        _probeDetail = parts.Length > 6 ? parts[6] : "";
        var v = parts.Take(6).Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        return (v[0], v[1], v[2], v[3], v[4], v[5]);
    }

    /// <summary>How many times the overlay entered a state, from a results folder other than the main app's.</summary>
    private static int CountIn(string results, string state)
    {
        var path = Path.Combine(results, "overlay.txt");
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return 0;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd().Split('\n').Count(l => l.StartsWith(state + " ", StringComparison.Ordinal));
            }
            catch (IOException) { Thread.Sleep(30); }
        }
        return 0;
    }
}
