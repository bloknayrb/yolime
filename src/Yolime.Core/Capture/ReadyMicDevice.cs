using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Yolime.Core.Audio;
using Yolime.Core.Logging;

namespace Yolime.Core.Capture;

/// <summary>A raw buffer from a running device: when it was captured (on the app's clock) and whether the device flagged it silent.</summary>
public delegate void RawBufferHandler(ReadOnlySpan<byte> pcm, long capturedAt, bool silent);

/// <summary>A microphone kept running between holds: it hands over raw buffers and reports a lost device.</summary>
public interface IRawCapture : IAsyncDisposable
{
    /// <summary>Raised on the device's thread for every buffer. The handler must not keep the span.</summary>
    event RawBufferHandler? Buffer;

    /// <summary>The device went away, or access was revoked.</summary>
    event Action<AudioCaptureException>? Failed;

    MicKind Kind { get; }
}

/// <summary>
/// "Keep the microphone open for a minute after each hold". The device stays running after a hold that was a real
/// dictation, so the next one starts at once; buffers that arrive with no hold active are dropped in the callback,
/// before any copy. Each hold takes exactly the audio captured between its key-down and key-up (judged by capture
/// time, not arrival), so nothing from between holds reaches one, and the last word before key-up isn't cut.
/// </summary>
/// <remarks>
/// The device closes on this class's own timer a minute after the last dictation, or at once on <see cref="Suspend"/>
/// (lock, sleep, Pause hotkeys, the setting or the mic changing). A tap or a Right-Ctrl shortcut never extends the
/// minute. With the setting off, every hold goes to the cold device, exactly as before.
/// </remarks>
public sealed class ReadyMicDevice(Func<IRawCapture> open, Func<bool> enabled, ICaptureDevice cold, TimeProvider time, ILog log) : ICaptureDevice, IAsyncDisposable
{
    public static readonly TimeSpan KeepFor = TimeSpan.FromSeconds(60);

    /// <summary>How long a stopped hold waits for the device to deliver what it captured before the stop.</summary>
    public const int StopFallbackMs = 200;

    /// <summary>How long a hold on a warm device waits for its first buffer before the device is reopened.</summary>
    public const int WarmCheckMs = 200;

    private readonly object _gate = new();
    private IRawCapture? _raw;
    private Hold? _active;
    private bool _suspendPending;
    private ITimer? _closeTimer;

    private TimeProvider Time => time;

    /// <summary>Whether the device is open now (the tray tooltip says so). Raised from any thread.</summary>
    public event Action<bool>? OpenChanged;

    public ILiveCapture Start()
    {
        if (!enabled())
        {
            Suspend();
            return cold.Start();
        }
        lock (_gate)
        {
            // The previous hold first: a suspend that arrived during it must still close the device before this one.
            if (_active is { } previous) EndLocked(previous);
            _closeTimer?.Dispose();
            _closeTimer = null;
            _suspendPending = false;
            var start = MicStart.Warm;
            if (_raw is null)
            {
                OpenLocked();
                start = MicStart.Cold;
            }
            var hold = new Hold(this, time.GetTimestamp(), start, _raw!.Kind);
            _active = hold;
            if (start == MicStart.Warm)
            {
                // A warm stream can stall without an error (a Bluetooth headset drifting off): no buffer soon means reopen.
                hold.WarmCheck = time.CreateTimer(_ => CheckWarm(hold), null, TimeSpan.FromMilliseconds(WarmCheckMs), Timeout.InfiniteTimeSpan);
            }
            return hold;
        }
    }

    /// <summary>Closes the device now, or when the hold in progress ends.</summary>
    public void Suspend()
    {
        lock (_gate)
        {
            _closeTimer?.Dispose();
            _closeTimer = null;
            if (_active is not null) _suspendPending = true;
            else CloseLocked();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_active is { } hold) EndLocked(hold);
            _closeTimer?.Dispose();
            CloseLocked();
        }
        return ValueTask.CompletedTask;
    }

    // Under _gate. Throws AudioCaptureException, which the coordinator maps as it does for a cold start.
    private void OpenLocked()
    {
        var raw = open();
        raw.Buffer += (pcm, at, silent) => OnBuffer(raw, pcm, at, silent);
        raw.Failed += ex => OnFailed(raw, ex);
        _raw = raw;
        OpenChanged?.Invoke(true);
    }

    // Under _gate. The disposal itself is the raw capture's business (the App's runs it on the pool, never here).
    private void CloseLocked()
    {
        // A closed device has no minute left: a shortcut after a suspend mustn't revive the old one.
        _deadline = null;
        if (_raw is not { } raw) return;
        _raw = null;
        _ = raw.DisposeAsync();
        OpenChanged?.Invoke(false);
    }

    private void OnBuffer(IRawCapture raw, ReadOnlySpan<byte> pcm, long capturedAt, bool silent)
    {
        lock (_gate)
        {
            // Between holds, or a device already replaced: dropped here, before any copy.
            if (raw != _raw || _active is not { } hold) return;
            if (hold.Accept(pcm, capturedAt, silent)) EndLocked(hold);
        }
    }

    private void OnFailed(IRawCapture raw, AudioCaptureException ex)
    {
        lock (_gate)
        {
            if (raw != _raw) return;
            var hold = _active;
            _active = null;
            CloseLocked();
            // With no hold in progress there's nobody to tell: the next hold just opens the device again.
            hold?.Finish(ex);
            if (hold is not null) log.Write(LogEvent.StageFailed, LogField.Tag("stage", Stage.Capture), LogField.Tag("error", ex.Error));
        }
    }

    private void CheckWarm(Hold hold)
    {
        lock (_gate)
        {
            if (_active != hold || hold.HadSound) return;
            // Nothing (or only buffers the device flagged silent) since key-down: reopen for this same hold.
            CloseLocked();
            try
            {
                OpenLocked();
                hold.MicStart = MicStart.WarmReopened;
            }
            catch (AudioCaptureException ex)
            {
                _active = null;
                hold.Finish(ex);
            }
        }
    }

    private void StopHold(Hold hold)
    {
        lock (_gate)
        {
            if (_active != hold) return;
            hold.StopAt = time.GetTimestamp();
            // The buffers captured before key-up still arrive after it; a device that stops delivering ends it anyway.
            hold.StopTimer = time.CreateTimer(_ =>
            {
                lock (_gate)
                {
                    if (_active == hold) EndLocked(hold);
                }
            }, null, TimeSpan.FromMilliseconds(StopFallbackMs), Timeout.InfiniteTimeSpan);
        }
    }

    private void DetachHold(Hold hold)
    {
        lock (_gate)
        {
            if (_active == hold) EndLocked(hold);
            else hold.Finish(null);
        }
    }

    // Under _gate.
    private void EndLocked(Hold hold)
    {
        if (_active == hold) _active = null;
        hold.Finish(null);
        if (_suspendPending || !hold.Dictation)
        {
            // A suspend that came during the hold, or a tap or shortcut that never became a dictation: no minute starts.
            // A tap still leaves an earlier dictation's minute running, if there is one.
            if (_suspendPending || _closeTimer is null && hold.DeadlineFromEarlier is null) CloseLocked();
            else if (hold.DeadlineFromEarlier is { } earlier) ScheduleCloseLocked(earlier);
            _suspendPending = false;
            return;
        }
        ScheduleCloseLocked(time.GetTimestamp() + (long)(KeepFor.TotalSeconds * time.TimestampFrequency));
    }

    private long? _deadline;

    // Under _gate.
    private void ScheduleCloseLocked(long deadline)
    {
        _deadline = deadline;
        _closeTimer?.Dispose();
        var due = time.GetElapsedTime(time.GetTimestamp(), deadline);
        _closeTimer = time.CreateTimer(_ =>
        {
            lock (_gate)
            {
                if (_active is null && _deadline == deadline) CloseLocked();
            }
        }, null, due < TimeSpan.Zero ? TimeSpan.Zero : due, Timeout.InfiniteTimeSpan);
    }

    private sealed class Hold : ILiveCapture
    {
        private const int ChunkBytes = 100 * AudioFormat.BytesPerMs;

        private readonly ReadyMicDevice _owner;
        private readonly Channel<AudioChunk> _chunks = Channel.CreateBounded<AudioChunk>(new BoundedChannelOptions(600) { SingleReader = true, SingleWriter = true });
        private readonly byte[] _pending = new byte[ChunkBytes];
        private readonly TaskCompletionSource _firstAudio = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _pendingLength;
        private long _firstAudioAt;
        private bool _finished;

        public Hold(ReadyMicDevice owner, long startAt, MicStart start, MicKind kind)
        {
            _owner = owner;
            StartAt = startAt;
            MicStart = start;
            Kind = kind;
            // A tap during an earlier dictation's minute must leave that minute as it was.
            DeadlineFromEarlier = owner._deadline;
        }

        public long StartAt { get; }
        public long? StopAt { get; set; }
        public long? DeadlineFromEarlier { get; }
        public ITimer? StopTimer { get; set; }
        public ITimer? WarmCheck { get; set; }
        public bool Dictation { get; private set; }
        public bool HadSound { get; private set; }
        public MicStart MicStart { get; set; }
        public MicKind Kind { get; }
        public Task FirstAudio => _firstAudio.Task;
        public long? FirstAudioAt => _firstAudioAt == 0 ? null : _firstAudioAt;

        public void Dictating() => Dictation = true;

        public void Stop() => _owner.StopHold(this);

        /// <summary>Under the owner's lock. Returns whether this buffer ends the hold (it reaches past key-up).</summary>
        public bool Accept(ReadOnlySpan<byte> pcm, long capturedAt, bool silent)
        {
            if (_finished) return true;
            var time = _owner.Time;
            var bufferEnd = capturedAt + BytesToTicks(pcm.Length, time);
            var from = capturedAt < StartAt ? Math.Min(pcm.Length, TicksToBytes(StartAt - capturedAt, time)) : 0;
            var to = pcm.Length;
            var ends = false;
            if (StopAt is { } stop)
            {
                if (capturedAt >= stop) return true;
                if (bufferEnd >= stop)
                {
                    to = Math.Min(pcm.Length, TicksToBytes(stop - capturedAt, time));
                    ends = true;
                }
            }
            if (to > from)
            {
                if (!silent) HadSound = true;
                if (_firstAudioAt == 0)
                {
                    _firstAudioAt = time.GetTimestamp();
                    _firstAudio.TrySetResult();
                }
                Append(pcm[from..to]);
            }
            return ends;
        }

        private void Append(ReadOnlySpan<byte> pcm)
        {
            while (!pcm.IsEmpty)
            {
                var take = Math.Min(pcm.Length, ChunkBytes - _pendingLength);
                pcm[..take].CopyTo(_pending.AsSpan(_pendingLength));
                _pendingLength += take;
                pcm = pcm[take..];
                if (_pendingLength == ChunkBytes) Emit();
            }
        }

        private void Emit()
        {
            var pcm = _pending.AsSpan(0, _pendingLength).ToArray();
            _pendingLength = 0;
            _chunks.Writer.TryWrite(new AudioChunk(pcm, Rms.Of(pcm)));
        }

        /// <summary>Under the owner's lock (or once the hold is detached): flushes the partial chunk and ends the stream.</summary>
        public void Finish(Exception? error)
        {
            if (_finished) return;
            _finished = true;
            StopTimer?.Dispose();
            WarmCheck?.Dispose();
            if (error is null && _pendingLength > 0) Emit();
            _chunks.Writer.TryComplete(error);
            _firstAudio.TrySetResult(); // nothing more is coming; no cue should wait on it
        }

        public async IAsyncEnumerable<AudioChunk> ReadAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var chunk in _chunks.Reader.ReadAllAsync(ct)) yield return chunk;
        }

        public ValueTask DisposeAsync()
        {
            _owner.DetachHold(this);
            return ValueTask.CompletedTask;
        }

        // Whole samples only, so a cut never splits one.
        private static int TicksToBytes(long ticks, TimeProvider time) =>
            (int)Math.Min(int.MaxValue, ticks * 1000 * AudioFormat.BytesPerMs / time.TimestampFrequency) & ~1;

        private static long BytesToTicks(int bytes, TimeProvider time) => (long)bytes * time.TimestampFrequency / (1000L * AudioFormat.BytesPerMs);
    }
}
