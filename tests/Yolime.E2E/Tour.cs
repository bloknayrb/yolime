using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using Microsoft.Win32;

namespace Yolime.E2E;

/// <summary>
/// The UI tour (docs/superpowers/plans/2026-10-30-ui-tour.md), run by <c>run-e2e.ps1 -Tour</c>, in the Sandbox only:
/// every overlay state the app's TourStates table stages, every card, each Welcome step and Settings page, and
/// History, in light and then dark, as cropped PNGs under C:\results\&lt;area&gt; plus tour-index.json. Each image is
/// checked to show what it names before it's kept; e2e.json lists those checks. No UI text goes into the index.
/// </summary>
internal static partial class Tour
{
    private const string Results = @"C:\results";
    private const string Root = @"C:\e2e";
    private const int Margin = 8;
    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>A sentence ending in a 200-character URL: every History row is long, as its fixed column must handle.</summary>
    private const string LongTranscript = "here is the budget review link for thursday please check the totals before the call " +
        "https://contoso.sharepoint.com/sites/finance/Shared%20Documents/Forms/AllItems.aspx?id=%2Fsites%2Ffinance%2FShared%20Documents%2FQ3%2Dbudget%2Dreview%2Dfinal%2Dv7%2Exlsx";

    /// <summary>
    /// In the fit table but never a card: tray balloons only, and CapWarning (with the overlay up, the coordinator posts a
    /// countdown instead). Their text goes to the critics' brief, not into the tour.
    /// </summary>
    private static readonly string[] TrayOnly = ["HoldKeyReset", "SettingsNotSaved", "ElevatedTarget", "CapWarning"];

    private static readonly List<object> Report = [];
    private static readonly List<object> Index = [];

    public static int Run()
    {
        Directory.CreateDirectory(Results);
        File.WriteAllText(Path.Combine(Results, "started"), "started");
        var areasFile = Path.Combine(Results, "areas.txt");
        var areas = File.Exists(areasFile) ? File.ReadAllLines(areasFile).Where(l => l.Length > 0).ToHashSet(StringComparer.Ordinal)
            : ["overlay", "cards", "windows"];
        var original = ReadTheme();
        Process? app = null;
        try
        {
            var form = TargetForm.StartOnOwnThread();
            foreach (var theme in new[] { "light", "dark" })
            {
                SetTheme(theme == "light" ? 1 : 0);
                var results = Path.Combine(Results, "app-" + theme);
                if (areas.Contains("overlay") || areas.Contains("cards") || areas.Contains("windows"))
                {
                    app = Launch(@"C:\tourdata-" + theme, results, firstRun: false);
                    if (app is null) { Fail($"{theme}: launch", "the app never became ready"); continue; }
                    if (areas.Contains("overlay") || areas.Contains("cards")) OverlayPass(results, theme, areas);
                    if (theme == "light" && areas.Contains("overlay")) CaptureControl(results);
                    if (areas.Contains("windows"))
                    {
                        Ask(results, "tour-state", "hidden");
                        Ask(results, "tour-freeze", "off");
                        SettingsPages(form, theme);
                        HistoryPage(form, results, theme);
                    }
                    Stop(app);
                    app = null;
                }
                if (areas.Contains("windows"))
                {
                    app = Launch(@"C:\tourdata-first-" + theme, Path.Combine(Results, "app-first-" + theme), firstRun: true);
                    if (app is null) Fail($"{theme}: welcome launch", "the fresh app never became ready");
                    else WelcomeSteps(form, theme);
                    Stop(app);
                    app = null;
                }
            }
            LightDarkDiffer();
        }
        catch (Exception ex)
        {
            Fail("tour", ex.GetType().Name + ": " + ex.Message);
            Screenshot.Save(Path.Combine(Results, "tour.png"));
        }
        finally
        {
            try { app?.Kill(); } catch (InvalidOperationException) { }
            RestoreTheme(original);
            File.WriteAllText(Path.Combine(Results, "tour-index.json"), JsonSerializer.Serialize(Index, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(Results, "e2e.json"), JsonSerializer.Serialize(Report, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(Results, "DONE"), "done");
        }
        return 0;
    }

    // ---- The overlay ------------------------------------------------------------------------------------------------

    private static void OverlayPass(string results, string theme, HashSet<string> areas)
    {
        if (Ask(results, "tour-freeze", "on") != "ok") { Fail($"{theme}: freeze", "tour-freeze didn't answer"); return; }
        var names = File.ReadAllLines(Path.Combine(results, "tour-states.txt"))
            .Where(n => n is not ("hidden" or "updating") && !n.StartsWith("control-", StringComparison.Ordinal)).ToList();
        if (theme == "light" && areas.Contains("overlay")) NegativeControls(results);
        var cards = Scenarios.CardTable().Where(c => !TrayOnly.Contains(c.Split(' ')[0])).Select(c => "card " + c).ToList();
        var entries = new List<(string Area, string Name)>();
        if (areas.Contains("overlay")) entries.AddRange(names.Where(n => !n.StartsWith("card-", StringComparison.Ordinal)).Select(n => ("overlay", n)));
        if (areas.Contains("cards"))
        {
            entries.AddRange(names.Where(n => n.StartsWith("card-", StringComparison.Ordinal)).Select(n => ("cards", n)));
            entries.AddRange(cards.Select(c => ("cards", c)));
        }
        // Nothing replaces Updating, so it goes last.
        if (areas.Contains("overlay")) entries.Add(("overlay", "updating"));
        foreach (var (area, name) in entries) CaptureState(results, theme, area, name);
    }

    /// <summary>
    /// Each check seen failing once, in the light pass: a wrong state, wrong text, the dark theme's expectations on a
    /// light app, and a blank image. A control that passes means its check proves nothing.
    /// </summary>
    private static void NegativeControls(string results)
    {
        foreach (var (entry, expect) in new[] { ("control-wrong-state", "mismatch expected Done"), ("control-wrong-text", "mismatch Listening text differs") })
        {
            var answer = Ask(results, "tour-state", entry) ?? "no answer";
            var ok = answer.StartsWith(expect, StringComparison.Ordinal);
            Report.Add(new { name = "control/" + entry, pass = ok, failure = ok ? null : "the state check didn't catch it: " + answer });
        }
        var wrongTheme = CaptureState(results, "dark", "overlay", "listen-dictate", save: false);
        var caught = wrongTheme.Any(p => p.StartsWith("probe says", StringComparison.Ordinal)) && wrongTheme.Any(p => p.StartsWith("fill", StringComparison.Ordinal));
        Report.Add(new { name = "control/wrong-theme", pass = caught, failure = caught ? null : "the theme checks missed a light card checked as dark: " + string.Join("; ", wrongTheme) });
        using var blank = new Bitmap(200, 60);
        using (var g = Graphics.FromImage(blank)) g.Clear(Color.FromArgb(32, 32, 32));
        Report.Add(new { name = "control/uniform", pass = Uniform(blank), failure = Uniform(blank) ? null : "a blank image passed the uniform check" });
    }

    private static List<string> CaptureState(string results, string theme, string area, string name, bool save = true)
    {
        var label = $"{area}/{FileName(name)}-{theme}";
        var answer = Ask(results, "tour-state", name);
        if (answer != "ok") return Failed(label, answer is null ? "tour-state didn't answer" : answer);
        Thread.Sleep(400); // the island lays out after the window is sized
        var notch = Overlay.Find();
        if (notch == 0 || !Overlay.Visible(notch)) return Failed(label, "the notch isn't visible");
        var n = Overlay.WindowRect(notch);
        var (l, t, r, b) = n;
        var pill = Overlay.FindPill();
        if (pill != 0 && Overlay.Visible(pill))
        {
            var p = Overlay.WindowRect(pill);
            (l, t, r, b) = (Math.Min(l, p.Left), Math.Min(t, p.Top), Math.Max(r, p.Right), Math.Max(b, p.Bottom));
        }
        (l, t, r, b) = (Math.Max(0, l - Margin), Math.Max(0, t - Margin), r + Margin, b + Margin);
        using var image = Overlay.Grab(l, t, r - l, b - t);
        var probe = Ask(results, "probe-theme", "") ?? "";
        var problems = new List<string>();
        if (Uniform(image)) problems.Add("uniform");
        if (!probe.StartsWith(theme + " ", StringComparison.Ordinal)) problems.Add($"probe says {probe.Split('|')[0]}");
        // The card's fill, just inside the notch's left edge, mid-height: the theme's grey (COLORREF-free, from the image).
        var fill = image.GetPixel(n.Left - l + 8, (n.Top + n.Bottom) / 2 - t);
        var grey = theme == "light" ? 249 : 32;
        if (Math.Abs(fill.R - grey) > 6 || Math.Abs(fill.G - grey) > 6 || Math.Abs(fill.B - grey) > 6) problems.Add($"fill {fill.R},{fill.G},{fill.B} isn't {grey}");
        if (save) Save(image, label, area, name, theme, (l, t, r, b), probe, problems);
        return problems;
    }

    private static List<string> Failed(string label, string why)
    {
        Fail(label, why);
        return [why];
    }

    /// <summary>
    /// "Hide from screen capture" works: with it on, the card drops out of the capture (it isn't blacked out:
    /// WDA_EXCLUDEFROMCAPTURE), so the image matches the same rect with nothing showing.
    /// </summary>
    private static void CaptureControl(string results)
    {
        const string label = "control/capture-hidden";
        if (Ask(results, "tour-state", "card DidntHear - false true") != "ok") { Fail(label, "the card wasn't staged"); return; }
        Thread.Sleep(400);
        var notch = Overlay.Find();
        var (l, t, r, b) = Overlay.WindowRect(notch);
        (l, t, r, b) = (Math.Max(0, l - Margin), Math.Max(0, t - Margin), r + Margin, b + Margin);
        using var shown = Overlay.Grab(l, t, r - l, b - t);
        var hide = Ask(results, "set-capture-hidden", "true") ?? "no answer";
        try
        {
            if (!hide.StartsWith("ok Error", StringComparison.Ordinal)) { Fail(label, "set-capture-hidden: " + hide); return; }
            if (!Overlay.Visible(notch)) { Fail(label, "the card went before the hidden capture"); return; }
            Thread.Sleep(400);
            using var hidden = Overlay.Grab(l, t, r - l, b - t);
            Ask(results, "tour-state", "hidden");
            Thread.Sleep(600);
            using var nothing = Overlay.Grab(l, t, r - l, b - t);
            var fromShown = Differ(hidden, shown);
            var fromNothing = Differ(hidden, nothing);
            foreach (var (img, name) in new[] { (shown, "shown"), (hidden, "hidden"), (nothing, "nothing") })
                img.Save(Path.Combine(Dir("control"), name + ".png"), ImageFormat.Png);
            var why = fromShown <= 0.20 ? $"the hidden capture still shows the card ({fromShown:P0} differs)"
                : fromNothing > 0.02 ? $"the hidden capture isn't the bare desktop ({fromNothing:P0} differs)" : null;
            Report.Add(new { name = label, pass = why is null, failure = why });
        }
        finally
        {
            if (Ask(results, "set-capture-hidden", "false") is not { } back || !back.StartsWith("ok", StringComparison.Ordinal))
                throw new InvalidOperationException("couldn't show the notch to capture again: later images would be the desktop");
        }
    }

    // ---- The windows ------------------------------------------------------------------------------------------------

    private static void SettingsPages(TargetForm form, string theme)
    {
        using var automation = new UIA3Automation();
        if (!TrayCommand(6)) { Fail($"windows/settings-{theme}", "no tray window"); return; }
        AutomationElement? window = null;
        if (!Program.Wait(() => (window = automation.GetDesktop().FindFirstChild(cf => cf.ByName("yolime settings"))) is not null, 8000))
        {
            Fail($"windows/settings-{theme}", "Settings never opened");
            return;
        }
        try
        {
            foreach (var page in new[] { "General", "Keys", "Apps & words", "Overlay & history", "Advanced" })
            {
                var item = window!.FindFirstDescendant(cf => cf.ByName(page).And(cf.ByControlType(FlaUI.Core.Definitions.ControlType.ListItem)))
                    ?? window.FindFirstDescendant(cf => cf.ByName(page));
                if (item is null) { Fail($"windows/settings-{FileName(page)}-{theme}", "no such page in the menu"); continue; }
                Activate(item);
                Thread.Sleep(800);
                CaptureWindow(form, window, "windows", "settings-" + page, theme);
            }
        }
        finally { CloseQuietly(window!); }
    }

    private static void HistoryPage(TargetForm form, string results, string theme)
    {
        // Four real runs first, so the rows are real: Dictate, Prompt (one Shift tap), Reply (two, with the fresh
        // clipboard as context), Raw (three, then Enter on its preview).
        form.On(() => form.WindowState = FormWindowState.Normal);
        foreach (var (taps, name) in new[] { (0, "dictate"), (1, "prompt"), (2, "reply"), (3, "raw") })
        {
            form.Reset();
            form.On(() => Clipboard.SetDataObject("Can you send me the final Q3 numbers?", copy: true));
            Thread.Sleep(1200);
            if (!form.FocusBox(form.Box1)) { Fail($"windows/history-{theme}", $"{name}: couldn't focus the form"); return; }
            var runs = RunCount(results);
            Input.Down(Input.RCtrl);
            Thread.Sleep(500);
            for (var i = 0; i < taps; i++) { Input.Tap(Input.LShift); Thread.Sleep(150); }
            Thread.Sleep(900);
            Input.Up(Input.RCtrl);
            if (taps == 3)
            {
                Thread.Sleep(2500);
                Input.Tap(Input.Enter);
            }
            if (!Program.Wait(() => RunCount(results) > runs, 15_000)) { Fail($"windows/history-{theme}", $"the {name} run never finished"); return; }
            Thread.Sleep(1500);
        }
        // The Sandbox reports the notch as unseeable, so a real run's notice is also a toast: let it go before capturing.
        Thread.Sleep(10_000);
        using var automation = new UIA3Automation();
        if (!TrayCommand(8)) { Fail($"windows/history-{theme}", "no tray window"); return; }
        AutomationElement? window = null;
        if (!Program.Wait(() => (window = automation.GetDesktop().FindFirstChild(cf => cf.ByName("yolime history"))) is not null, 8000))
        {
            Fail($"windows/history-{theme}", "History never opened");
            return;
        }
        Thread.Sleep(1000);
        try { CaptureWindow(form, window!, "windows", "history", theme); }
        finally { CloseQuietly(window!); }
    }

    private static void WelcomeSteps(TargetForm form, string theme)
    {
        using var automation = new UIA3Automation();
        AutomationElement? window = null;
        if (!Program.Wait(() => (window = automation.GetDesktop().FindFirstChild(cf => cf.ByName("Welcome to yolime"))) is not null, 10_000))
        {
            Fail($"windows/welcome-{theme}", "the wizard never opened");
            return;
        }
        Thread.Sleep(800);
        try
        {
            for (var step = 1; step <= 5; step++)
            {
                CaptureWindow(form, window!, "windows", $"welcome-{step}", theme);
                if (step == 5) break;
                var next = window!.FindFirstDescendant(cf => cf.ByName("Next").And(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button)));
                if (next is null) { Fail($"windows/welcome-{step + 1}-{theme}", "no Next button"); return; }
                Activate(next);
                Thread.Sleep(800);
            }
        }
        finally { CloseQuietly(window!); }
    }

    private static void CaptureWindow(TargetForm form, AutomationElement window, string area, string name, string theme)
    {
        var label = $"{area}/{FileName(name)}-{theme}";
        form.On(() => form.WindowState = FormWindowState.Minimized);
        var hwnd = window.Properties.NativeWindowHandle.ValueOrDefault;
        if (hwnd == 0 || !IsWindowVisible(hwnd)) { Fail(label, "the window isn't visible"); return; }
        SetForegroundWindow(hwnd);
        window.Focus();
        Thread.Sleep(500);
        var old = SetThreadDpiAwarenessContext(-4);
        Rect r;
        try { if (DwmGetWindowAttribute(hwnd, 9, out r, Marshal.SizeOf<Rect>()) != 0) { Fail(label, "no frame bounds"); return; } }
        finally { SetThreadDpiAwarenessContext(old); }
        using var image = Overlay.Grab(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        var problems = new List<string>();
        if (Uniform(image)) problems.Add("uniform");
        var luminance = MeanLuminance(image);
        if (theme == "light" ? luminance <= 128 : luminance >= 128) problems.Add($"mean luminance {luminance:0} doesn't fit {theme}");
        Save(image, label, area, name, theme, (r.Left, r.Top, r.Right, r.Bottom), $"luminance {luminance:0}", problems);
    }

    // ---- Checks -----------------------------------------------------------------------------------------------------

    /// <summary>Each entry's light and dark images must differ, or the theme switch did nothing.</summary>
    private static void LightDarkDiffer()
    {
        foreach (var light in Directory.EnumerateFiles(Results, "*-light.png", SearchOption.AllDirectories))
        {
            var dark = light[..^"-light.png".Length] + "-dark.png";
            if (!File.Exists(dark)) continue;
            using var a = new Bitmap(light);
            using var b = new Bitmap(dark);
            var differs = a.Size != b.Size || Differ(a, b) > 0.20;
            var name = Path.GetRelativePath(Results, light)[..^"-light.png".Length].Replace('\\', '/') + " light/dark";
            Report.Add(new { name, pass = differs, failure = differs ? null : "light and dark look the same" });
        }
    }

    /// <summary>More than 98% of pixels within 4 of the commonest colour: blank, undrawn or covered.</summary>
    private static bool Uniform(Bitmap image)
    {
        var px = Pixels(image);
        var top = px.GroupBy(p => p & 0xFCFCFC).OrderByDescending(g => g.Count()).First().Key;
        int R(int p) => (p >> 16) & 0xFF;
        int G(int p) => (p >> 8) & 0xFF;
        int B(int p) => p & 0xFF;
        var near = px.Count(p => Math.Abs(R(p) - R(top)) <= 4 && Math.Abs(G(p) - G(top)) <= 4 && Math.Abs(B(p) - B(top)) <= 4);
        return near > px.Length * 0.98;
    }

    private static double MeanLuminance(Bitmap image) =>
        Pixels(image).Average(p => 0.2126 * ((p >> 16) & 0xFF) + 0.7152 * ((p >> 8) & 0xFF) + 0.0722 * (p & 0xFF));

    /// <summary>The share of pixels that differ by more than 8 in any channel (same-size images only).</summary>
    private static double Differ(Bitmap a, Bitmap b)
    {
        if (a.Size != b.Size) return 1;
        var pa = Pixels(a);
        var pb = Pixels(b);
        var n = 0;
        for (var i = 0; i < pa.Length; i++)
        {
            int x = pa[i], y = pb[i];
            if (Math.Abs(((x >> 16) & 0xFF) - ((y >> 16) & 0xFF)) > 8 || Math.Abs(((x >> 8) & 0xFF) - ((y >> 8) & 0xFF)) > 8 || Math.Abs((x & 0xFF) - (y & 0xFF)) > 8) n++;
        }
        return (double)n / pa.Length;
    }

    private static int[] Pixels(Bitmap image)
    {
        var data = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var px = new int[image.Width * image.Height];
            for (var y = 0; y < image.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, px, y * image.Width, image.Width);
            return px;
        }
        finally { image.UnlockBits(data); }
    }

    // ---- Plumbing ---------------------------------------------------------------------------------------------------

    private static void Save(Bitmap image, string label, string area, string name, string theme, (int L, int T, int R, int B) rect, string probe, List<string> problems)
    {
        var failure = problems.Count == 0 ? null : string.Join("; ", problems);
        // Kept even when a check fails: the image shows what went wrong.
        image.Save(Path.Combine(Dir(area), $"{FileName(name)}-{theme}.png"), ImageFormat.Png);
        Index.Add(new { area, entry = name, theme, file = $"{area}/{FileName(name)}-{theme}.png", rect = new[] { rect.L, rect.T, rect.R, rect.B }, probe, failure });
        Report.Add(new { name = label, pass = failure is null, failure });
    }

    private static void Fail(string label, string why) => Report.Add(new { name = label, pass = false, failure = why });

    private static string Dir(string area)
    {
        var dir = Path.Combine(Results, area);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string FileName(string name) => string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c == '-' ? char.ToLowerInvariant(c) : '_'));

    /// <summary>Drops a hook file and waits for its answer: the ".done" text, or null on an error or no answer.</summary>
    private static string? Ask(string results, string hook, string arg)
    {
        var path = Path.Combine(results, hook);
        foreach (var stale in new[] { path + ".done", path + ".error" }) RetryIo(() => File.Delete(stale));
        File.WriteAllText(path, arg);
        string? answer = null;
        Program.Wait(() => File.Exists(path + ".error") || (File.Exists(path + ".done") && (answer = RetryIo(() => File.ReadAllText(path + ".done"))) is not null), 5000);
        if (File.Exists(path + ".error")) return "error " + RetryIo(() => File.ReadAllText(path + ".error"));
        return answer;
    }

    private static T RetryIo<T>(Func<T> io)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return io(); }
            catch (IOException) when (attempt < 20) { Thread.Sleep(25); }
        }
    }

    private static void RetryIo(Action io) => RetryIo(() => { io(); return 0; });

    private static int RunCount(string results) => Directory.Exists(results) ? Directory.GetFiles(results, "run-*.json").Length : 0;

    private static Process? Launch(string data, string results, bool firstRun)
    {
        // Single instance: a launch while the last one lives hands over and exits.
        if (!Program.Wait(() => Process.GetProcessesByName("Yolime.App").Length == 0, 15_000)) return null;
        Thread.Sleep(1000);
        var start = new ProcessStartInfo(Path.Combine(Root, "app", "Yolime.App.exe")) { UseShellExecute = false };
        var args = new List<string> { "--e2e", "--test-mic", $@"{Root}\fixtures\speech-short.wav", "--test-transcript", LongTranscript, "--test-rewrite",
            "--test-data-dir", data, "--e2e-results", results };
        if (firstRun) args.Add("--test-first-run");
        foreach (var a in args) start.ArgumentList.Add(a);
        var app = Process.Start(start);
        if (!Program.Wait(() => File.Exists(Path.Combine(results, "ready")), 20_000)) return null;
        // The startup toast arrives late in a fresh Sandbox (seen ~17 s in): let it come and go first.
        Thread.Sleep(20_000);
        return app;
    }

    private static void Stop(Process? app)
    {
        try { app?.Kill(); app?.WaitForExit(5000); } catch (InvalidOperationException) { }
    }

    private static (object? Apps, object? System) ReadTheme()
    {
        using var key = Registry.CurrentUser.CreateSubKey(Personalize);
        return (key.GetValue("AppsUseLightTheme"), key.GetValue("SystemUsesLightTheme"));
    }

    private static void SetTheme(int light)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Personalize);
        key.SetValue("AppsUseLightTheme", light, RegistryValueKind.DWord);
        key.SetValue("SystemUsesLightTheme", light, RegistryValueKind.DWord);
    }

    private static void RestoreTheme((object? Apps, object? System) original)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Personalize);
        foreach (var (name, value) in new[] { ("AppsUseLightTheme", original.Apps), ("SystemUsesLightTheme", original.System) })
        {
            if (value is null) key.DeleteValue(name, throwOnMissingValue: false);
            else key.SetValue(name, value, RegistryValueKind.DWord);
        }
    }

    private static bool TrayCommand(int command)
    {
        var tray = FindWindowW("yolime-tray", "yolime");
        if (tray == 0) return false;
        PostMessageW(tray, 0x0111, command, 0);
        return true;
    }

    /// <summary>Through patterns, never a mouse click: the harness and the app may not agree on DPI.</summary>
    private static void Activate(AutomationElement element)
    {
        if (element.Patterns.Invoke.IsSupported) element.Patterns.Invoke.Pattern.Invoke();
        else if (element.Patterns.SelectionItem.IsSupported) element.Patterns.SelectionItem.Pattern.Select();
        else element.Click();
    }

    private static void CloseQuietly(AutomationElement window)
    {
        try { window.AsWindow().Close(); }
        catch (Exception) { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint hwnd, int attribute, out Rect value, int size);

    [LibraryImport("user32.dll")]
    private static partial nint SetThreadDpiAwarenessContext(nint context);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowW(string? cls, string? title);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);
}
