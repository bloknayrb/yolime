using System.Text.Json;
using System.Text.Json.Nodes;

namespace Yolime.Core.Settings;

/// <summary>
/// The one live copy of the settings. Every change goes through <see cref="Update"/>, which saves before it applies,
/// so memory never runs ahead of the file. Hand edits to <c>settings.json</c> are picked up by a debounced watcher.
/// </summary>
/// <remarks>
/// Readers take <see cref="Current"/> once per run and keep that snapshot, so a change mid-run never mixes two
/// settings in one dictation. <see cref="Changed"/> fires outside the lock, on whichever thread made the change.
/// </remarks>
public sealed class SettingsHolder : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    private readonly object _gate = new();
    private readonly SettingsStore _store;
    private YolimeSettings _current;
    private FileSystemWatcher? _watcher;
    private Timer? _timer;
    private bool _disposed;

    private readonly Logging.ILog? _log;

    public SettingsHolder(SettingsStore store, YolimeSettings initial, Logging.ILog? log = null)
    {
        _store = store;
        _current = initial;
        _log = log;
    }

    /// <summary>(old, new). Never raised for a save or reload that changed nothing.</summary>
    public event Action<YolimeSettings, YolimeSettings>? Changed;

    public YolimeSettings Current => Volatile.Read(ref _current);

    /// <summary>Applies <paramref name="transform"/> only if the result saved; a failed or read-only save changes nothing.</summary>
    public SaveResult Update(Func<YolimeSettings, YolimeSettings> transform)
    {
        YolimeSettings old, next;
        lock (_gate)
        {
            old = _current;
            next = transform(old);
            if (SameAs(old, next)) return SaveResult.Saved;
            var result = _store.Save(next);
            if (result != SaveResult.Saved) return result;
            Volatile.Write(ref _current, next);
        }
        Changed?.Invoke(old, next);
        return SaveResult.Saved;
    }

    /// <summary>
    /// Re-reads the file after a hand edit. A file that doesn't parse (an editor mid-save, a typo) or is missing (an
    /// editor's delete-and-rename) is ignored and left alone: the store would otherwise move it aside as corrupt.
    /// </summary>
    public void ReloadFromDisk()
    {
        YolimeSettings old, next;
        lock (_gate)
        {
            if (_disposed || !ParsesAsObject()) return;
            var loaded = _store.Load();
            if (loaded.Status is not (SettingsLoadStatus.Ok or SettingsLoadStatus.NewerReadOnly)) return;
            old = _current;
            next = loaded.Settings;
            if (SameAs(old, next)) return;
            Volatile.Write(ref _current, next);
        }
        Changed?.Invoke(old, next);
    }

    public void StartWatching()
    {
        lock (_gate)
        {
            if (_disposed || _watcher is not null) return;
            var file = _store.SettingsFile;
            var dir = Path.GetDirectoryName(file)!;
            Directory.CreateDirectory(dir);
            _timer = new Timer(_ => SafeReload(), null, Timeout.Infinite, Timeout.Infinite);
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(file))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            // File.Replace (our own saves and most editors) shows up as a rename onto the name.
            _watcher.Changed += (_, _) => Poke();
            _watcher.Created += (_, _) => Poke();
            _watcher.Renamed += (_, _) => Poke();
            _watcher.EnableRaisingEvents = true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _watcher?.Dispose();
            _watcher = null;
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>On a watcher thread; may race Dispose, which is why it checks under the lock.</summary>
    private void Poke()
    {
        lock (_gate)
        {
            if (!_disposed) _timer?.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void SafeReload()
    {
        // A timer callback that throws takes the process down, and a Changed handler is someone else's code: nothing a
        // reload does is worth that. The next hand edit tries again.
        try { ReloadFromDisk(); }
        catch (Exception ex)
        {
            _log?.Write(Logging.LogEvent.UnhandledException, Logging.LogField.Error(ex));
        }
    }

    private bool ParsesAsObject()
    {
        try
        {
            var text = File.ReadAllText(_store.SettingsFile);
            var node = JsonNode.Parse(text, documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return node is JsonObject obj && obj.Count >= 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    // Records compare lists by reference, so compare what would be written instead.
    private static bool SameAs(YolimeSettings a, YolimeSettings b) =>
        ReferenceEquals(a, b)
        || JsonSerializer.Serialize(a, SettingsJsonContext.Default.YolimeSettings) == JsonSerializer.Serialize(b, SettingsJsonContext.Default.YolimeSettings);
}
