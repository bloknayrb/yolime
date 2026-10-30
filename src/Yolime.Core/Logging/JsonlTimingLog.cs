using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yolime.Core.Logging;

public interface ITimingLog
{
    void Append(RunTiming timing);
}

/// <summary>
/// <c>logs\timings-yyyyMMdd.jsonl</c>, kept 30 days rather than the main log's 7, because PR7 baselines against it.
/// </summary>
public sealed class JsonlTimingLog : ITimingLog
{
    private const int KeepDays = 30;
    private readonly string _dir;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    public JsonlTimingLog(AppPaths paths, TimeProvider time)
    {
        _dir = paths.LogsDir;
        _time = time;
        DatedFiles.DeleteOlderThan(_dir, "timings", ".jsonl", time.GetUtcNow(), KeepDays);
    }

    public void Append(RunTiming timing)
    {
        var line = JsonSerializer.Serialize(timing, TimingJsonContext.Default.RunTiming) + "\n";
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                File.AppendAllText(DatedFiles.PathFor(_dir, "timings", ".jsonl", _time.GetUtcNow()), line);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static RunTiming Parse(string line) =>
        JsonSerializer.Deserialize(line, TimingJsonContext.Default.RunTiming) ?? throw new JsonException("empty timing line");
}

[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(RunTiming))]
internal sealed partial class TimingJsonContext : JsonSerializerContext;
