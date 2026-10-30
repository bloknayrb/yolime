using Yolime.Core.Logging;

namespace Yolime.Core.Audio;

/// <summary>
/// Recordings in <c>pending\</c>, kept until their transcript is safe. This is what "no lost work" rests on: a crash,
/// a failed upload or an unheard clip leaves the audio here for Retry.
/// </summary>
public sealed class PendingAudioStore(AppPaths paths, ILog log, TimeProvider time)
{
    /// <summary>The suffix of a recording made while history was paused: never adopted after a crash, only deleted.</summary>
    public const string PausedSuffix = ".paused.wav";

    public PendingRecording Begin(Guid runId, bool historyPaused = false)
    {
        Directory.CreateDirectory(paths.PendingDir);
        var name = historyPaused ? $"{runId:N}{PausedSuffix}" : $"{runId:N}.wav";
        return new PendingRecording(name, PathOf(name));
    }

    /// <summary>The full path of a pending file. Only bare file names are accepted.</summary>
    public string PathOf(string fileName)
    {
        if (fileName.Length == 0 || Path.GetFileName(fileName) != fileName || fileName.Contains(".."))
            throw new ArgumentException("not a pending file name", nameof(fileName));
        return Path.Combine(paths.PendingDir, fileName);
    }

    public IReadOnlyList<string> List() =>
        Directory.Exists(paths.PendingDir)
            ? Directory.GetFiles(paths.PendingDir, "*.wav").Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal).ToList()
            : [];

    public IReadOnlyList<string> PruneOlderThan(TimeSpan age)
    {
        var cutoff = time.GetUtcNow().UtcDateTime - age;
        var pruned = new List<string>();
        foreach (var name in List())
        {
            var path = PathOf(name);
            if (File.GetCreationTimeUtc(path) >= cutoff) continue;
            if (TryDelete(path)) pruned.Add(name);
        }
        if (pruned.Count > 0) log.Write(LogEvent.PendingPruned, LogField.Count("files", pruned.Count));
        return pruned;
    }

    /// <summary>Deletes a closed pending file; a missing file is fine.</summary>
    public void Delete(string fileName) => TryDelete(PathOf(fileName));

    /// <summary>Deletes every pending file (history Clear).</summary>
    public void DeleteAll()
    {
        foreach (var name in List()) Delete(name);
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}

/// <summary>
/// A WAV being written. The header's sizes are placeholders until <see cref="FinaliseAsync"/>, which
/// <see cref="WavFile.ReadPcm"/> tolerates, so a crash mid-recording still leaves readable audio.
/// </summary>
public sealed class PendingRecording : IAsyncDisposable
{
    private readonly string _path;
    private FileStream? _stream;

    internal PendingRecording(string fileName, string path)
    {
        FileName = fileName;
        _path = path;
        _stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete);
        WavFile.WriteHeader(_stream, 0);
    }

    public string FileName { get; }

    public long DataBytes { get; private set; }

    public async ValueTask AppendAsync(ReadOnlyMemory<byte> pcm)
    {
        if (_stream is null) throw new ObjectDisposedException(nameof(PendingRecording));
        await _stream.WriteAsync(pcm);
        // Flush to the OS, not to disk: enough to survive an app crash without an fsync every 100 ms.
        _stream.Flush(flushToDisk: false);
        DataBytes += pcm.Length;
    }

    public async ValueTask FinaliseAsync()
    {
        if (_stream is null) return;
        _stream.Position = 0;
        WavFile.WriteHeader(_stream, DataBytes);
        _stream.Position = _stream.Length;
        await _stream.FlushAsync();
    }

    public async ValueTask DeleteAsync()
    {
        await DisposeAsync();
        try { File.Delete(_path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_stream is null) return;
        await _stream.DisposeAsync();
        _stream = null;
    }
}
