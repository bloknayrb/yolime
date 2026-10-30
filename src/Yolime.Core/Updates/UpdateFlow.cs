namespace Yolime.Core.Updates;

/// <summary>Where an in-app update is, from "nothing known" to "setup is running".</summary>
public enum UpdatePhase
{
    Idle,
    Checking,
    /// <summary>A newer release is known (and, unless this copy is elevated, can be installed).</summary>
    Available,
    /// <summary>Install was asked for while a dictation was still recording or working: it finishes first.</summary>
    Finishing,
    Downloading,
    /// <summary>Setup is running; yolime stays up, refusing holds, until setup asks it to exit.</summary>
    Updating,
    /// <summary>The last attempt didn't install (<see cref="UpdateState.Problem"/> says why).</summary>
    Failed,
}

/// <summary>Why the last install attempt didn't happen, as the update window or the card says it.</summary>
public enum UpdateProblem { DownloadFailed, ChecksumMismatch, LaunchFailed, InstallFailed, TimedOut }

/// <summary>
/// The in-app update as a reducer: events in, the next state out, with no timers, threads or I/O, so every rule
/// (one install at a time, holds refused from Install until setup is gone, the five-minute deadline) is testable.
/// Time arrives as milliseconds on a monotonic clock. An event that doesn't apply returns the same instance.
/// </summary>
/// <param name="Elevated">This copy runs above medium integrity: it may learn of a release but never installs one.</param>
public sealed record UpdateState(
    UpdatePhase Phase,
    ReleaseInfo? Release = null,
    string? SetupHash = null,
    double Progress = 0,
    UpdateProblem? Problem = null,
    long UpdatingSinceMs = 0,
    bool Elevated = false)
{
    /// <summary>How long setup may run without asking yolime to exit before holds come back.</summary>
    public static readonly TimeSpan SetupDeadline = TimeSpan.FromMinutes(5);

    public static UpdateState Initial(bool elevated) => new(UpdatePhase.Idle, Elevated: elevated);

    /// <summary>
    /// Whether Install can start now: a known release, not elevated, and nothing in progress. Not after a timed-out
    /// setup, which may still be running (it isn't killed, and its mutex would refuse a second one).
    /// </summary>
    public bool CanInstall => !Elevated && Release is not null
        && (Phase == UpdatePhase.Available || (Phase == UpdatePhase.Failed && Problem != UpdateProblem.TimedOut));

    /// <summary>The version the tray and Settings offer to install, or null.</summary>
    public AppVersion? Offered => CanInstall ? Release!.Version : null;

    /// <summary>From Install until setup has gone (or failed): a new hold would be cut off by the exit.</summary>
    public bool RefuseHolds => Phase is UpdatePhase.Finishing or UpdatePhase.Downloading or UpdatePhase.Updating;

    /// <summary>
    /// An install is under way (or a timed-out setup may still be running): checks wait. Leaving it is when the
    /// schedule must be armed again, since a check that fell due meanwhile was refused.
    /// </summary>
    public bool InstallBusy => Phase is UpdatePhase.Finishing or UpdatePhase.Downloading or UpdatePhase.Updating
        || (Phase == UpdatePhase.Failed && Problem == UpdateProblem.TimedOut);

    /// <summary>Only between installs: a check mid-install would replace the release being installed.</summary>
    public UpdateState CheckStarted() =>
        Phase != UpdatePhase.Checking && !InstallBusy ? this with { Phase = UpdatePhase.Checking } : this;

    public UpdateState CheckFinished(CheckResult result)
    {
        if (Phase != UpdatePhase.Checking) return this;
        return result.Outcome switch
        {
            CheckOutcome.Available when result.Release is not null && result.SetupHash is not null =>
                this with { Phase = UpdatePhase.Available, Release = result.Release, SetupHash = result.SetupHash, Problem = null, Progress = 0 },
            CheckOutcome.UpToDate => this with { Phase = UpdatePhase.Idle, Release = null, SetupHash = null, Problem = null },
            // Try later: whatever was known before still stands.
            _ => this with { Phase = Release is null ? UpdatePhase.Idle : UpdatePhase.Available },
        };
    }

    /// <summary>Install now (or Try again). A second click while one is in progress does nothing.</summary>
    public UpdateState InstallRequested(bool dictationInProgress) => !CanInstall ? this
        : this with { Phase = dictationInProgress ? UpdatePhase.Finishing : UpdatePhase.Downloading, Progress = 0, Problem = null };

    public UpdateState DictationFinished() => Phase == UpdatePhase.Finishing ? this with { Phase = UpdatePhase.Downloading } : this;

    public UpdateState DownloadProgress(double fraction) =>
        // Never backwards: a progress report can arrive after the download finished.
        Phase == UpdatePhase.Downloading ? this with { Progress = Math.Max(Progress, Math.Clamp(fraction, 0, 1)) } : this;

    public UpdateState DownloadFinished(DownloadOutcome outcome)
    {
        if (Phase != UpdatePhase.Downloading) return this;
        return outcome switch
        {
            DownloadOutcome.Ready => this with { Progress = 1 },
            // A setup whose version isn't the release's is as untrustworthy as one with the wrong hash.
            DownloadOutcome.ChecksumMismatch or DownloadOutcome.WrongVersion => Failed(UpdateProblem.ChecksumMismatch),
            _ => Failed(UpdateProblem.DownloadFailed),
        };
    }

    public UpdateState SetupLaunched(long nowMs) =>
        Phase == UpdatePhase.Downloading ? this with { Phase = UpdatePhase.Updating, UpdatingSinceMs = nowMs } : this;

    /// <summary>The setup couldn't be prepared or started: nothing changed.</summary>
    public UpdateState LaunchFailed() => Phase == UpdatePhase.Downloading ? Failed(UpdateProblem.LaunchFailed) : this;

    /// <summary>
    /// Setup exited while yolime is still running: it never asked yolime to exit, so the update failed before the stop.
    /// </summary>
    public UpdateState SetupExited() =>
        Phase == UpdatePhase.Updating || (Phase == UpdatePhase.Failed && Problem == UpdateProblem.TimedOut)
            ? Failed(UpdateProblem.InstallFailed)
            : this;

    public UpdateState Tick(long nowMs) =>
        Phase == UpdatePhase.Updating && nowMs - UpdatingSinceMs >= (long)SetupDeadline.TotalMilliseconds
            ? Failed(UpdateProblem.TimedOut)
            : this;

    private UpdateState Failed(UpdateProblem problem) => this with { Phase = UpdatePhase.Failed, Problem = problem };
}

/// <summary>The update's words, for the window and Settings, built from the version and fixed text only.</summary>
public static class UpdateTexts
{
    public static string WindowTitle(AppVersion v) => $"Install yolime {v}?";

    public static string InstallButton(UpdateState s) => s.Phase == UpdatePhase.Failed ? "Try again" : "Install now";

    /// <summary>Once Install is under way, closing the window only hides it: the install carries on.</summary>
    public static string DismissButton(UpdateState s) => s.Phase is UpdatePhase.Finishing or UpdatePhase.Downloading ? "Hide" : "Not now";

    public static string WindowStatus(UpdateState s)
    {
        var v = s.Release?.Version.ToString() ?? "the update";
        return s.Phase switch
        {
            UpdatePhase.Checking => "Checking for a newer version…",
            UpdatePhase.Finishing => "Finishing your dictation first…",
            UpdatePhase.Downloading when s.Progress >= 1 => "Checking the download…",
            UpdatePhase.Downloading => $"Downloading {v}… {(int)(s.Progress * 100)}%",
            UpdatePhase.Updating => $"Updating to {v}…",
            UpdatePhase.Failed => s.Problem switch
            {
                UpdateProblem.ChecksumMismatch => $"The {v} download didn't match its checksum, so it wasn't installed.",
                UpdateProblem.LaunchFailed => $"Couldn't start the {v} setup. Nothing changed.",
                UpdateProblem.InstallFailed => $"The update to {v} didn't install. Nothing changed.",
                UpdateProblem.TimedOut => $"The {v} setup is taking longer than it should.",
                _ => $"Couldn't download {v}. Nothing changed.",
            },
            _ => "Installing closes yolime briefly and reopens it.",
        };
    }

    /// <summary>The help line under Settings' update row: what the last check found.</summary>
    public static string SettingsLine(bool installed, UpdateState s, DateTimeOffset now, DateTimeOffset? lastChecked, DateTimeOffset? lastFailed)
    {
        if (!installed) return "Updates are off for this copy (not installed)";
        if (s.Phase == UpdatePhase.Checking) return "Checking…";
        if (s.Release is { } r)
            return s.Elevated ? $"{r.Version} is available, but this copy runs as administrator, so it can't install it" : $"{r.Version} is available";
        if (lastFailed is { } f && (lastChecked is null || f > lastChecked)) return "Couldn't reach GitHub";
        return lastChecked is { } c ? "Up to date · checked " + Ago(now - c) : "Not checked yet";
    }

    public static string ReleasePage(string owner, string repo, AppVersion v) =>
        $"https://github.com/{owner}/{repo}/releases/tag/v{v.Major}.{v.Minor}.{v.Patch}";

    private static string Ago(TimeSpan t)
    {
        if (t < TimeSpan.FromMinutes(1)) return "just now";
        if (t < TimeSpan.FromHours(1)) return $"{(int)t.TotalMinutes} min ago";
        if (t < TimeSpan.FromHours(48)) return $"{(int)t.TotalHours} h ago";
        return $"{(int)t.TotalDays} days ago";
    }
}

/// <summary>When updates apply to this copy, and when to say so.</summary>
public static class UpdateGate
{
    /// <summary>
    /// Only the copy installed where install.ps1 puts it: a dev build, the E2E app or a copy run from a download
    /// folder never updates itself (setup would replace the installed copy, not this one).
    /// </summary>
    public static bool IsInstalled(string exeDir, string? localAppData)
    {
        if (string.IsNullOrEmpty(localAppData) || string.IsNullOrEmpty(exeDir)) return false;
        try
        {
            var here = Path.GetFullPath(exeDir).TrimEnd('\\');
            var install = Path.GetFullPath(Path.Combine(localAppData, "Programs", "Yolime")).TrimEnd('\\');
            return string.Equals(here, install, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// The balloon for a release just found, and the settings that remember it. The offer and the admin notice are
    /// remembered apart: after the admin notice, the user starts yolime from the Start menu as it says, and that copy
    /// still offers the same version once.
    /// </summary>
    public static (Feedback.Notice? Notice, Settings.YolimeSettings Next) Announcement(AppVersion v, bool elevated, Settings.YolimeSettings s)
    {
        var last = elevated ? s.LastNotifiedElevatedUpdate : s.LastNotifiedUpdate;
        if (!ShouldNotify(v, last)) return (null, s);
        var next = elevated ? s with { LastNotifiedElevatedUpdate = v.ToString() } : s with { LastNotifiedUpdate = v.ToString() };
        return (new Feedback.Notice(elevated ? Feedback.NoticeKind.UpdateAvailableElevated : Feedback.NoticeKind.UpdateAvailable, Version: v), next);
    }

    /// <summary>
    /// The balloon for an install attempt that stopped at the download, its check or setup's start, for when the update
    /// window (which says it in place) is closed. Null for any other step; a failed or slow setup has its card.
    /// </summary>
    public static Feedback.Notice? UnseenFailure(UpdateState before, UpdateState after) =>
        before.Phase == UpdatePhase.Downloading && after.Phase == UpdatePhase.Failed
            && after.Problem is UpdateProblem.DownloadFailed or UpdateProblem.ChecksumMismatch or UpdateProblem.LaunchFailed
            ? new Feedback.Notice(Feedback.NoticeKind.UpdateAttemptFailed, Version: after.Release?.Version, Problem: after.Problem)
            : null;

    /// <summary>
    /// An admin copy announced a release it couldn't install, and this copy (started from the Start menu, as that
    /// notice said) hasn't offered it yet: its first check comes a minute after start, not a day after the admin one's.
    /// </summary>
    public static bool OwedOffer(Settings.YolimeSettings s, AppVersion? current, bool elevated) =>
        !elevated && current is { } running
        && AppVersion.FromProductVersion(s.LastNotifiedElevatedUpdate) is { } announced && announced > running
        && ShouldNotify(announced, s.LastNotifiedUpdate);

    /// <summary>A balloon once per version: not again for one already announced (or an older one).</summary>
    public static bool ShouldNotify(AppVersion available, string? lastNotified) =>
        AppVersion.FromProductVersion(lastNotified) is not { } last || available > last;
}
