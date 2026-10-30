using System.Text.Json;
using System.Text.Json.Nodes;
using Yolime.Core.Logging;

namespace Yolime.Core.Settings;

/// <summary>
/// Loads and saves <c>settings.json</c>. It never crashes on a bad file and never silently drops the user's other
/// settings because of one bad field. A file from a newer version is read but never overwritten.
/// </summary>
public sealed class SettingsStore
{
    public const int CurrentSchemaVersion = 1;
    private const int KeepBackups = 5;

    private readonly AppPaths _paths;
    private readonly ILog _log;
    private readonly TimeProvider _time;
    private readonly Action<string>? _beforeReplace;
    private readonly int _currentVersion;
    private readonly IReadOnlyDictionary<int, Action<JsonObject>> _steps;
    private bool _readOnly;

    public SettingsStore(AppPaths paths, ILog log)
        : this(paths, log, TimeProvider.System, null)
    {
    }

    /// <param name="beforeReplace">Test seam: runs with the temp file's path just before it replaces the real one.</param>
    internal SettingsStore(
        AppPaths paths,
        ILog log,
        TimeProvider time,
        Action<string>? beforeReplace,
        int currentVersion = CurrentSchemaVersion,
        IReadOnlyDictionary<int, Action<JsonObject>>? steps = null)
    {
        _paths = paths;
        _log = log;
        _time = time;
        _beforeReplace = beforeReplace;
        _currentVersion = currentVersion;
        _steps = steps ?? SettingsMigrations.Steps;
    }

    public string SettingsFile => _paths.SettingsFile;

    public SettingsLoadResult Load()
    {
        _readOnly = false;
        string text;
        try
        {
            text = File.ReadAllText(_paths.SettingsFile);
        }
        catch (FileNotFoundException) { return new(new YolimeSettings(), SettingsLoadStatus.Missing, null, []); }
        catch (DirectoryNotFoundException) { return new(new YolimeSettings(), SettingsLoadStatus.Missing, null, []); }

        JsonObject doc;
        try
        {
            doc = JsonNode.Parse(text, documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                ?.AsObject() ?? throw new JsonException();
            // JsonObject builds its dictionary lazily; touch it here so a duplicated key fails inside this try.
            _ = doc.Count;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            var backup = BackUpCorruptFile();
            _log.Write(LogEvent.SettingsCorruptBackedUp);
            return new(new YolimeSettings(), SettingsLoadStatus.CorruptBackedUp, backup, []);
        }

        var version = doc["schemaVersion"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : _currentVersion;
        var status = SettingsLoadStatus.Ok;
        if (version > _currentVersion)
        {
            _readOnly = true;
            status = SettingsLoadStatus.NewerReadOnly;
        }
        else if (version < _currentVersion)
        {
            SettingsMigrations.Migrate(doc, version, _currentVersion, _steps);
            _log.Write(LogEvent.SettingsMigrated, LogField.Count("from", version), LogField.Count("to", _currentVersion));
        }

        var (settings, invalid) = DeserializeFieldByField(doc);
        return new(settings, status, null, invalid);
    }

    public SaveResult Save(YolimeSettings settings)
    {
        if (_readOnly) return SaveResult.ReadOnly;
        var target = _paths.SettingsFile;
        var tmp = target + ".tmp";
        try
        {
            Directory.CreateDirectory(_paths.RoamingDir);
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.YolimeSettings) + "\n");
            _beforeReplace?.Invoke(tmp);
            // File.Replace needs an existing destination, so the very first save moves instead.
            if (File.Exists(target)) File.Replace(tmp, target, null);
            else File.Move(tmp, target);
            return SaveResult.Saved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Write(LogEvent.SettingsSaveFailed, LogField.Error(ex));
            try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return SaveResult.Failed;
        }
    }

    /// <summary>Drops each field that fails to deserialize (or is out of range) so the rest of the file survives.</summary>
    private static (YolimeSettings, IReadOnlyList<string>) DeserializeFieldByField(JsonObject doc)
    {
        var invalid = new List<string>();
        while (true)
        {
            try
            {
                var settings = doc.Deserialize(SettingsJsonContext.Default.YolimeSettings) ?? new YolimeSettings();
                var outOfRange = settings.OutOfRangeFields().ToList();
                invalid.AddRange(outOfRange);
                return (settings.WithDefaultsFor(outOfRange), invalid);
            }
            catch (JsonException ex) when (TopLevelName(ex.Path) is { } name && doc.ContainsKey(name))
            {
                doc.Remove(name);
                invalid.Add(name);
            }
        }
    }

    private static string? TopLevelName(string? path)
    {
        // "$.sttProvider" or "$.vocabulary[0]" → the top-level property.
        if (path is null || !path.StartsWith("$.", StringComparison.Ordinal)) return null;
        var rest = path[2..];
        var end = rest.IndexOfAny(['.', '[']);
        return end < 0 ? rest : rest[..end];
    }

    private string? BackUpCorruptFile()
    {
        var stamp = _time.GetUtcNow().UtcDateTime.ToString("yyyyMMddHHmmss");
        var baseName = $"{_paths.SettingsFile}.bad-{stamp}";
        var backup = baseName;
        for (var i = 1; File.Exists(backup); i++) backup = $"{baseName}-{i}";
        try
        {
            File.Move(_paths.SettingsFile, backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var all = Directory.GetFiles(_paths.RoamingDir, "settings.json.bad-*").Order(StringComparer.Ordinal).ToList();
        foreach (var old in all.Take(Math.Max(0, all.Count - KeepBackups)))
        {
            try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return backup;
    }
}
