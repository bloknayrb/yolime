namespace Yolime.Core.Logging;

public interface ILog
{
    void Write(LogEvent evt, params ReadOnlySpan<LogField> fields);
}
