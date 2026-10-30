using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.UI.Dispatching;
using Yolime.Core;
using Yolime.Core.Feedback;
using Yolime.Core.Logging;
using Yolime.Core.Overlay;
using Yolime.Core.Settings;
using Yolime.Core.Updates;

namespace Yolime.App;

/// <summary>
/// The in-app updater, on the UI thread: the daily check, the offer, and Install (wait for a dictation, download,
/// verify, launch setup, then stay up refusing holds until setup asks yolime to exit through the tray). Every
/// decision is <see cref="UpdateState"/>'s; this class only does the I/O and the timing.
/// </summary>
internal sealed class Updater : IDisposable
{
    private readonly DispatcherQueue _ui;
    private readonly UpdateClient _client;
    private readonly SettingsHolder _settings;
    private readonly ILog _log;
    private readonly string _dir;
    private readonly Func<bool> _dictationInProgress;
    private readonly Func<bool> _hotkeysPaused;
    private readonly string _owner, _repo;
    private readonly TimeProvider _time = TimeProvider.System;
    private readonly DateTimeOffset _startedAt;
    private Timer? _schedule;
    private DispatcherQueueTimer? _deadline;
    private Process? _setup;
    private int _cleanupTries;
    private Timer? _cleanup;
    private bool _disposed;
    private bool _answered;

    public Updater(DispatcherQueue ui, UpdateClient client, SettingsHolder settings, ILog log, string updatesDir, bool elevated,
        Func<bool> dictationInProgress, Func<bool> hotkeysPaused, string owner, string repo)
    {
        _ui = ui;
        _client = client;
        _settings = settings;
        _log = log;
        _dir = updatesDir;
        _dictationInProgress = dictationInProgress;
        _hotkeysPaused = hotkeysPaused;
        _owner = owner;
        _repo = repo;
        _startedAt = _time.GetUtcNow();
        Product = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Current = AppVersion.FromProductVersion(Product);
        Installed = Current is not null && UpdateGate.IsInstalled(Path.GetDirectoryName(Environment.ProcessPath) ?? "",
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        State = UpdateState.Initial(elevated);
    }

    /// <summary>The running build's ProductVersion ("0.1.0+&lt;commit&gt;"), for Settings.</summary>
    public string? Product { get; }

    public AppVersion? Current { get; }

    /// <summary>Only the copy in %LOCALAPPDATA%\Programs\Yolime checks or installs anything.</summary>
    public bool Installed { get; }

    public UpdateState State { get; private set; }

    /// <summary>After every change, on the UI thread.</summary>
    public event Action<UpdateState>? StateChanged;

    /// <summary>A notice for a balloon (the offer, next-start results).</summary>
    public event Action<Notice>? Balloon;

    /// <summary>The Updating card and what it turns into.</summary>
    public event Action<OverlayInput>? Overlay;

    /// <summary>Every update notice, balloon or card (the E2E build records them).</summary>
    public event Action<Notice>? NoticeShown;


    private static long NowMs => Environment.TickCount64;

    public string ReleasePage(AppVersion v) => UpdateTexts.ReleasePage(_owner, _repo, v);

    /// <summary>
    /// How the last update went, read once at start (the marker and result file are deleted). Null when none was
    /// tried, so the usual Started notice shows.
    /// </summary>
    public Notice? StartupNotice()
    {
        if (Current is not { } running) return null;
        var outcome = UpdateFiles.ReadAndClear(_dir, running);
        if (outcome is null) return null;
        if (outcome.Succeeded)
        {
            _log.Write(LogEvent.UpdateInstalled, LogField.Count("major", running.Major), LogField.Count("minor", running.Minor), LogField.Count("patch", running.Patch));
            return new Notice(NoticeKind.Updated, Version: outcome.Expected);
        }
        _log.Write(LogEvent.UpdateInstallFailed, LogField.Tag("failure", outcome.Failure!.Value));
        return new Notice(NoticeKind.UpdateFailed, Version: outcome.Expected, Current: running, UpdateFailure: outcome.Failure);
    }

    /// <summary>Starts the schedule and the clean-up of old downloads.</summary>
    public void Start()
    {
        Reschedule();
        Cleanup();
    }

    /// <summary>
    /// Arms the next automatic check from the saved times. Called at start, after each check, when the setting changes,
    /// and on resume (a check that fell due while paused or asleep runs then).
    /// </summary>
    public void Reschedule()
    {
        _schedule?.Dispose();
        _schedule = null;
        if (_disposed || !Installed || !_settings.Current.CheckForUpdates) return;
        var now = _time.GetUtcNow();
        var s = _settings.Current;
        // Only until this run's first check answers: an offer still owed after it (the release was pulled) must not
        // keep the schedule at "a minute after start", which is already past.
        // The admin copy's own earlier failure doesn't count either; one since its last answer does.
        var owed = !_answered && UpdateGate.OwedOffer(s, Current, State.Elevated);
        var lastChecked = owed ? null : s.LastUpdateCheck;
        // A check stamped in the future (the clock ran ahead) would make every new failure look older: keep the back-off.
        var lastFailed = owed && s.LastUpdateFailure <= s.LastUpdateCheck && s.LastUpdateCheck <= now ? null : s.LastUpdateFailure;
        var delay = UpdateSchedule.NextCheck(now, _startedAt, lastChecked, lastFailed) - now;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        _schedule = new Timer(_ => _ui.TryEnqueue(Due), null, delay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Hotkeys came back on, or the session was unlocked or woke: a check missed meanwhile runs now.</summary>
    public void Resumed() => Reschedule();

    private void Due()
    {
        if (_disposed) return;
        // "Pause hotkeys" means yolime is meant to be quiet: the check waits for Resumed, which re-arms the schedule
        // (still due, so it runs at once).
        if (_hotkeysPaused()) return;
        _ = CheckAsync();
    }

    /// <summary>Settings' "Check now", and the schedule. Nothing happens unless this copy is installed.</summary>
    public async Task CheckAsync()
    {
        if (!Installed || Current is not { } current) return;
        var started = State.CheckStarted();
        if (ReferenceEquals(started, State)) return;
        Set(started);
        CheckResult result;
        try
        {
            result = await _client.CheckAsync(current, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.Write(LogEvent.UnhandledException, LogField.Error(ex));
            result = new CheckResult(CheckOutcome.TryLater);
        }
        var now = _time.GetUtcNow();
        _settings.Update(s => result.Outcome == CheckOutcome.TryLater ? s with { LastUpdateFailure = now } : s with { LastUpdateCheck = now });
        if (result.Outcome != CheckOutcome.TryLater) _answered = true;
        Set(State.CheckFinished(result));
        if (State.Release is { } release && result.Outcome == CheckOutcome.Available) Announce(release.Version);
        Reschedule();
    }

    /// <summary>One balloon per version: the offer, or (elevated) why it can't be installed from here.</summary>
    private void Announce(AppVersion v)
    {
        if (UpdateGate.Announcement(v, State.Elevated, _settings.Current).Notice is not { } notice) return;
        _settings.Update(s => UpdateGate.Announcement(v, State.Elevated, s).Next);
        Tell(notice);
    }

    private void Tell(Notice notice)
    {
        Balloon?.Invoke(notice);
        NoticeShown?.Invoke(notice);
    }

    /// <summary>Whether the update window is open (it shows a failure in place; otherwise a balloon says it).</summary>
    public Func<bool> WindowOpen { get; set; } = () => false;

    /// <summary>A hold refused while installing (on the hook thread: only hands off).</summary>
    public void HoldRefused() => _ui.TryEnqueue(() =>
    {
        // The Updating card already says it; before then (waiting, downloading) the notch says why nothing happened.
        if (State.Phase == UpdatePhase.Updating) return;
        var notice = new Notice(NoticeKind.UpdateHoldRefused);
        Overlay?.Invoke(new NoticeOnly(notice, NowMs));
        NoticeShown?.Invoke(notice);
    });

    /// <summary>Install now / Try again. A second click while one is in progress does nothing.</summary>
    public async void Install()
    {
        var requested = State.InstallRequested(_dictationInProgress());
        if (ReferenceEquals(requested, State)) return;
        Set(requested);
        try
        {
            while (State.Phase == UpdatePhase.Finishing)
            {
                await Task.Delay(250);
                // yolime was asked to exit: nothing more may start, least of all setup (which would relaunch it).
                if (_disposed) return;
                if (!_dictationInProgress()) Set(State.DictationFinished());
            }
            if (State.Phase != UpdatePhase.Downloading || State.Release is not { } release || State.SetupHash is not { } hash) return;
            // Whole percents only: every 80 KB read reports, and each change redraws the window and the tray.
            var progress = new Progress<double>(p =>
            {
                if ((int)(p * 100) != (int)(State.Progress * 100)) Set(State.DownloadProgress(p));
            });
            var download = await _client.DownloadAsync(release, hash, _dir, progress, CancellationToken.None);
            if (_disposed) return;
            Set(State.DownloadFinished(download.Outcome));
            if (download.Outcome != DownloadOutcome.Ready || download.Path is not { } path) return;
            Launch(path, release.Version, hash);
        }
        catch (Exception ex)
        {
            _log.Write(LogEvent.UnhandledException, LogField.Error(ex));
            UpdateFiles.Abandon(_dir);
            if (State.Phase is UpdatePhase.Finishing) Set(State.DictationFinished());
            Set(State.LaunchFailed());
        }
    }

    /// <summary>
    /// Install steps 1–2: clear the last result and log, write the expected-version marker, then start setup from the
    /// checked file, held open (read sharing only) from its last hash until it has started, so what runs is what was
    /// checked.
    /// </summary>
    private void Launch(string setup, AppVersion version, string hash)
    {
        if (!UpdateFiles.Prepare(_dir, version))
        {
            Set(State.LaunchFailed());
            return;
        }
        Process? process = null;
        try
        {
            using var held = new FileStream(setup, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(held)), hash, StringComparison.OrdinalIgnoreCase))
            {
                UpdateFiles.Abandon(_dir);
                Set(State.DownloadFinished(DownloadOutcome.ChecksumMismatch));
                return;
            }
            var start = new ProcessStartInfo(Path.GetFullPath(setup)) { UseShellExecute = false, WorkingDirectory = _dir };
            foreach (var arg in UpdateFiles.SetupArguments(_dir)) start.ArgumentList.Add(arg);
            process = Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log.Write(LogEvent.UpdateInstallFailed, LogField.Error(ex));
        }
        if (process is null)
        {
            UpdateFiles.Abandon(_dir);
            Set(State.LaunchFailed());
            return;
        }
        _setup = process;
        _log.Write(LogEvent.UpdateInstallStarted, LogField.Count("major", version.Major), LogField.Count("minor", version.Minor), LogField.Count("patch", version.Patch));
        Set(State.SetupLaunched(NowMs));
        Overlay?.Invoke(new UpdatingShown(version, NowMs));
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => _ui.TryEnqueue(SetupExited);
        // It may have gone before the handler was attached.
        if (process.HasExited) _ui.TryEnqueue(SetupExited);
        _deadline = _ui.CreateTimer();
        _deadline.Interval = TimeSpan.FromSeconds(1);
        _deadline.Tick += (_, _) =>
        {
            var next = State.Tick(NowMs);
            if (ReferenceEquals(next, State)) return;
            _deadline?.Stop();
            Set(next);
            _log.Write(LogEvent.UpdateInstallFailed, LogField.Tag("problem", UpdateProblem.TimedOut));
            var notice = new Notice(NoticeKind.UpdateSlow, Version: version);
            Overlay?.Invoke(new UpdatingEnded(notice, NowMs));
            NoticeShown?.Invoke(notice);
        };
        _deadline.Start();
    }

    /// <summary>
    /// Setup exited while yolime still runs: it never asked yolime to exit, so the update failed before the stop. Its
    /// result says why; the marker and result are cleared now so the next start doesn't report it again.
    /// </summary>
    private void SetupExited()
    {
        var next = State.SetupExited();
        if (ReferenceEquals(next, State) || _disposed) return;
        _deadline?.Stop();
        // After the deadline holds are back, so the notch may be showing a dictation: the failure goes to a balloon,
        // and only a "taking longer" card still up is taken down.
        var afterDeadline = State.Problem == UpdateProblem.TimedOut;
        var version = State.Release?.Version;
        var failure = Current is { } running ? UpdateFiles.ReadAndClear(_dir, running)?.Failure ?? UpdateFailure.NoResult : UpdateFailure.NoResult;
        Set(next);
        _log.Write(LogEvent.UpdateInstallFailed, LogField.Tag("failure", failure));
        var notice = new Notice(NoticeKind.UpdateFailed, Version: version, Current: Current, UpdateFailure: failure);
        if (afterDeadline)
        {
            Overlay?.Invoke(new UpdateSlowWithdrawn());
            Tell(notice);
            return;
        }
        Overlay?.Invoke(new UpdatingEnded(notice, NowMs));
        NoticeShown?.Invoke(notice);
    }

    /// <summary>Opens setup's log (or, without one, the updates folder).</summary>
    public void OpenLog()
    {
        var log = Path.Combine(_dir, UpdateFiles.LogName);
        try
        {
            Process.Start(new ProcessStartInfo(File.Exists(log) ? log : Directory.Exists(_dir) ? _dir : Path.GetDirectoryName(_dir)!) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            _log.Write(LogEvent.UnhandledException, LogField.Error(ex));
        }
    }

    public void OpenReleasePage(AppVersion v)
    {
        try
        {
            Process.Start(new ProcessStartInfo(ReleasePage(v)) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _log.Write(LogEvent.UnhandledException, LogField.Error(ex));
        }
    }

    /// <summary>Old setups are deleted once setup has had time to exit, with a few retries if one is still in use.</summary>
    private void Cleanup()
    {
        _cleanup = new Timer(_ =>
        {
            try
            {
                // Never during an install: the file being installed is one of them.
                if (_disposed || State.Phase is UpdatePhase.Downloading or UpdatePhase.Updating) return;
                if (UpdateFiles.DeleteSetups(_dir) || ++_cleanupTries >= 5) _cleanup?.Change(Timeout.Infinite, Timeout.Infinite);
            }
            catch (Exception ex)
            {
                _log.Write(LogEvent.UnhandledException, LogField.Error(ex));
            }
        }, null, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2));
    }

    private void Set(UpdateState next)
    {
        if (ReferenceEquals(next, State)) return;
        var before = State;
        State = next;
        StateChanged?.Invoke(next);
        if (UpdateGate.UnseenFailure(before, next) is { } failed && !WindowOpen()) Tell(failed);
        // A check that fell due mid-install was refused, and the timer is one-shot.
        if (before.InstallBusy && !next.InstallBusy) Reschedule();
    }

    public void Dispose()
    {
        _disposed = true;
        _schedule?.Dispose();
        _cleanup?.Dispose();
        _deadline?.Stop();
        // Never killed: setup is what asked yolime to exit, and it carries on with the install.
        _setup?.Dispose();
    }
}
