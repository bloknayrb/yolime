namespace Yolime.Core;

/// <summary>
/// Where yolime keeps its files. Roaming holds the small user-authored ones (settings, prompt overrides);
/// local holds the large or machine-bound ones (history, pending audio, logs).
/// </summary>
public sealed record AppPaths(string RoamingDir, string LocalDir)
{
    public string SettingsFile => Path.Combine(RoamingDir, "settings.json");
    public string PromptsDir => Path.Combine(RoamingDir, "prompts");
    public string HistoryDb => Path.Combine(LocalDir, "history.db");
    public string PendingDir => Path.Combine(LocalDir, "pending");
    public string LogsDir => Path.Combine(LocalDir, "logs");

    /// <summary>Downloaded setups, the expected-version marker, and setup's result file and log.</summary>
    public string UpdatesDir => Path.Combine(LocalDir, "updates");

    public static AppPaths Default() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Yolime"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yolime"));
}
