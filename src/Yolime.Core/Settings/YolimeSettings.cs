using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yolime.Core.Settings;

/// <summary>
/// User settings, stored as JSON in <see cref="AppPaths.SettingsFile"/>. API keys never live here.
/// Later PRs add their own fields; a field added with a default needs no migration.
/// </summary>
/// <remarks>
/// Properties are <c>set</c>, not <c>init</c>: the JSON source generator treats init-only members like constructor
/// parameters, so a field missing from the file would come back as 0 instead of its default, and extension data
/// can't bind at all. Treat instances as immutable and change them with <c>with</c>.
/// </remarks>
public sealed record YolimeSettings
{
    public int SchemaVersion { get; set; } = SettingsStore.CurrentSchemaVersion;

    public SttProviderKind SttProvider { get; set; } = SttProviderKind.OpenAi;
    public string OpenAiSttModel { get; set; } = "gpt-4o-transcribe";
    public string GroqSttModel { get; set; } = "whisper-large-v3-turbo";
    public string? CustomSttBaseUrl { get; set; }
    public string? CustomSttModel { get; set; }

    /// <summary>
    /// Send the recording while the key is held (OpenAI only), so key-up waits only for the server. Off by default: audio
    /// spoken in a hold that's then discarded has already reached OpenAI, though it's never completed or transcribed.
    /// </summary>
    public bool StreamingUpload { get; set; }
    public IReadOnlyList<string> Vocabulary { get; set; } = [];

    /// <summary>1–3650.</summary>
    public int HistoryRetentionDays { get; set; } = 30;

    /// <summary>The hold key's virtual-key code. Right Ctrl by default.</summary>
    public int HoldKeyVk { get; set; } = 0xA3;

    /// <summary>Double-tap the hold key for hands-free recording; a tap ends it. Off by default.</summary>
    public bool DoubleTapLatch { get; set; }

    public Mode StartingMode { get; set; } = Mode.Dictate;

    /// <summary>Start, stop and error sounds. They're the only feedback that Do Not Disturb doesn't silence.</summary>
    public bool Sounds { get; set; } = true;

    /// <summary>Per-chunk RMS that counts as voiced audio, 0.0001–0.5.</summary>
    public double SilenceRms { get; set; } = 0.0025;

    /// <summary>
    /// The Claude model for Prompt mode: one of <see cref="Modes.ModelIds.Known"/>. Sonnet by evidence: on the fixture
    /// eval (2026-10-28) Sonnet passed 15/15 and Haiku 11/15, Haiku's misses being invented requirements.
    /// </summary>
    public string PromptModel { get; set; } = Modes.ModelIds.Sonnet;

    /// <summary>
    /// Process name → starting mode name ("Prompt"). Values are strings so one bad entry is skipped instead of
    /// making the whole file unreadable.
    /// </summary>
    public Dictionary<string, string> AppStartingModes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Beeps that say which mode a Shift tap switched to (1 = Dictate … 4 = Raw). Needs <see cref="Sounds"/>.</summary>
    public bool ModeCycleCues { get; set; } = true;

    /// <summary>Where the notch sits on the monitor the user is working on.</summary>
    public OverlayPosition OverlayPosition { get; set; } = OverlayPosition.TopCentre;

    /// <summary>
    /// Keep the notch out of screen captures and screen sharing (it shows transcripts and reply context). On by
    /// default: it costs nothing on the user's own screen.
    /// </summary>
    public bool HideOverlayFromCapture { get; set; } = true;

    /// <summary>The capture device's id; null means the Windows default (which follows device changes).</summary>
    public string? MicDeviceId { get; set; }

    /// <summary>Keep the microphone open for a minute after each dictation, so the next hold starts at once. Off by default.</summary>
    public bool KeepMicReady { get; set; }

    /// <summary>While on, runs keep no history row and no recording: nothing to retry or paste later.</summary>
    public bool HistoryPaused { get; set; }

    /// <summary>The first-run wizard was finished or skipped.</summary>
    public bool FirstRunDone { get; set; }

    /// <summary>
    /// The chord that pastes the last result (spec: always available). Not Ctrl+Shift+V (paste as plain text in most
    /// apps) and not Ctrl+Alt+V (Paste Special in Office; Bryan chose Win+Alt+V, 2026-10-28).
    /// </summary>
    public string PasteLastHotkey { get; set; } = "Win+Alt+V";

    /// <summary>Mode names ("Prompt") that always show the preview card before inserting. Unknown names are ignored.</summary>
    public List<string> PreviewModes { get; set; } = [];

    /// <summary>Look for a newer release on GitHub once a day (installed copies only). On by default; Check now works either way.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>When the last check got an answer (up to date or a release), for the schedule and Settings.</summary>
    public DateTimeOffset? LastUpdateCheck { get; set; }

    /// <summary>When the last check failed (no network, rate limited): the next one is an hour later.</summary>
    public DateTimeOffset? LastUpdateFailure { get; set; }

    /// <summary>The last version a balloon announced ("0.2.0"), so each release is announced once.</summary>
    public string? LastNotifiedUpdate { get; set; }

    /// <summary>The same for the admin notice ("can't install it from here"), kept apart so it doesn't use up the offer.</summary>
    public string? LastNotifiedElevatedUpdate { get; set; }

    public bool PreviewsAlways(Mode mode) =>
        PreviewModes.Any(m => string.Equals(m, mode.ToString(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Fields this version doesn't know, kept so a newer file survives a save by this one.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>
    /// The JSON names of fields whose value is outside its allowed range, or null or blank where that can't work.
    /// JSON happily deserializes null into a non-nullable member, so this is the check that keeps a hand-edited
    /// <c>null</c> from crashing every later run.
    /// </summary>
    internal IEnumerable<string> OutOfRangeFields()
    {
        if (!Enum.IsDefined(SttProvider)) yield return "sttProvider";
        if (string.IsNullOrWhiteSpace(OpenAiSttModel)) yield return "openAiSttModel";
        if (string.IsNullOrWhiteSpace(GroqSttModel)) yield return "groqSttModel";
        if (Vocabulary is null || Vocabulary.Any(v => v is null)) yield return "vocabulary";
        if (!Hotkeys.HookReducer.IsValidHoldKey(HoldKeyVk)) yield return "holdKeyVk";
        if (!Enum.IsDefined(StartingMode)) yield return "startingMode";
        if (HistoryRetentionDays is < 1 or > 3650) yield return "historyRetentionDays";
        if (SilenceRms is < 0.0001 or > 0.5 or double.NaN) yield return "silenceRms";
        if (PromptModel is null || !Modes.ModelIds.Known.Contains(PromptModel)) yield return "promptModel";
        if (AppStartingModes is null) yield return "appStartingModes";
        if (!Enum.IsDefined(OverlayPosition)) yield return "overlayPosition";
        if (PreviewModes is null) yield return "previewModes";
        if (!Hotkeys.HotkeyChord.TryParse(PasteLastHotkey, out _)) yield return "pasteLastHotkey";
    }

    internal YolimeSettings WithDefaultsFor(IEnumerable<string> fields)
    {
        var defaults = new YolimeSettings();
        var result = this;
        foreach (var field in fields)
        {
            result = field switch
            {
                "sttProvider" => result with { SttProvider = defaults.SttProvider },
                "openAiSttModel" => result with { OpenAiSttModel = defaults.OpenAiSttModel },
                "groqSttModel" => result with { GroqSttModel = defaults.GroqSttModel },
                "vocabulary" => result with { Vocabulary = defaults.Vocabulary },
                "holdKeyVk" => result with { HoldKeyVk = defaults.HoldKeyVk },
                "startingMode" => result with { StartingMode = defaults.StartingMode },
                "historyRetentionDays" => result with { HistoryRetentionDays = defaults.HistoryRetentionDays },
                "silenceRms" => result with { SilenceRms = defaults.SilenceRms },
                "promptModel" => result with { PromptModel = defaults.PromptModel },
                "appStartingModes" => result with { AppStartingModes = defaults.AppStartingModes },
                "overlayPosition" => result with { OverlayPosition = defaults.OverlayPosition },
                "previewModes" => result with { PreviewModes = defaults.PreviewModes },
                "pasteLastHotkey" => result with { PasteLastHotkey = defaults.PasteLastHotkey },
                _ => result,
            };
        }
        return result;
    }
}

public enum OverlayPosition { TopCentre, BottomCentre }
