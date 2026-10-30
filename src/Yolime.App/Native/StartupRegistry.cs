using Microsoft.Win32;
using Yolime.Core.Startup;

namespace Yolime.App.Native;

/// <summary>"Start with Windows": the per-user <c>Run</c> value, and Task Manager's switch for it.</summary>
internal static class StartupRegistry
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    private static string Exe => Environment.ProcessPath ?? "";

    public static StartupStatus Status()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return StartWithWindows.From(
                run?.GetValue(StartWithWindows.ValueName) as string,
                approved?.GetValue(StartWithWindows.ValueName) as byte[],
                Exe);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return StartupStatus.Off;
        }
    }

    /// <summary>Points the entry at this copy, and turns Task Manager's switch back on if it was off.</summary>
    public static bool Enable()
    {
        try
        {
            using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
                run.SetValue(StartWithWindows.ValueName, StartWithWindows.CommandFor(Exe), RegistryValueKind.String);
            using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true))
            {
                if (approved?.GetValue(StartWithWindows.ValueName) is byte[] { Length: > 0 } bytes && (bytes[0] & 1) == 1)
                    approved.SetValue(StartWithWindows.ValueName, StartWithWindows.Enabled(), RegistryValueKind.Binary);
            }
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    public static bool Disable()
    {
        try
        {
            using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
                run?.DeleteValue(StartWithWindows.ValueName, throwOnMissingValue: false);
            using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true))
                approved?.DeleteValue(StartWithWindows.ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }
}
