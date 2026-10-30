#if YOLIME_E2E
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Yolime.Core;
using Yolime.Core.Audio;
using Yolime.Core.Capture;
using Yolime.Core.Hotkeys;
using Yolime.Core.Llm;
using Yolime.Core.Stt;

namespace Yolime.App.E2E;

/// <summary>
/// E2E builds only: the full app, driven by SendInput from the Sandbox harness, with the mic replaced by a WAV file
/// and STT by fixed text.
/// <code>
/// Yolime.App.exe --e2e --test-mic x.wav --test-transcript text [--test-rewrite] [--test-stt-delay ms] [--test-data-dir d] [--e2e-results d] [--test-first-run]
/// </code>
/// It refuses to run outside Windows Sandbox: it accepts synthetic keystrokes, which must never happen on a real desktop.
/// </summary>
internal static class E2EMode
{
    private const string SandboxUser = "WDAGUtilityAccount";

    public static HostOverrides? Configure(string[] args)
    {
        if (!string.Equals(Environment.UserName, SandboxUser, StringComparison.OrdinalIgnoreCase)) return null;
        // Fail closed: never fall back to the real mic or the real data folder.
        if (Arg(args, "--test-mic") is not { } mic || !File.Exists(mic)) return null;
        var text = Arg(args, "--test-transcript") ?? "hello e2e";
        var delay = int.TryParse(Arg(args, "--test-stt-delay"), out var d) ? d : 0;
        var dataDir = Arg(args, "--test-data-dir") ?? Path.Combine(Path.GetTempPath(), "yolime-e2e", Guid.NewGuid().ToString("N"));
        // The harness reads the notch's pixels, which capture exclusion would hide from it.
        var roaming = Path.Combine(dataDir, "roaming");
        Directory.CreateDirectory(roaming);
        var settingsFile = Path.Combine(roaming, "settings.json");
        // Raw always previews here, so the preview scenarios reach the card without changing any other scenario. The
        // wizard is marked done (it would cover the harness's target window) unless a scenario asks for a first run.
        if (!File.Exists(settingsFile))
        {
            File.WriteAllText(settingsFile, args.Contains("--test-first-run")
                ? """{ "hideOverlayFromCapture": false, "previewModes": ["Raw"] }"""
                : args.Contains("--test-keep-mic-ready")
                    // Through the real setting, so the E2E exercises what turns the kept-ready mic on.
                    ? """{ "hideOverlayFromCapture": false, "previewModes": ["Raw"], "firstRunDone": true, "keepMicReady": true }"""
                    : """{ "hideOverlayFromCapture": false, "previewModes": ["Raw"], "firstRunDone": true }""");
        }
        ResultsDir = Arg(args, "--e2e-results");

        HookReducer.AcceptInjected = true;
        return new HostOverrides(
            new AppPaths(Path.Combine(dataDir, "roaming"), Path.Combine(dataDir, "local")),
            new FileMicDevice(mic),
            new FakeTranscriptionSessionFactory(text, delay),
            args.Contains("--test-rewrite") ? new E2ERewriter() : null,
            () => new FileRawCapture(mic));
    }

    /// <summary>
    /// The second way in, for the update scenarios: the copy under test is installed and started without arguments
    /// (by the harness, then by setup's relaunch), so its E2E settings come from %LOCALAPPDATA%\Yolime\e2e.json. Read
    /// only when --e2e is absent; the same gates apply (the Sandbox user, a mic file). It also pins the update feed:
    /// a folder whose files stand in for GitHub (the release JSON and each asset by id), so the check and download
    /// run for real against them, hash and version checks included. <paramref name="refused"/>: the file is there but
    /// can't be used, and the app must not start (never fall back to real data).
    /// </summary>
    public static HostOverrides? ConfigureFromFile(out bool refused)
    {
        refused = false;
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yolime", "e2e.json");
        if (!File.Exists(file)) return null;
        refused = true;
        if (!string.Equals(Environment.UserName, SandboxUser, StringComparison.OrdinalIgnoreCase)) return null;
        FileConfig? config;
        try { config = JsonSerializer.Deserialize<FileConfig>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
        if (config is not { TestMic: { } mic, DataDir: { } data, Results: { } results, Feed: { } feed, Owner: { } owner, Repo: { } repo }) return null;
        var args = new List<string> { "--e2e", "--test-mic", mic, "--test-transcript", config.Transcript ?? "hello e2e", "--test-rewrite", "--test-data-dir", data, "--e2e-results", results };
        if (Configure([.. args]) is not { } overrides) return null;
        refused = false;
        var http = new HttpClient(new Yolime.Core.Net.HostPinningHandler { InnerHandler = new FileFeedHandler(feed, owner, repo, config.AssetDelayMs) }) { Timeout = Timeout.InfiniteTimeSpan };
        return overrides with { UpdateHttp = http, UpdateOwner = owner, UpdateRepo = repo };
    }

    private sealed record FileConfig(string? TestMic, string? Transcript, string? DataDir, string? Results, string? Feed, string? Owner, string? Repo, int AssetDelayMs);

    /// <summary>
    /// GitHub, from a folder: "latest.json" answers releases/latest, "asset-&lt;id&gt;" answers releases/assets/&lt;id&gt;,
    /// anything else is a 404. A large asset (the setup) can be held back <c>assetDelayMs</c>, so a scenario can act while
    /// the download is in progress.
    /// </summary>
    private sealed class FileFeedHandler(string dir, string owner, string repo, int assetDelayMs) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            var prefix = $"/repos/{owner}/{repo}/releases/";
            string? file = null;
            if (path == prefix + "latest") file = Path.Combine(dir, "latest.json");
            else if (path.StartsWith(prefix + "assets/", StringComparison.Ordinal) && long.TryParse(path[(prefix.Length + 7)..], out var id)) file = Path.Combine(dir, $"asset-{id}");
            if (file is null || !File.Exists(file)) return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { RequestMessage = request };
            if (new FileInfo(file).Length > 1024 * 1024 && assetDelayMs > 0) await Task.Delay(assetDelayMs, ct);
            var content = new StreamContent(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read));
            content.Headers.ContentLength = new FileInfo(file).Length;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content, RequestMessage = request };
        }
    }

    /// <summary>Where run results go: enum names only, read by the harness.</summary>
    public static string? ResultsDir { get; private set; }

    private static int _runs;
    private static Timer? _stallWatch;

    public static void Record(CaptureCoordinator coordinator, Shell.OverlayController overlay, Updater updater, Yolime.Core.Feedback.Notice? startup)
    {
        if (ResultsDir is not { } dir) return;
        Directory.CreateDirectory(dir);
        // The update scenarios: which version runs, each update notice (the next-start one first), the flow's phases,
        // and a "check-updates" file hook so a scenario doesn't wait out the schedule's first minute.
        File.WriteAllText(Path.Combine(dir, "version.txt"), $"{updater.Current} installed={updater.Installed} elevated={updater.State.Elevated}");
        // A failure carries its result word, so a scenario can tell the failure it meant from any other.
        static string Line(Yolime.Core.Feedback.Notice n) => (n.UpdateFailure is { } f ? $"{n.Kind}:{f}" : n.Kind.ToString()) + "\n";
        if (startup is not null) Append(Path.Combine(dir, "notices.txt"), Line(startup));
        updater.NoticeShown += n => Append(Path.Combine(dir, "notices.txt"), Line(n));
        var lastPhase = "";
        updater.StateChanged += s =>
        {
            var phase = $"{s.Phase} {s.Problem}";
            if (phase == lastPhase) return; // progress alone isn't a phase
            lastPhase = phase;
            Append(Path.Combine(dir, "updates.txt"), phase + "\n");
        };
        // The harness drops a "stall-ui" file holding a duration: the UI thread then blocks that long, as a heavy
        // window or the exit flush would, to show another app's paste never waits on it.
        var ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        var stall = Path.Combine(dir, "stall-ui");
        _stallWatch = new Timer(_ =>
        {
            try
            {
                var check = Path.Combine(dir, "check-updates");
                if (File.Exists(check))
                {
                    File.Delete(check);
                    ui.TryEnqueue(() => _ = updater.CheckAsync());
                }
            }
            catch (IOException) { }
            try { CardHooks(dir, ui, overlay); }
            catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException or ArgumentException) { }
            TourHooks(dir, ui, overlay);
            try
            {
                if (!File.Exists(stall)) return;
                var ms = int.Parse(File.ReadAllText(stall).Trim(), System.Globalization.CultureInfo.InvariantCulture);
                File.Delete(stall);
                ui.TryEnqueue(() =>
                {
                    // When the stall really ran (UTC ticks), so the harness can check it covered the read.
                    var began = DateTime.UtcNow.Ticks;
                    Thread.Sleep(ms);
                    // Written whole, then moved in: the harness must never see it half-written.
                    File.WriteAllText(stall + ".tmp", $"{began} {DateTime.UtcNow.Ticks}");
                    File.Move(stall + ".tmp", stall + ".done", overwrite: true);
                });
            }
            catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException) { }
        }, null, 100, 100);
        // COM failures with where they were thrown (code locations only, no content), so a wrong-thread call the
        // product log records as a bare HRESULT can be found.
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            if (e.Exception is not System.Runtime.InteropServices.COMException com) return;
            try { File.AppendAllText(Path.Combine(dir, "com-exceptions.txt"), $"{com.HResult:X8}\n{com.StackTrace}\n---\n"); }
            catch (IOException) { }
        };
        // The overlay's state and buttons only, never its text (the same rule as the logs).
        overlay.ViewChanged += v =>
        {
            _lastView = v;
            Append(Path.Combine(dir, "overlay.txt"), $"{v.State} {string.Join(",", v.Buttons)} {(v.OfferUseRaw ? "UseRaw" : "")}\n");
        };
        File.WriteAllText(Path.Combine(dir, "overlay-hwnd"), overlay.Notch.Hwnd.ToString());
        coordinator.RunFinished += r => Write(Path.Combine(dir, $"run-{Interlocked.Increment(ref _runs)}.json"),
            JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["outcome"] = r.Outcome.ToString(),
                ["delivery"] = r.Delivery.ToString(),
                ["mode"] = r.Mode.ToString(),
                ["rewriteStage"] = r.Timing?.Stages.TryGetValue(Stage.Rewrite, out var s) == true ? s.ToString() : null,
                ["contextSource"] = r.ContextSource?.ToString(),
                ["contextChars"] = r.ContextChars.ToString(),
                ["contextNote"] = r.ContextNote.ToString(),
                ["audioBytes"] = r.Timing?.AudioBytes.ToString(),
                ["micStart"] = r.Timing?.MicStart?.ToString(),
            }));
        coordinator.NoticeShown += n => Append(Path.Combine(dir, "notices.txt"), n.Kind + "\n");
        // The tour's entry names, in capture order (the harness adds the cards from its own table).
        File.WriteAllLines(Path.Combine(dir, "tour-states.txt"), TourStates.All.Select(e => e.Name));
        File.WriteAllText(Path.Combine(dir, "ready"), "ready");
    }

    /// <summary>
    /// Card-fit hooks, each a file the harness drops into the results folder:
    /// <list type="bullet">
    /// <item>"show-card" ("Kind Mode|- Copied Retryable"): a finished run carrying that notice, through the same
    /// <see cref="Yolime.Core.Overlay.RunFinished"/> path a real one takes, so its text and buttons are the real ones;</item>
    /// <item>"probe-fit": answered in "probe-fit.done" with <see cref="Shell.OverlayController.FitProbe"/>'s numbers;</item>
    /// <item>"shrink-card" (pixels): makes every card that much too short, so the fit check can be seen failing.</item>
    /// </list>
    /// </summary>
    private static void CardHooks(string dir, Microsoft.UI.Dispatching.DispatcherQueue ui, Shell.OverlayController overlay)
    {
        var show = Path.Combine(dir, "show-card");
        if (File.Exists(show))
        {
            var parts = File.ReadAllText(show).Trim().Split(' ');
            File.Delete(show);
            var kind = Enum.Parse<Yolime.Core.Feedback.NoticeKind>(parts[0]);
            Mode? mode = parts[1] == "-" ? null : Enum.Parse<Mode>(parts[1]);
            var retryable = bool.Parse(parts[3]);
            // NoSpeech with a history id is retryable, without one it isn't: Retryable() decides the buttons as it would.
            var result = new Yolime.Core.Pipeline.RunResult { RunId = Guid.NewGuid(), Outcome = RunOutcome.NoSpeech, Mode = mode ?? Mode.Dictate, HistoryId = retryable ? 1 : null };
            overlay.Post(new Yolime.Core.Overlay.RunFinished(result, new Yolime.Core.Feedback.Notice(kind, mode, Copied: bool.Parse(parts[2])), "E2E", Shell.OverlayController.NowMs));
        }
        var probe = Path.Combine(dir, "probe-fit");
        if (File.Exists(probe))
        {
            File.Delete(probe);
            ui.TryEnqueue(() =>
            {
                // On the UI thread with nothing above it: a file error must not take the app down.
                try
                {
                    File.WriteAllText(probe + ".tmp", overlay.FitProbe());
                    File.Move(probe + ".tmp", probe + ".done", overwrite: true);
                }
                catch (IOException) { }
            });
        }
        var shrink = Path.Combine(dir, "shrink-card");
        if (File.Exists(shrink))
        {
            Shell.OverlayWindow.TestShrinkPx = int.Parse(File.ReadAllText(shrink).Trim(), System.Globalization.CultureInfo.InvariantCulture);
            File.Delete(shrink);
            File.WriteAllText(shrink + ".done", "done");
        }
    }

    private static Yolime.Core.Overlay.OverlayView? _lastView;

    /// <summary>
    /// The UI tour's hooks (docs/superpowers/plans/2026-10-30-ui-tour.md), each answered in "&lt;hook&gt;.done":
    /// <list type="bullet">
    /// <item>"tour-state" (an entry of <see cref="TourStates"/>): resets the notch to hidden, posts the entry's inputs,
    /// then answers "ok" or "mismatch …" with the state and buttons (enum words only);</item>
    /// <item>"tour-freeze" ("on"/"off"): drops the overlay timer's ticks, so a staged card stays as it is;</item>
    /// <item>"probe-theme": the notch's and pill's theme as <c>ApplyTheme</c> last set it;</item>
    /// <item>"set-capture-hidden" ("true"/"false"): both windows' capture exclusion, answered with Windows' result and the
    /// view's state.</item>
    /// </list>
    /// Errors go to "&lt;hook&gt;.error" as the exception type only: an entry's text never reaches a file.
    /// </summary>
    private static void TourHooks(string dir, Microsoft.UI.Dispatching.DispatcherQueue ui, Shell.OverlayController overlay)
    {
        Hook(dir, "tour-freeze", arg =>
        {
            Shell.OverlayController.TestFreezeTicks = arg == "on";
            return "ok";
        });
        HookOnUi(dir, "probe-theme", ui, _ => $"{overlay.TestNotchTheme}|{overlay.TestPillTheme}");
        HookOnUi(dir, "set-capture-hidden", ui, arg =>
        {
            var ok = overlay.TestSetCaptureHidden(bool.Parse(arg));
            return $"{(ok ? "ok" : "refused")} {_lastView?.State} {string.Join(",", _lastView?.Buttons ?? [])}";
        });
        var state = Path.Combine(dir, "tour-state");
        string id = "";
        try
        {
            if (Request(state) is not var (rid, name)) return;
            id = rid;
            if (TourStates.Find(name) is not { } entry)
            {
                File.WriteAllText(state + ".error", id + " unknown entry");
                return;
            }
            // All in one UI-thread step, applied directly: the answer follows the inputs, and an exception in the model
            // lands in this step's catch rather than in a dispatcher item of its own.
            HookAnswer(ui, state, id, () =>
            {
                // Updating ignores Dismissed: UpdatingEnded without a notice is what leaves it.
                foreach (var reset in new Yolime.Core.Overlay.OverlayInput[]
                         { new Yolime.Core.Overlay.UpdatingEnded(null, 0), new Yolime.Core.Overlay.PreviewEnded(), new Yolime.Core.Overlay.PendingPasteEnded(), new Yolime.Core.Overlay.Dismissed() })
                    overlay.TestApply(reset);
                foreach (var input in entry.Build(Shell.OverlayController.NowMs)) overlay.TestApply(input);
                return _lastView is { } v && TourStates.Mismatch(entry, v) is { } why ? "mismatch " + why : "ok";
            });
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(state + ".error", id + " " + ex.GetType().Name); }
            catch (IOException) { }
        }
    }

    /// <summary>
    /// Reads and removes a tour request, "&lt;id&gt; &lt;arg&gt;". Every answer starts with the id, so the harness never takes
    /// a late answer to an earlier request for the one it's waiting on. Null when there's no request.
    /// </summary>
    private static (string Id, string Arg)? Request(string path)
    {
        if (!File.Exists(path)) return null;
        // Claimed by a rename, which only one taker can win: the 100 ms hook timer's callbacks can overlap, and two
        // reading and deleting the same file raced (UnauthorizedAccessException on the second delete). A lost race, or
        // the harness still moving its request in, is "not yet": the next tick looks again.
        var taken = path + ".taken";
        try { File.Move(path, taken, overwrite: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        var text = File.ReadAllText(taken).Trim();
        File.Delete(taken);
        var space = text.IndexOf(' ');
        return space < 0 ? (text, "") : (text[..space], text[(space + 1)..]);
    }

    /// <summary>A tour hook answered on the hook timer's thread.</summary>
    private static void Hook(string dir, string name, Func<string, string> answer)
    {
        var path = Path.Combine(dir, name);
        var id = "";
        try
        {
            if (Request(path) is not var (rid, arg)) return;
            id = rid;
            WriteDone(path, id + " " + answer(arg));
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(path + ".error", id + " " + ex.GetType().Name); }
            catch (IOException) { }
        }
    }

    /// <summary>A tour hook answered on the UI thread (it reads or changes the overlay's windows).</summary>
    private static void HookOnUi(string dir, string name, Microsoft.UI.Dispatching.DispatcherQueue ui, Func<string, string> answer)
    {
        var path = Path.Combine(dir, name);
        var id = "";
        try
        {
            if (Request(path) is not var (rid, arg)) return;
            id = rid;
            HookAnswer(ui, path, id, () => answer(arg));
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(path + ".error", id + " " + ex.GetType().Name); }
            catch (IOException) { }
        }
    }

    private static void HookAnswer(Microsoft.UI.Dispatching.DispatcherQueue ui, string path, string id, Func<string> answer)
    {
        var queued = ui.TryEnqueue(() =>
        {
            // On the UI thread with nothing above it: nothing here may take the app down.
            try { WriteDone(path, id + " " + answer()); }
            catch (Exception ex)
            {
                try { File.WriteAllText(path + ".error", id + " " + ex.GetType().Name); }
                catch (IOException) { }
            }
        });
        if (!queued) File.WriteAllText(path + ".error", id + " not queued");
    }

    /// <summary>Written whole, then moved in: the harness must never read a half-written answer.</summary>
    private static void WriteDone(string path, string text)
    {
        File.WriteAllText(path + ".tmp", text);
        File.Move(path + ".tmp", path + ".done", overwrite: true);
    }

    private static void Append(string path, string text) => Retry(path, text, FileMode.Append);

    private static void Write(string path, string text) => Retry(path, text, FileMode.Create);

    /// <summary>
    /// These run on app threads with nothing above them: a sharing violation (the harness reading the file, or
    /// Defender scanning a new one) must never take the app down. Readers may share the file while it's written.
    /// </summary>
    private static void Retry(string path, string text, FileMode mode)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var f = new FileStream(path, mode, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                f.Write(System.Text.Encoding.UTF8.GetBytes(text));
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(25);
            }
        }
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>A stand-in for Claude (the Sandbox has no network): "rewritten: " plus the transcript, in two deltas.</summary>
    private sealed class E2ERewriter : IRewriter
    {
        public RewriterReadiness CheckReady() => RewriterReadiness.Ready;

        public async IAsyncEnumerable<string> StreamAsync(RewriteRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Delay(200, ct);
            yield return "rewritten: ";
            yield return request.Transcript;
        }
    }

    /// <summary>
    /// A kept-ready "mic": plays a WAV in real time, looping, from open to dispose, whether or not a hold is active (as
    /// a real open mic does), stamping each buffer with its capture time. Each open is counted for the harness.
    /// </summary>
    private sealed class FileRawCapture : IRawCapture
    {
        private readonly CancellationTokenSource _stop = new();

        public event RawBufferHandler? Buffer;
        public event Action<AudioCaptureException>? Failed;

        public MicKind Kind => MicKind.Unknown;

        public FileRawCapture(string wav)
        {
            if (ResultsDir is { } dir) Append(Path.Combine(dir, "mic-opens.txt"), "open\n");
            var pcm = WavFile.ReadPcm(wav);
            _ = Task.Run(async () =>
            {
                try
                {
                    for (var o = 0; !_stop.IsCancellationRequested; o = (o + 3200) % Math.Max(3200, pcm.Length - 3200))
                    {
                        var capturedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                        await Task.Delay(100, _stop.Token);
                        Buffer?.Invoke(pcm.AsSpan(o, Math.Min(3200, pcm.Length - o)), capturedAt, silent: false);
                    }
                }
                catch (OperationCanceledException) { }
            });
            _ = Failed; // never fails
        }

        public ValueTask DisposeAsync()
        {
            _stop.Cancel();
            if (ResultsDir is { } dir) Append(Path.Combine(dir, "mic-opens.txt"), "close\n");
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A "mic" that plays a WAV in real time, looping, until stopped.</summary>
    private sealed class FileMicDevice(string wav) : ICaptureDevice
    {
        public ILiveCapture Start() => new Live(wav);

        private sealed class Live : ILiveCapture
        {
            private readonly Channel<AudioChunk> _chunks = Channel.CreateUnbounded<AudioChunk>();
            private readonly CancellationTokenSource _stop = new();

            public Live(string wav)
            {
                var pcm = WavFile.ReadPcm(wav);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        for (var o = 0; !_stop.IsCancellationRequested; o = (o + 3200) % Math.Max(3200, pcm.Length - 3200))
                        {
                            var chunk = pcm.AsMemory(o, Math.Min(3200, pcm.Length - o)).ToArray();
                            _chunks.Writer.TryWrite(new AudioChunk(chunk, Rms.Of(chunk)));
                            await Task.Delay(100, _stop.Token);
                        }
                    }
                    catch (OperationCanceledException) { }
                    _chunks.Writer.TryComplete();
                });
            }

            public void Stop() => _stop.Cancel();

            public async IAsyncEnumerable<AudioChunk> ReadAsync([EnumeratorCancellation] CancellationToken ct)
            {
                await foreach (var c in _chunks.Reader.ReadAllAsync(ct)) yield return c;
            }

            public ValueTask DisposeAsync()
            {
                _stop.Cancel();
                return ValueTask.CompletedTask;
            }
        }
    }
}
#endif
