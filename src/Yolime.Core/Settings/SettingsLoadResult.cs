namespace Yolime.Core.Settings;

public enum SettingsLoadStatus { Ok, Missing, CorruptBackedUp, NewerReadOnly }

public enum SaveResult { Saved, ReadOnly, Failed }

/// <param name="InvalidFields">JSON names of fields reset to their default. Never their values.</param>
public sealed record SettingsLoadResult(
    YolimeSettings Settings,
    SettingsLoadStatus Status,
    string? BackupPath,
    IReadOnlyList<string> InvalidFields);
