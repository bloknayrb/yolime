using System.Collections.Concurrent;
using System.Diagnostics;

namespace Yolime.App.Native;

/// <summary>"Slack" rather than "slack": the product name from the process's file version info, cached.</summary>
internal static class DisplayNames
{
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string For(string? process)
    {
        if (string.IsNullOrEmpty(process)) return "";
        if (Cache.TryGetValue(process, out var known)) return known;
        foreach (var running in Process.GetProcessesByName(process))
        {
            using (running)
            {
                try
                {
                    // One elevated or other-bitness instance can't be opened; another instance may still answer.
                    var description = running.MainModule?.FileVersionInfo.FileDescription;
                    if (!string.IsNullOrWhiteSpace(description) && description.Length <= 40) return Cache[process] = description.Trim();
                }
                catch (Exception)
                {
                }
            }
        }
        // Not cached: a later lookup may find an instance it can read.
        return process;
    }
}
