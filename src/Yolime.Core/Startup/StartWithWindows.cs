namespace Yolime.Core.Startup;

public enum StartupStatus { Off, On, DisabledInTaskManager, OtherCopy }

/// <summary>
/// What "Start with Windows" shows, from the <c>Run</c> value and Task Manager's <c>StartupApproved\Run</c> entry.
/// Task Manager's switch doesn't touch <c>Run</c>: it writes 12 bytes whose first byte is even when enabled (0x02)
/// and odd when disabled (0x03), so reading <c>Run</c> alone would show "on" for a startup the user turned off.
/// </summary>
public static class StartWithWindows
{
    public const string ValueName = "yolime";

    /// <summary>The <c>Run</c> value for this exe: quoted, so a path with spaces isn't split.</summary>
    public static string CommandFor(string exePath) => "\"" + exePath + "\"";

    public static StartupStatus From(string? runValue, byte[]? approved, string exePath)
    {
        if (string.IsNullOrWhiteSpace(runValue)) return StartupStatus.Off;
        if (approved is { Length: > 0 } && (approved[0] & 1) == 1) return StartupStatus.DisabledInTaskManager;
        var path = runValue.Trim();
        if (path.Length >= 2 && path[0] == '"')
        {
            var end = path.IndexOf('"', 1);
            path = end > 0 ? path[1..end] : path.Trim('"');
        }
        return string.Equals(Path.GetFullPath(path), Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase)
            ? StartupStatus.On
            : StartupStatus.OtherCopy;
    }

    /// <summary>The <c>StartupApproved</c> bytes for "enabled": 0x02 then eleven zeros (no disable time).</summary>
    public static byte[] Enabled() => [0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
}
