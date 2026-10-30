namespace Yolime.Core.Settings;

public static class FirstRun
{
    /// <summary>
    /// The wizard opens until it has been finished or skipped once. A missing key after that is shown by the tray
    /// ("setup needed", with "Finish setup…"), never by reopening the wizard on every launch.
    /// </summary>
    public static bool ShowWizard(YolimeSettings settings) => !settings.FirstRunDone;
}

public enum TrayIconState { Idle, Recording, Paused, SetupNeeded, Elevated }

/// <summary>The tray icon and tooltip, derived from the app's state in one place so they never disagree.</summary>
public sealed record TrayState(TrayIconState Icon, string Tooltip)
{
    /// <param name="micOpen">The microphone is being kept ready: said in the tooltip, since Windows shows it as in use.</param>
    public static TrayState From(bool recording, bool paused, bool sttReady, bool micDenied, bool elevated, bool micOpen = false)
    {
        var state = FromCore(recording, paused, sttReady, micDenied, elevated);
        return micOpen && !recording ? state with { Tooltip = state.Tooltip + " \u00b7 microphone open" } : state;
    }

    /// <summary>
    /// The full tooltip: the state, then " · update ready", then any hotkey problem, cut to the 127 characters the
    /// notification area allows (the state's own text is what gets cut, so both suffixes survive).
    /// </summary>
    public static string Compose(string state, bool updateReady, string? hotkeyProblem)
    {
        var suffix = (updateReady ? " · update ready" : "") + (hotkeyProblem is null ? "" : " · " + hotkeyProblem);
        if (state.Length + suffix.Length <= 127) return state + suffix;
        if (suffix.Length >= 127) return suffix[..127];
        return state[..(127 - suffix.Length)] + suffix;
    }

    private static TrayState FromCore(bool recording, bool paused, bool sttReady, bool micDenied, bool elevated)
    {
        if (recording) return new(TrayIconState.Recording, "yolime: recording");
        if (paused) return new(TrayIconState.Paused, "yolime: paused (hotkeys off)");
        if (!sttReady) return new(TrayIconState.SetupNeeded, "yolime: setup needed (no transcription key). Right-click for Finish setup.");
        if (micDenied) return new(TrayIconState.SetupNeeded, "yolime: setup needed (microphone access is off)");
        if (elevated) return new(TrayIconState.Elevated, "yolime: running as admin (not recommended)");
        return new(TrayIconState.Idle, "yolime: ready");
    }
}
