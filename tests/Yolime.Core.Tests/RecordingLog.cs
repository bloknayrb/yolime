using Yolime.Core.Logging;

namespace Yolime.Core.Tests;

/// <summary>An in-memory <see cref="ILog"/> for asserting which events a component raised.</summary>
public sealed class RecordingLog : ILog
{
    private readonly List<LogEvent> _events = [];

    public IReadOnlyList<LogEvent> Events
    {
        get { lock (_events) return _events.ToArray(); }
    }

    private readonly List<(LogEvent Event, string[] Fields)> _lines = [];

    /// <summary>Each event with its fields as the log file would format them ("key=value").</summary>
    public IReadOnlyList<(LogEvent Event, string[] Fields)> Lines
    {
        get { lock (_events) return _lines.ToArray(); }
    }

    public void Write(LogEvent evt, params ReadOnlySpan<LogField> fields)
    {
        var formatted = new string[fields.Length];
        for (var i = 0; i < fields.Length; i++) formatted[i] = fields[i].Format();
        lock (_events)
        {
            _events.Add(evt);
            _lines.Add((evt, formatted));
        }
    }
}
