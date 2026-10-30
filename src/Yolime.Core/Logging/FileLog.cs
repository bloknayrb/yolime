using System.Text;

namespace Yolime.Core.Logging;

/// <summary>Daily log files under <c>logs\</c>, kept for 7 days.</summary>
public sealed class FileLog : ILog
{
    private const int KeepDays = 7;
    private readonly string _dir;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    public FileLog(AppPaths paths, TimeProvider time)
    {
        _dir = paths.LogsDir;
        _time = time;
        DatedFiles.DeleteOlderThan(_dir, "yolime", ".log", time.GetUtcNow(), KeepDays);
    }

    public void Write(LogEvent evt, params ReadOnlySpan<LogField> fields)
    {
        var now = _time.GetUtcNow();
        var line = new StringBuilder().Append(now.ToString("O")).Append(' ').Append(evt);
        foreach (var field in fields) line.Append(' ').Append(field.Format());
        line.Append('\n');
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                File.AppendAllText(DatedFiles.PathFor(_dir, "yolime", ".log", now), line.ToString());
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
