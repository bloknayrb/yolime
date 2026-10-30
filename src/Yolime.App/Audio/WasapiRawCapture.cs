using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Yolime.Core.Audio;
using Yolime.Core.Capture;

namespace Yolime.App.Audio;

/// <summary>
/// The mic kept running between holds (<see cref="ReadyMicDevice"/>): every buffer goes to <see cref="Buffer"/> with
/// its capture time, and nothing is kept here. Same device choice and fallback as <see cref="WasapiLive"/>.
/// </summary>
internal sealed class WasapiRawCapture : IRawCapture
{
    private const int EAccessDenied = unchecked((int)0x80070005);

    private readonly WasapiRecorder _recorder;
    private volatile bool _stopping;

    public event RawBufferHandler? Buffer;
    public event Action<AudioCaptureException>? Failed;

    public MicKind Kind { get; }

    /// <summary>The chosen microphone wasn't connected, so this records from the default.</summary>
    public bool FellBack { get; }

    public WasapiRawCapture(string? deviceId)
    {
        try
        {
            var builder = new WasapiRecorderBuilder();
            var chosen = deviceId is null ? null : WasapiLive.Find(deviceId);
            FellBack = deviceId is not null && chosen is null;
            Kind = WasapiLive.KindOf(chosen);
            builder = chosen is null ? builder.WithDefaultDeviceStreamRouting() : builder.WithDevice(chosen);
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
            (_recorder as IDisposable)?.Dispose();
            throw new AudioCaptureException(ex.HResult == EAccessDenied || ex is UnauthorizedAccessException ? CaptureError.AccessDenied : CaptureError.NoDevice);
        }
    }

    private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        // WASAPI's capture time is in 100 ns units of the performance counter; the app's clock is Stopwatch ticks.
        var capturedAt = qpcPosition > 0 ? (long)(qpcPosition * (Stopwatch.Frequency / 10_000_000.0)) : Stopwatch.GetTimestamp();
        Buffer?.Invoke(buffer, capturedAt, (flags & AudioClientBufferFlags.Silent) != 0);
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null || _stopping) return;
        var revoked = e.Exception.HResult == EAccessDenied || e.Exception is UnauthorizedAccessException;
        Failed?.Invoke(new AudioCaptureException(revoked ? CaptureError.AccessDenied : CaptureError.DeviceLost));
    }

    /// <summary>
    /// Returns at once: stopping a recorder joins its capture thread, which must never happen on the thread that asked
    /// (it can be the one servicing the keyboard hook).
    /// </summary>
    public ValueTask DisposeAsync()
    {
        _stopping = true;
        _recorder.DataAvailable -= OnData;
        _ = Task.Run(async () =>
        {
            try { await _recorder.DisposeAsync(); }
            catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
        });
        return ValueTask.CompletedTask;
    }
}
