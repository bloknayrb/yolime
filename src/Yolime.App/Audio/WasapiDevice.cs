using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Yolime.Core.Audio;
using Yolime.Core.Capture;
using Yolime.Core.Logging;

namespace Yolime.App.Audio;

/// <summary>
/// The chosen microphone (or the default) through WASAPI shared mode, in Core's 16 kHz mono s16. A chosen device that
/// isn't connected falls back to the default rather than failing the hold.
/// </summary>
internal sealed class WasapiDevice(ILog log, Func<string?>? deviceId = null, TimeProvider? time = null) : ICaptureDevice
{
    /// <summary>Raised (on the capture thread) when the chosen microphone wasn't there and the default was used.</summary>
    public event Action? FellBack;

    public ILiveCapture Start()
    {
        var live = new WasapiLive(log, deviceId?.Invoke(), time ?? TimeProvider.System);
        if (live.FellBack) FellBack?.Invoke();
        return live;
    }

    /// <summary>The connected microphones, for Settings. Names are shown, never logged.</summary>
    public static IReadOnlyList<(string Id, string Name)> Microphones()
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            var list = new List<(string, string)>();
            foreach (var d in devices.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (d) list.Add((d.ID, d.FriendlyName));
            }
            return list;
        }
        catch (COMException)
        {
            return [];
        }
    }
}

internal sealed class WasapiLive : ILiveCapture
{
    private const int EAccessDenied = unchecked((int)0x80070005);
    private const int AudclntEDeviceInvalidated = unchecked((int)0x88890004);
    private const int ChunkBytes = 100 * AudioFormat.BytesPerMs;

    // About a minute of 100 ms chunks: the pipeline reads them as they come, so this only fills if it stalls.
    private readonly Channel<AudioChunk> _chunks = Channel.CreateBounded<AudioChunk>(new BoundedChannelOptions(600) { SingleReader = true, SingleWriter = true });
    private readonly WasapiRecorder _recorder;
    private readonly ILog _log;
    private readonly byte[] _pending = new byte[ChunkBytes];
    private int _pendingLength;
    private volatile bool _stopping;
    private int _dropped;
    private readonly TaskCompletionSource _firstAudio = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeProvider _time;
    private long _firstAudioAt;

    public Task FirstAudio => _firstAudio.Task;

    public long? FirstAudioAt => Interlocked.Read(ref _firstAudioAt) is var at && at != 0 ? at : null;

    public MicKind Kind { get; }

    /// <param name="deviceId">A chosen microphone, or null for the default (which follows Windows when it changes).</param>
    public WasapiLive(ILog log, string? deviceId = null, TimeProvider? time = null)
    {
        _log = log;
        _time = time ?? TimeProvider.System;
        try
        {
            var builder = new WasapiRecorderBuilder();
            var chosen = deviceId is null ? null : Find(deviceId);
            FellBack = deviceId is not null && chosen is null;
            Kind = KindOf(chosen);
            builder = chosen is null ? builder.WithDefaultDeviceStreamRouting() : builder.WithDevice(chosen);
            // Shared mode with AutoConvertPcm: the audio engine downmixes and resamples to our format.
            _recorder = builder
                .WithSharedMode()
                .WithFormat(new WaveFormat(AudioFormat.SampleRate, AudioFormat.BitsPerSample, AudioFormat.Channels))
                .Build();
            _recorder.DataAvailable += OnData;
            _recorder.RecordingStopped += OnStopped;
            _recorder.StartRecording();
        }
        catch (Exception ex) when (ex is not AudioCaptureException)
        {
            // NAudio surfaces failures as COMException, CoreAudioException or UnauthorizedAccessException depending on
            // the call; the HRESULT is what identifies the privacy switch.
            (_recorder as IDisposable)?.Dispose();
            throw new AudioCaptureException(ex.HResult == EAccessDenied || ex is UnauthorizedAccessException ? CaptureError.AccessDenied : CaptureError.NoDevice);
        }
    }

    /// <summary>The chosen microphone wasn't connected, so this records from the default.</summary>
    public bool FellBack { get; }

    // DEVPKEY_Device_EnumeratorName: BTHENUM, USB, HDAUDIO... Read for the timing log, never logged as text.
    private static readonly PropertyKey EnumeratorName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);

    /// <summary>The kind of the chosen mic, or of the default one the routed stream starts on. Unknown if unreadable.</summary>
    internal static MicKind KindOf(MMDevice? chosen)
    {
        try
        {
            if (chosen is not null) return MicKinds.FromEnumerator(chosen.Properties[EnumeratorName].Value as string);
            using var devices = new MMDeviceEnumerator();
            using var device = devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            return MicKinds.FromEnumerator(device.Properties[EnumeratorName].Value as string);
        }
        catch (Exception)
        {
            // Only for the timing log: whatever the property store throws (access, an odd variant type), the hold goes on.
            return MicKind.Unknown;
        }
    }

    internal static MMDevice? Find(string id)
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            var d = devices.GetDevice(id);
            if (d.State == DeviceState.Active) return d;
            d.Dispose();
            return null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    public void Stop()
    {
        _stopping = true;
        // The last buffers still arrive after this; OnStopped completes the stream after them.
        try { _recorder.StopRecording(); } catch (COMException) { Complete(null); }
    }

    public async IAsyncEnumerable<AudioChunk> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var chunk in _chunks.Reader.ReadAllAsync(ct)) yield return chunk;
    }

    public async ValueTask DisposeAsync()
    {
        _stopping = true;
        _recorder.DataAvailable -= OnData;
        await _recorder.DisposeAsync();
        Complete(null);
        if (_dropped > 0) _log.Write(LogEvent.StageFailed, LogField.Tag("stage", Core.Stage.Capture), LogField.Count("droppedChunks", _dropped));
    }

    private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        // The device is delivering: the start cue can play (on the first buffer, not the first full chunk), and the
        // timing log gets key-down to here.
        if (Interlocked.Read(ref _firstAudioAt) == 0) Interlocked.CompareExchange(ref _firstAudioAt, _time.GetTimestamp(), 0);
        _firstAudio.TrySetResult();
        // Batch into 100 ms chunks: the speech gate and the overlay level meter both assume that size.
        while (!buffer.IsEmpty)
        {
            var take = Math.Min(buffer.Length, ChunkBytes - _pendingLength);
            buffer[..take].CopyTo(_pending.AsSpan(_pendingLength));
            _pendingLength += take;
            buffer = buffer[take..];
            if (_pendingLength == ChunkBytes) Emit();
        }
    }

    private void Emit()
    {
        var pcm = _pending.AsSpan(0, _pendingLength).ToArray();
        _pendingLength = 0;
        // Never block the audio thread: if the reader stalls, drop and count rather than wait.
        if (!_chunks.Writer.TryWrite(new AudioChunk(pcm, Rms.Of(pcm)))) _dropped++;
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (_pendingLength > 0) Emit();
        if (e.Exception is null || _stopping)
        {
            Complete(null);
            return;
        }
        // Access revoked mid-recording (the privacy switch) is AccessDenied, so the notice links to the setting;
        // everything else, including AUDCLNT_E_DEVICE_INVALIDATED (unplugged), is DeviceLost.
        var revoked = e.Exception.HResult == EAccessDenied || e.Exception is UnauthorizedAccessException;
        Complete(new AudioCaptureException(revoked ? CaptureError.AccessDenied : CaptureError.DeviceLost));
    }

    private void Complete(Exception? error)
    {
        _chunks.Writer.TryComplete(error);
        _firstAudio.TrySetResult(); // nothing more is coming; no cue should wait on it
    }
}
