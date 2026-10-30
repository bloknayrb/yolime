using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Yolime.E2E;

/// <summary>
/// Runs inside Windows Sandbox only. Starts the E2E build of yolime, shows its own target form, and drives scenarios
/// with SendInput, checking what lands in the form and on the clipboard. Results go to C:\results\e2e.json.
/// </summary>
internal static class Program
{
    private const string Results = @"C:\results";
    private const string Root = @"C:\e2e";

    [STAThread]
    private static int Main(string[] args)
    {
        if (!string.Equals(Environment.UserName, "WDAGUtilityAccount", StringComparison.OrdinalIgnoreCase)) return 64;
        if (args.Length == 4 && args[0] == "--flash") return Flash.Child(args);
        // The update scenarios (run-e2e.ps1 -Updates), and their medium-integrity target window.
        if (args.Length == 2 && args[0] == "--target") return TargetChild.Run(args[1]);
        if (args.Length == 1 && args[0] == "--updates") return UpdateScenarios.Run();
        Directory.CreateDirectory(Results);
        // Tells the host runner the logon command ran: a Sandbox that boots without running it is relaunched.
        File.WriteAllText(Path.Combine(Results, "started"), "started");
        var report = new List<object>();
        Process? app = null;
        try
        {
            var form = TargetForm.StartOnOwnThread();
            var start = new ProcessStartInfo(Path.Combine(Root, "app", "Yolime.App.exe")) { UseShellExecute = false };
            // ArgumentList quotes each one, so the spoken text stays a single argument.
            foreach (var a in new[] { "--e2e", "--test-mic", $@"{Root}\fixtures\speech-short.wav", "--test-transcript", Scenarios.Spoken, "--test-rewrite",
                         "--test-stt-delay", "1500", "--test-data-dir", @"C:\e2edata", "--e2e-results", $@"{Results}\app" })
                start.ArgumentList.Add(a);
            app = Process.Start(start);
            if (!Wait(() => File.Exists(Path.Combine(Results, "app", "ready")), 20_000)) throw new InvalidOperationException("app never became ready");
            // The startup toast arrives late in a fresh Sandbox (seen ~17 s in) and, arriving mid-hold, discarded it: wait it out.
            Thread.Sleep(20_000);

            var scenarios = new Scenarios(form);
            // only.txt (written by run-e2e.ps1 -Only) narrows the run to the named scenarios.
            var onlyFile = Path.Combine(Results, "only.txt");
            var only = File.Exists(onlyFile) ? File.ReadAllLines(onlyFile).Where(l => l.Length > 0).ToHashSet(StringComparer.Ordinal) : null;
            foreach (var scenario in scenarios.All().Concat(scenarios.Foreground()).Concat(scenarios.PreviewGesture()).Concat(scenarios.Onboarding())
                         .Concat(scenarios.History()).Concat(scenarios.DelayedRender()).Concat(scenarios.CardFit()).Where(s => only is null || only.Contains(s.Name)))
            {
                string? failure;
                try { failure = scenario.Run(); }
                catch (Exception ex) { failure = "threw " + ex.GetType().Name + ": " + ex.Message; }
                report.Add(new { name = scenario.Name, pass = failure is null, failure });
                if (failure is not null) Screenshot.Save(Path.Combine(Results, scenario.Name + ".png"));
            }

            // Own launches, each needing a fresh app; -Only runs them too when it names them.
            var ownLaunches = new (string Name, Func<(string?, Process?)> Run)[]
            {
                // The kept-ready mic, with the setting on in its own data folder.
                ("KeepMicReady_ReusesTheMic_NothingFromTheGap_PauseCloses", () => (KeepMicReadyRun(scenarios, out var a), a)),
                // A first run on an empty data folder: it opens the wizard, and Skip ends it.
                ("Wizard_FirstRun_SkipMarksDone", () => (FirstRunWizard(out var a), a)),
                // Windows' text size at 150%: the cards still fit, now that their text is half as large again.
                ("Cards_FitAt150PercentText", () => (CardsAtTextScale(out var a), a)),
            };
            foreach (var (name, run) in ownLaunches.Where(o => only is null || only.Contains(o.Name)))
            {
                try { app?.Kill(); app?.WaitForExit(5000); } catch (InvalidOperationException) { }
                app = null;
                string? failure;
                try { (failure, app) = run(); }
                catch (Exception ex) { failure = "threw " + ex.GetType().Name + ": " + ex.Message; }
                report.Add(new { name, pass = failure is null, failure });
                if (failure is not null) Screenshot.Save(Path.Combine(Results, name + ".png"));
            }
        }
        catch (Exception ex)
        {
            report.Add(new { name = "harness", pass = false, failure = ex.GetType().Name + ": " + ex.Message });
            Screenshot.Save(Path.Combine(Results, "harness.png"));
        }
        finally
        {
            try { app?.Kill(); } catch (InvalidOperationException) { }
            // The app's own logs (state changes and errors only, never content) explain a crash the report can't.
            foreach (var dir in new[] { @"C:\e2edata\local\logs", @"C:\e2edata-first\local\logs", @"C:\e2edata-warm\local\logs", @"C:\e2edata-text\local\logs" })
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    var to = Path.Combine(Results, "logs", Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(dir)))!);
                    Directory.CreateDirectory(to);
                    foreach (var f in Directory.GetFiles(dir)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: true);
                }
                catch (IOException) { }
            }
            // Application Error / .NET Runtime events name the faulting module and exception code of a native crash.
            try
            {
                var wevt = Process.Start(new ProcessStartInfo("wevtutil", "qe Application /q:\"*[System[(EventID=1000 or EventID=1026)]]\" /f:text /c:6 /rd:true")
                    { RedirectStandardOutput = true, UseShellExecute = false });
                if (wevt is not null)
                {
                    File.WriteAllText(Path.Combine(Results, "app-crashes.txt"), wevt.StandardOutput.ReadToEnd());
                    wevt.WaitForExit(10_000);
                }
            }
            catch (Exception) { }
            File.WriteAllText(Path.Combine(Results, "e2e.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(Results, "DONE"), "done");
        }
        return 0;
    }

    private static string? KeepMicReadyRun(Scenarios scenarios, out Process? app)
    {
        app = null;
        var results = Path.Combine(Results, "app-warm");
        // The killed main app must be fully gone: while it lives it holds the single-instance key, and this launch
        // would hand its activation over and exit without ever starting.
        if (!Wait(() => Process.GetProcessesByName("Yolime.App").Length == 0, 15_000)) return "the previous app never exited";
        Thread.Sleep(1000);
        var start = new ProcessStartInfo(Path.Combine(Root, "app", "Yolime.App.exe")) { UseShellExecute = false };
        foreach (var a in new[] { "--e2e", "--test-mic", $@"{Root}\fixtures\speech-short.wav", "--test-transcript", Scenarios.Spoken, "--test-rewrite",
                     "--test-stt-delay", "300", "--test-keep-mic-ready", "--test-data-dir", @"C:\e2edata-warm", "--e2e-results", results })
            start.ArgumentList.Add(a);
        app = Process.Start(start);
        if (!Wait(() => File.Exists(Path.Combine(results, "ready")), 20_000)) return "the kept-ready app never became ready";
        Thread.Sleep(2000);
        return scenarios.KeepMicReady(results);
    }

    private static string? FirstRunWizard(out Process? app)
    {
        const string data = @"C:\e2edata-first";
        var start = new ProcessStartInfo(Path.Combine(Root, "app", "Yolime.App.exe")) { UseShellExecute = false };
        foreach (var a in new[] { "--e2e", "--test-mic", $@"{Root}\fixtures\speech-short.wav", "--test-first-run", "--test-data-dir", data, "--e2e-results", $@"{Results}\app-first" })
            start.ArgumentList.Add(a);
        app = Process.Start(start);
        if (!Wait(() => File.Exists(Path.Combine(Results, "app-first", "ready")), 20_000)) return "the fresh app never became ready";
        using var automation = new FlaUI.UIA3.UIA3Automation();
        FlaUI.Core.AutomationElements.AutomationElement? window = null;
        if (!Wait(() => (window = automation.GetDesktop().FindFirstChild(cf => cf.ByName("Welcome to yolime"))) is not null, 10_000)) return "the wizard never opened";
        var skip = window!.FindFirstDescendant(cf => cf.ByName("Skip setup"));
        if (skip is null) return "no Skip setup";
        skip.Patterns.Invoke.Pattern.Invoke();
        var settings = Path.Combine(data, "roaming", "settings.json");
        if (!Wait(() => File.Exists(settings) && File.ReadAllText(settings).Contains("\"firstRunDone\": true", StringComparison.Ordinal), 5000))
            return "Skip didn't record firstRunDone";
        return Wait(() => automation.GetDesktop().FindFirstChild(cf => cf.ByName("Welcome to yolime")) is null, 3000) ? null : "the wizard stayed open";
    }

    /// <summary>
    /// A fresh app with Windows' text size set to 150% before it starts, then a sweep of the longest cards. The probe
    /// reports the text scale the app saw, so a setting that didn't take fails instead of passing at 100%.
    /// </summary>
    private static string? CardsAtTextScale(out Process? app)
    {
        app = null;
        var results = Path.Combine(Results, "app-text");
        if (!Wait(() => Process.GetProcessesByName("Yolime.App").Length == 0, 15_000)) return "the previous app never exited";
        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Accessibility"))
            key.SetValue("TextScaleFactor", 150, Microsoft.Win32.RegistryValueKind.DWord);
        try
        {
            var start = new ProcessStartInfo(Path.Combine(Root, "app", "Yolime.App.exe")) { UseShellExecute = false };
            foreach (var a in new[] { "--e2e", "--test-mic", $@"{Root}\fixtures\speech-short.wav", "--test-data-dir", @"C:\e2edata-text", "--e2e-results", results })
                start.ArgumentList.Add(a);
            app = Process.Start(start);
            if (!Wait(() => File.Exists(Path.Combine(results, "ready")), 20_000)) return "the app never became ready";
            Thread.Sleep(3000);
            var longest = Scenarios.CardTable().Where(c => c.StartsWith("Rewrite", StringComparison.Ordinal) || c.StartsWith("Cancelled", StringComparison.Ordinal)
                || c.StartsWith("DidntHear", StringComparison.Ordinal) || c.StartsWith("MicDenied", StringComparison.Ordinal) || c.StartsWith("Busy", StringComparison.Ordinal));
            return Scenarios.CardsFit(results, longest, expectTextScale: 1.5);
        }
        finally
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Accessibility");
            key.DeleteValue("TextScaleFactor", throwOnMissingValue: false);
        }
    }

    public static bool Wait(Func<bool> condition, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }
}

internal static class Screenshot
{
    public static void Save(string path)
    {
        try
        {
            var bounds = Screen.PrimaryScreen!.Bounds;
            using var bmp = new Bitmap(bounds.Width, bounds.Height);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            bmp.Save(path);
        }
        catch (Exception)
        {
            // A screenshot is a diagnostic nicety; never fail the run over it.
        }
    }
}
