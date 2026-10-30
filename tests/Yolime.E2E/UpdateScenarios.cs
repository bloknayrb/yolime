using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Yolime.E2E;

/// <summary>
/// The in-app update scenarios (run-e2e.ps1 -Updates), each on a fresh install: an E2E build of 0.1.0 seeded with
/// install.ps1, started without arguments under a lowered token (its settings come from e2e.json), offered 0.1.1 by a
/// file-based feed, and installed through the tray item and the update window, with the real setup.
/// </summary>
internal static class UpdateScenarios
{
    private const string Results = @"C:\results";
    // The read-only stage is copied here first: the lowered copies (and setup) may not be allowed to read the
    // Sandbox's mapped folders, and a real install reads from the user's own disk anyway.
    private const string Mapped = @"C:\e2e";
    private const string Root = @"C:\upstage";
    private const string LocalResults = @"C:\upres";
    private const string DataDir = @"C:\updata";
    private const string Owner = "e2e-owner";
    private const string Repo = "yolime";
    private const int CmdInstallUpdate = 9;
    private const string ChildTitle = "yolime-e2e-child";

    private static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string InstallDir = Path.Combine(Local, "Programs", "Yolime");
    private static readonly string InstalledExe = Path.Combine(InstallDir, "Yolime.App.exe");
    private static readonly string UpdatesDir = Path.Combine(DataDir, "local", "updates");
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static int Run()
    {
        Directory.CreateDirectory(Results);
        File.WriteAllText(Path.Combine(Results, "started"), "started");
        var report = new List<object>();
        foreach (var dir in new[] { "harness", "pay-0.1.0", "setups", "tools", "fixtures" })
            CopyTree(Path.Combine(Mapped, dir), Path.Combine(Root, dir));
        File.WriteAllText(Path.Combine(Results, "desktop.txt"), LoweredToken.PrepareDesktop());
        var only = File.Exists(Path.Combine(Results, "only.txt"))
            ? File.ReadAllLines(Path.Combine(Results, "only.txt")).Where(l => l.Length > 0).ToHashSet(StringComparer.Ordinal) : null;
        var scenarios = new (string Name, Func<Context, string?> Run)[]
        {
            ("Update_Elevated_ShowsTheAdminNotice", Elevated),
            ("Update_ChecksumMismatch_Refused", ChecksumMismatch),
            ("Update_RefusesHoldsWhileUpdating", RefusesHolds),
            ("Update_RefusedPayload_KeepsRunning", RefusedPayload),
            ("Update_FailsAfterStop_Relaunches", FailsAfterStop),
            ("Update_InstallsTheNewerSetup", InstallsTheNewerSetup),
        };
        try
        {
            foreach (var (name, run) in scenarios.Where(s => only is null || only.Contains(s.Name)))
            {
                var ctx = new Context(name);
                string? failure;
                try { failure = run(ctx); }
                catch (Exception ex) { failure = "threw " + ex.GetType().Name + ": " + ex.Message; }
                if (failure is not null) Screenshot.Save(Path.Combine(Results, name + ".png"));
                ctx.Collect();
                Cleanup();
                report.Add(new { name, pass = failure is null, failure });
            }
        }
        catch (Exception ex)
        {
            report.Add(new { name = "harness", pass = false, failure = ex.GetType().Name + ": " + ex.Message });
        }
        finally
        {
            File.WriteAllText(Path.Combine(Results, "e2e.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(Results, "DONE"), "done");
        }
        return 0;
    }

    // ------------------------------------------------------------------ scenarios

    /// <summary>A full (elevated) token: the release is found, but the copy says it can't install it, and offers nothing.</summary>
    private static string? Elevated(Context c)
    {
        if (c.Prepare(Payload.Good) is { } fail) return fail;
        var pid = Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = false, WorkingDirectory = InstallDir })!.Id;
        if (LoweredToken.Read(pid) is not { Integrity: > 0x2000 }) return "the harness's own token isn't elevated, so this can't test an elevated copy";
        if (c.WaitReady() is { } notReady) return notReady;
        if (!c.Text("version.txt").Contains("elevated=True", StringComparison.Ordinal)) return "the copy didn't see itself as elevated: " + c.Text("version.txt");
        c.CheckForUpdates();
        if (!Program.Wait(() => c.Notices().Contains("UpdateAvailableElevated"), 15_000)) return "no admin notice; notices: " + string.Join(",", c.Notices());
        if (c.Notices().Contains("UpdateAvailable")) return "it offered the update as well";
        return c.Updates().Any(l => l.StartsWith("Downloading", StringComparison.Ordinal)) ? "it started a download" : null;
    }

    /// <summary>A sums file with the wrong hash: the download is refused and setup never starts.</summary>
    private static string? ChecksumMismatch(Context c)
    {
        if (c.Prepare(Payload.Good, wrongHash: true) is { } fail) return fail;
        var pid = LoweredToken.Start(InstalledExe, null, InstallDir);
        if (c.Started(pid) is { } bad) return bad;
        if (c.Install(pid) is { } notInstalled) return notInstalled;
        if (!Program.Wait(() => c.Updates().Contains("Failed ChecksumMismatch"), 60_000)) return "never refused; phases: " + string.Join(",", c.Updates());
        using var automation = new FlaUI.UIA3.UIA3Automation();
        var window = automation.GetDesktop().FindFirstChild(cf => cf.ByName("yolime update"));
        if (window?.FindFirstDescendant(cf => cf.ByName("The 0.1.1 download didn't match its checksum, so it wasn't installed.")) is null)
            return "the window doesn't say the checksum didn't match";
        if (File.Exists(Path.Combine(UpdatesDir, "expected-version"))) return "the marker was written";
        if (Process.GetProcesses().Any(p => p.ProcessName.StartsWith("yolime-setup", StringComparison.OrdinalIgnoreCase))) return "setup is running";
        if (c.Overlay().Any(l => l.StartsWith("Updating", StringComparison.Ordinal))) return "the Updating card showed";
        if (File.Exists(Path.Combine(UpdatesDir, "yolime-setup-0.1.1.exe"))) return "the refused setup was kept";
        return Alive(pid) ? null : "the copy exited";
    }

    /// <summary>
    /// A hold works into a medium-integrity window first (so the hook can see it at all), then, once Install has
    /// started (the download is held back), a hold does nothing and the notch says why.
    /// </summary>
    private static string? RefusesHolds(Context c)
    {
        if (c.Prepare(Payload.Good, assetDelayMs: 60_000, quietOffer: true) is { } fail) return fail;
        var pid = LoweredToken.Start(InstalledExe, null, InstallDir);
        if (c.Started(pid) is { } bad) return bad;
        var child = c.StartChild();
        if (child.Failure is { } noChild) return noChild;
        // The startup balloon arrives late in a fresh Sandbox and would discard a hold it lands in.
        Thread.Sleep(20_000);
        if (c.HoldInto(child.Hwnd, expectRun: true) is { } control) return "control hold: " + control;

        if (c.Install(pid) is { } notInstalled) return notInstalled;
        if (!Program.Wait(() => c.Updates().Contains("Downloading "), 10_000)) return "never started downloading; phases: " + string.Join(",", c.Updates());
        var runs = c.Runs();
        var text = c.ChildText();
        if (c.HoldInto(child.Hwnd, expectRun: false) is { } refused) return refused;
        if (c.Runs() != runs) return "a run happened while installing";
        if (c.ChildText() != text) return "text reached the window while installing";
        return Program.Wait(() => c.Notices().Contains("UpdateHoldRefused"), 3000) ? null : "the refusal wasn't said";
    }

    /// <summary>
    /// A payload setup refuses (no .pri) before it stops anything: 0.1.0 keeps running, the Updating card becomes the
    /// failure warning, and holds work again.
    /// </summary>
    private static string? RefusedPayload(Context c)
    {
        if (c.Prepare(Payload.NoPri, quietOffer: true) is { } fail) return fail;
        var pid = LoweredToken.Start(InstalledExe, null, InstallDir);
        if (c.Started(pid) is { } bad) return bad;
        var child = c.StartChild();
        if (child.Failure is { } noChild) return noChild;
        Thread.Sleep(20_000);
        if (c.Install(pid) is { } notInstalled) return notInstalled;
        if (!Program.Wait(() => c.Updates().Contains("Failed InstallFailed"), 180_000)) return "setup never failed; phases: " + string.Join(",", c.Updates());
        if (!Alive(pid)) return "0.1.0 didn't keep running";
        if (!Program.Wait(() => c.Notices().Contains("UpdateFailed:CheckFailed"), 10_000)) return "not refused by the payload check; notices: " + string.Join(",", c.Notices());
        var overlay = c.Overlay();
        var updating = overlay.FindIndex(l => l.StartsWith("Updating", StringComparison.Ordinal));
        if (updating < 0) return "no Updating card; overlay: " + string.Join(" | ", overlay);
        if (!overlay.Skip(updating).Any(l => l.StartsWith("Error OpenLog,Dismiss", StringComparison.Ordinal))) return "no failure warning; overlay: " + string.Join(" | ", overlay);
        if (Process.GetProcessesByName("Yolime.App").Length != 1) return "a second copy was started";
        return c.HoldInto(child.Hwnd, expectRun: true) is { } after ? "holds didn't come back: " + after : null;
    }

    /// <summary>
    /// A file held open in the install folder makes the swap fail after yolime was stopped: setup starts 0.1.0 again,
    /// and that start says the update didn't install.
    /// </summary>
    private static string? FailsAfterStop(Context c)
    {
        if (c.Prepare(Payload.Good) is { } fail) return fail;
        var pid = LoweredToken.Start(InstalledExe, null, InstallDir);
        if (c.Started(pid) is { } bad) return bad;
        using var held = new FileStream(Path.Combine(InstallDir, "held-by-the-harness.txt"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        c.DeleteReady();
        if (c.Install(pid) is { } notInstalled) return notInstalled;
        if (!Program.Wait(() => !Alive(pid), 120_000)) return "0.1.0 was never stopped; phases: " + string.Join(",", c.Updates());
        if (!Program.Wait(() => c.Exists("ready"), 180_000)) return "nothing was started again";
        var copies = Process.GetProcessesByName("Yolime.App");
        if (copies.Length != 1) return $"{copies.Length} copies running";
        if (!c.Text("version.txt").StartsWith("0.1.0", StringComparison.Ordinal)) return "the relaunched copy is " + c.Text("version.txt");
        if (!Program.Wait(() => c.Notices().Contains("UpdateFailed:SwapFailed"), 10_000)) return "the relaunched copy didn't say the update failed; notices: " + string.Join(",", c.Notices());
        return LoweredToken.CheckLowered(copies[0].Id) is { } t ? "the relaunched copy: " + t : null;
    }

    /// <summary>
    /// The whole update: start-with-Windows on, Install, and 0.1.1 runs from the install folder, says "Updated to",
    /// and start-with-Windows still points at it.
    /// </summary>
    private static string? InstallsTheNewerSetup(Context c)
    {
        if (c.Prepare(Payload.Good) is { } fail) return fail;
        var runValue = "\"" + InstalledExe + "\"";
        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey)) key.SetValue("yolime", runValue);
        var pid = LoweredToken.Start(InstalledExe, null, InstallDir);
        if (c.Started(pid) is { } bad) return bad;
        c.DeleteReady();
        if (c.Install(pid, expectBalloon: true) is { } notInstalled) return notInstalled;
        if (!Program.Wait(() => !Alive(pid), 120_000)) return "0.1.0 never exited; phases: " + string.Join(",", c.Updates());
        if (!c.Overlay().Any(l => l.StartsWith("Updating", StringComparison.Ordinal))) return "no Updating card before the exit";
        if (!Program.Wait(() => c.Exists("ready"), 180_000)) return "the new version never started";
        var copies = Process.GetProcessesByName("Yolime.App");
        if (copies.Length != 1) return $"{copies.Length} copies running";
        string? path = null;
        try { path = copies[0].MainModule?.FileName; } catch (System.ComponentModel.Win32Exception) { }
        if (!string.Equals(path, InstalledExe, StringComparison.OrdinalIgnoreCase)) return "running from " + path;
        if (!c.Text("version.txt").StartsWith("0.1.1", StringComparison.Ordinal)) return "running " + c.Text("version.txt");
        if (FileVersionInfo.GetVersionInfo(InstalledExe).ProductVersion?.StartsWith("0.1.1", StringComparison.Ordinal) != true) return "the installed exe isn't 0.1.1";
        if (!Program.Wait(() => c.Notices().Contains("Updated"), 10_000)) return "no Updated notice; notices: " + string.Join(",", c.Notices());
        using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
            if (key?.GetValue("yolime") as string != runValue) return "start-with-Windows is now " + key?.GetValue("yolime");
        if (Directory.Exists(InstallDir + ".old") || Directory.Exists(InstallDir + ".new")) return "a .old or .new folder was left";
        return LoweredToken.CheckLowered(copies[0].Id) is { } t ? "the new copy: " + t : null;
    }

    // ------------------------------------------------------------------ plumbing

    private enum Payload { Good, NoPri }

    private sealed class Context(string name)
    {
        public string AppResults { get; } = Path.Combine(LocalResults, name, "app");
        private string Feed { get; } = Path.Combine(@"C:\feed", name);

        /// <summary>A fresh install of 0.1.0, a feed offering 0.1.1, and e2e.json pointing the copy at both.</summary>
        public string? Prepare(Payload payload, bool wrongHash = false, int assetDelayMs = 0, bool quietOffer = false)
        {
            Cleanup();
            Directory.CreateDirectory(AppResults);
            // Seeded the way a user installs: install.ps1 from a normal (lowered) PowerShell.
            var seedLog = Path.Combine(LocalResults, name, "seed.txt");
            var seed = LoweredToken.Run(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
                $@"-NoProfile -ExecutionPolicy Bypass -Command ""& '{Root}\tools\install.ps1' -From '{Root}\pay-0.1.0' -NoLaunch -AllowE2EPayload *> '{seedLog}'; exit $LASTEXITCODE""",
                Root, 300_000);
            if (seed != 0) return $"seeding 0.1.0 failed (exit {seed}): " + Read(seedLog);

            Directory.CreateDirectory(Feed);
            var setup = Path.Combine(Root, "setups", payload == Payload.NoPri ? "nopri" : "good", "yolime-setup-0.1.1.exe");
            File.Copy(setup, Path.Combine(Feed, "asset-11"), overwrite: true);
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(setup))).ToLowerInvariant();
            if (wrongHash) hash = new string('0', 64);
            File.WriteAllText(Path.Combine(Feed, "asset-12"), $"{hash}  yolime-setup-0.1.1.exe\n");
            var bot = new { login = "github-actions[bot]" };
            File.WriteAllText(Path.Combine(Feed, "latest.json"), JsonSerializer.Serialize(new
            {
                tag_name = "v0.1.1",
                draft = false,
                prerelease = false,
                assets = new object[]
                {
                    new { id = 11, name = "yolime-setup-0.1.1.exe", size = new FileInfo(setup).Length, uploader = bot },
                    new { id = 12, name = "SHA256SUMS.txt", size = new FileInfo(Path.Combine(Feed, "asset-12")).Length, uploader = bot },
                },
            }));

            // quietOffer: the release counts as announced, so no balloon arrives mid-hold (a toast discards a hold).
            var roaming = Path.Combine(DataDir, "roaming");
            Directory.CreateDirectory(roaming);
            File.WriteAllText(Path.Combine(roaming, "settings.json"), JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["hideOverlayFromCapture"] = false,
                ["previewModes"] = Array.Empty<string>(),
                ["firstRunDone"] = true,
                ["lastNotifiedUpdate"] = quietOffer ? "0.1.1" : "0.0.0",
            }));
            Directory.CreateDirectory(Path.Combine(Local, "Yolime"));
            File.WriteAllText(Path.Combine(Local, "Yolime", "e2e.json"), JsonSerializer.Serialize(new
            {
                testMic = $@"{Root}\fixtures\speech-short.wav",
                transcript = Scenarios.Spoken,
                dataDir = DataDir,
                results = AppResults,
                feed = Feed,
                owner = Owner,
                repo = Repo,
                assetDelayMs,
            }));
            return null;
        }

        /// <summary>The copy is lowered as asked, and ready.</summary>
        public string? Started(int pid)
        {
            if (LoweredToken.CheckLowered(pid) is { } token) return token;
            if (WaitReady() is { } notReady) return notReady;
            return Text("version.txt").Contains("installed=True elevated=False", StringComparison.Ordinal) ? null : "the copy says " + Text("version.txt");
        }

        public string? WaitReady() => Program.Wait(() => Exists("ready"), 30_000) ? null : "the copy never became ready";

        public void DeleteReady() => File.Delete(Path.Combine(AppResults, "ready"));

        public void CheckForUpdates() => File.WriteAllText(Path.Combine(AppResults, "check-updates"), "now");

        /// <summary>Check, wait for the offer, then the tray's "Install update…" and the window's Install now.</summary>
        public string? Install(int pid, bool expectBalloon = false)
        {
            CheckForUpdates();
            if (!Program.Wait(() => Updates().Contains("Available "), 20_000)) return "no update was offered; phases: " + string.Join(",", Updates());
            if (expectBalloon && !Program.Wait(() => Notices().Contains("UpdateAvailable"), 5000)) return "no balloon for the offer";
            if (!PostTray(pid, CmdInstallUpdate)) return "no tray window";
            using var automation = new FlaUI.UIA3.UIA3Automation();
            FlaUI.Core.AutomationElements.AutomationElement? window = null;
            if (!Program.Wait(() => (window = automation.GetDesktop().FindFirstChild(cf => cf.ByName("yolime update"))) is not null, 15_000)) return "the update window never opened";
            if (window!.FindFirstDescendant(cf => cf.ByName("Install yolime 0.1.1?")) is null) return "the window doesn't ask to install 0.1.1";
            var install = window.FindFirstDescendant(cf => cf.ByName("Install now"));
            if (install is null) return "no Install now";
            install.Patterns.Invoke.Pattern.Invoke();
            return null;
        }

        public (nint Hwnd, string? Failure) StartChild()
        {
            var textFile = Path.Combine(LocalResults, name, "child.txt");
            var pid = LoweredToken.Start(Path.Combine(Root, "harness", "Yolime.E2E.exe"), $"--target \"{textFile}\"", Path.Combine(Root, "harness"));
            if (LoweredToken.CheckLowered(pid) is { } t) return (0, "target child: " + t);
            nint hwnd = 0;
            if (!Program.Wait(() => (hwnd = FindWindowW(null, ChildTitle)) != 0, 15_000)) return (0, "the target child never showed");
            return (hwnd, null);
        }

        public string ChildText()
        {
            var f = Path.Combine(LocalResults, name, "child.txt");
            for (var i = 0; i < 10; i++)
            {
                try { return File.Exists(f) ? File.ReadAllText(f) : ""; }
                catch (IOException) { Thread.Sleep(50); }
            }
            return "";
        }

        /// <summary>Clicks into the child, holds the hold key, and checks for (or against) a run and its text.</summary>
        public string? HoldInto(nint hwnd, bool expectRun)
        {
            if (!FocusChild(hwnd)) return "couldn't bring the target child forward";
            var runs = Runs();
            var before = ChildText();
            Input.Hold(Input.RCtrl, 1500);
            if (!expectRun) return Program.Wait(() => Runs() > runs, 5000) ? "a run happened" : null;
            if (!Program.Wait(() => Runs() > runs, 15_000)) return "no run finished";
            return Program.Wait(() => ChildText().Length > before.Length, 8000) ? null : "nothing was pasted into the child";
        }

        public int Runs() => Directory.Exists(AppResults) ? Directory.GetFiles(AppResults, "run-*.json").Length : 0;

        public bool Exists(string file) => File.Exists(Path.Combine(AppResults, file));

        public string Text(string file) => Read(Path.Combine(AppResults, file));

        public List<string> Notices() => Lines("notices.txt");

        public List<string> Updates() => Lines("updates.txt");

        public List<string> Overlay() => Lines("overlay.txt");

        private List<string> Lines(string file) => Read(Path.Combine(AppResults, file)).Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();

        /// <summary>The copy's logs and setup's log, for a failure the report can't explain.</summary>
        public void Collect()
        {
            try
            {
                var to = Path.Combine(Results, name);
                CopyTree(Path.Combine(LocalResults, name), to);
                foreach (var dir in new[] { Path.Combine(DataDir, "local", "logs"), UpdatesDir })
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var f in Directory.GetFiles(dir).Where(f => !f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                        File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: true);
                }
            }
            catch (IOException) { }
        }
    }

    private static string Read(string path)
    {
        for (var i = 0; i < 20; i++)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
            catch (IOException) { Thread.Sleep(50); }
        }
        return "";
    }

    private static bool Alive(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>Stops every copy, setup and script, and removes the install, its data and the feed.</summary>
    private static void Cleanup()
    {
        foreach (var p in Process.GetProcesses().Where(p => p.ProcessName is "Yolime.App" or "powershell" || p.ProcessName.StartsWith("yolime-setup", StringComparison.OrdinalIgnoreCase)))
        {
            try { p.Kill(); p.WaitForExit(5000); } catch (Exception) { }
        }
        foreach (var p in Process.GetProcessesByName("Yolime.E2E").Where(p => p.Id != Environment.ProcessId))
        {
            try { p.Kill(); } catch (Exception) { }
        }
        Thread.Sleep(1000);
        foreach (var dir in new[] { InstallDir, InstallDir + ".new", InstallDir + ".old", DataDir, Path.Combine(Local, "Yolime"), @"C:\feed" })
        {
            for (var i = 0; i < 5 && Directory.Exists(dir); i++)
            {
                try { Directory.Delete(dir, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(500); }
            }
        }
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        key.DeleteValue("yolime", throwOnMissingValue: false);
    }

    private static void CopyTree(string from, string to)
    {
        if (!Directory.Exists(from)) return;
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.GetDirectories(from)) CopyTree(d, Path.Combine(to, Path.GetFileName(d)));
    }

    /// <summary>Posts a tray command to the copy with that pid, as the menu would run it.</summary>
    private static bool PostTray(int pid, int command)
    {
        nint hwnd = 0;
        while ((hwnd = FindWindowExW(0, hwnd, "yolime-tray", "yolime")) != 0)
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner == pid) return PostMessageW(hwnd, 0x0111, command, 0);
        }
        return false;
    }

    /// <summary>
    /// A click in the child's client area: it runs at Medium, so the harness (High) can't hand it the foreground any
    /// other way, but a click is input like any other.
    /// </summary>
    private static bool FocusChild(nint hwnd)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (GetForegroundWindow() == hwnd) return true;
            GetClientRect(hwnd, out var rect);
            var centre = new PointStruct { X = rect.Right / 2, Y = rect.Bottom / 2 };
            ClientToScreen(hwnd, ref centre);
            Input.Click(centre.X, centre.Y);
            Thread.Sleep(400);
        }
        return GetForegroundWindow() == hwnd;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RectStruct { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct PointStruct { public int X, Y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowW(string? cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowExW(nint parent, nint after, string cls, string title);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out int pid);
    [DllImport("user32.dll")] private static extern bool PostMessageW(nint hwnd, uint msg, nint w, nint l);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out RectStruct rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref PointStruct point);
}

/// <summary>
/// The update scenarios' target window, started by the harness as its own process under the lowered token: a
/// medium-integrity copy's keyboard hook never sees input going to a higher-integrity window (the harness's own).
/// Whatever lands in its box is written to a file the harness reads.
/// </summary>
internal static class TargetChild
{
    public static int Run(string textFile)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        var box = new TextBox { Multiline = true, Dock = DockStyle.Fill, Name = "child" };
        var form = new Form { Text = "yolime-e2e-child", Width = 600, Height = 240, StartPosition = FormStartPosition.CenterScreen, TopMost = true };
        form.Controls.Add(box);
        box.TextChanged += (_, _) =>
        {
            for (var i = 0; i < 10; i++)
            {
                try
                {
                    File.WriteAllText(textFile, box.Text);
                    return;
                }
                catch (IOException) { Thread.Sleep(30); }
            }
        };
        Application.Run(form);
        return 0;
    }
}
