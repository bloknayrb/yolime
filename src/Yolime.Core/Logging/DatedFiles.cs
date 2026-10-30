using System.Globalization;

namespace Yolime.Core.Logging;

/// <summary>Files named <c>prefix-yyyyMMdd.ext</c>; age comes from the name, not file times, so it is testable.</summary>
internal static class DatedFiles
{
    public static string PathFor(string dir, string prefix, string ext, DateTimeOffset now) =>
        Path.Combine(dir, $"{prefix}-{now.UtcDateTime:yyyyMMdd}{ext}");

    public static void DeleteOlderThan(string dir, string prefix, string ext, DateTimeOffset now, int days)
    {
        if (!Directory.Exists(dir)) return;
        var cutoff = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-days);
        foreach (var file in Directory.EnumerateFiles(dir, $"{prefix}-*{ext}"))
        {
            var stamp = Path.GetFileNameWithoutExtension(file)[(prefix.Length + 1)..];
            if (DateOnly.TryParseExact(stamp, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && day < cutoff)
            {
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
