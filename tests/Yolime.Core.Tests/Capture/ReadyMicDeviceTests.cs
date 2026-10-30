using Microsoft.Extensions.Time.Testing;
using Yolime.Core.Audio;
using Yolime.Core.Capture;

namespace Yolime.Core.Tests.Capture;

public class ReadyMicDeviceTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly List<FakeRaw> _opened = [];
    private readonly ColdDevice _cold = new();
    private bool _enabled = true;
    private readonly ReadyMicDevice _device;

    public ReadyMicDeviceTests()
    {
        _device = new ReadyMicDevice(() =>
        {
            var raw = new FakeRaw();
            _opened.Add(raw);
            return raw;
        }, () => _enabled, _cold, _time, new RecordingLog());
    }

    private FakeRaw Raw => _opened[^1];
    private long Now => _time.GetTimestamp();
    private static byte[] Pcm(int ms, byte value = 7) => Enumerable.Repeat(value, ms * AudioFormat.BytesPerMs).ToArray();

    private static async Task<int> Bytes(ILiveCapture live)
    {
        var n = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var c in live.ReadAsync(cts.Token)) n += c.Pcm.Length;
        return n;
    }

    private void Advance(int ms) => _time.Advance(TimeSpan.FromMilliseconds(ms));

    [Fact]
    public void Off_UsesTheColdDevice_EveryTime()
    {
        _enabled = false;
        _device.Start();
        _device.Start();
        Assert.Equal(2, _cold.Starts);
        Assert.Empty(_opened);
    }

    [Fact]
    public async Task AudioBetweenHolds_IsNeverDelivered_ToAnyHold()
    {
        var a = _device.Start();
        a.Dictating();
        Raw.Deliver(Pcm(200), Now);
        a.Stop();
        Advance(10);
        Raw.Deliver(Pcm(100), Now); // after A's stop: A's tail is done
        Advance(300);
        Raw.Deliver(Pcm(3000, 9), Now); // the gap between holds
        Advance(3000);
        var b = _device.Start();
        Advance(10);
        Raw.Deliver(Pcm(500), Now);
        b.Stop();
        Advance(10);
        Raw.Deliver(Pcm(100), Now);
        Assert.Equal(200 * AudioFormat.BytesPerMs, await Bytes(a));
        Assert.Equal(500 * AudioFormat.BytesPerMs, await Bytes(b));
        Assert.Single(_opened); // the second hold reused the open device
    }

    [Fact]
    public async Task ABufferCapturedBeforeKeyDown_ButDeliveredAfter_ContributesOnlyItsLaterPart()
    {
        var first = _device.Start();
        first.Dictating();
        first.Stop();
        Raw.Deliver(Pcm(10), Now + 1);
        Advance(1000);
        var captured = Now; // a 100 ms buffer captured from here...
        Advance(40); // ...key-down 40 ms into it, and it's delivered only now
        var hold = _device.Start();
        Raw.Deliver(Pcm(100), captured);
        hold.Stop();
        Advance(10);
        Raw.Deliver(Pcm(10), Now);
        Assert.Equal(60 * AudioFormat.BytesPerMs, await Bytes(hold));
    }

    [Fact]
    public async Task Stop_KeepsWhatWasCapturedBeforeIt_IncludingThePartialChunk()
    {
        var hold = _device.Start();
        var t = Now;
        Advance(250);
        hold.Stop(); // key-up at +250 ms; the last buffers arrive after it
        Raw.Deliver(Pcm(130), t);
        Raw.Deliver(Pcm(130), t + Ms(130)); // straddles the stop: 120 ms of it are before
        Raw.Deliver(Pcm(100), t + Ms(260)); // after the stop: ends the hold
        Assert.Equal(250 * AudioFormat.BytesPerMs, await Bytes(hold));
    }

    [Fact]
    public async Task ADeviceThatStopsDelivering_StillEndsTheHold_AfterTheFallback()
    {
        var hold = _device.Start();
        Raw.Deliver(Pcm(100), Now);
        hold.Stop();
        Advance(ReadyMicDevice.StopFallbackMs);
        Assert.Equal(100 * AudioFormat.BytesPerMs, await Bytes(hold));
    }

    [Fact]
    public async Task FirstAudio_WaitsForABufferForThisHold_EvenWhenWarm()
    {
        var a = _device.Start();
        a.Dictating();
        Raw.Deliver(Pcm(100), Now);
        a.Stop();
        Advance(10);
        Raw.Deliver(Pcm(10), Now);
        var b = _device.Start();
        Assert.False(b.FirstAudio.IsCompleted);
        Assert.Equal(MicStart.Warm, b.MicStart);
        Advance(10);
        Raw.Deliver(Pcm(10), Now);
        await b.FirstAudio.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(b.FirstAudioAt);
    }

    [Fact]
    public void AWarmStreamThatDeliversNothing_IsReopenedForTheSameHold()
    {
        var a = _device.Start();
        a.Dictating();
        a.Stop();
        Raw.Deliver(Pcm(10), Now + 1);
        var stale = Raw;
        var b = _device.Start();
        Advance(ReadyMicDevice.WarmCheckMs);
        Assert.Equal(2, _opened.Count);
        Assert.True(stale.Disposed);
        Assert.Equal(MicStart.WarmReopened, b.MicStart);
    }

    [Fact]
    public void TheDevice_ClosesOnItsOwnTimer_AMinuteAfterADictation()
    {
        var a = _device.Start();
        a.Dictating();
        a.Stop();
        Raw.Deliver(Pcm(10), Now + 1);
        Advance(59_000);
        Assert.False(Raw.Disposed);
        Advance(2_000);
        Assert.True(Raw.Disposed);
    }

    [Fact]
    public void ATapOrAShortcut_DoesNotKeepTheDeviceOpen()
    {
        // Right Ctrl shortcuts all day must not keep the mic lit: only a real dictation extends the minute.
        var tap = _device.Start();
        tap.Stop();
        Raw.Deliver(Pcm(10), Now + 1);
        Advance(ReadyMicDevice.StopFallbackMs + 100);
        Assert.True(Raw.Disposed);
    }

    [Fact]
    public void ATapWithinTheMinute_DoesNotExtendIt()
    {
        var a = _device.Start();
        a.Dictating();
        a.Stop();
        Raw.Deliver(Pcm(10), Now + 1);
        Advance(50_000);
        var tap = _device.Start();
        Raw.Deliver(Pcm(10), Now);
        tap.Stop();
        Advance(5);
        Raw.Deliver(Pcm(10), Now);
        Advance(1_000);
        Assert.False(Raw.Disposed); // nor does it end it early
        Advance(10_000);
        Assert.True(Raw.Disposed);
    }

    [Fact]
    public void AShortcutAfterASuspend_ClosesTheMicAtOnce_ItDoesNotRevivetheOldMinute()
    {
        var a = _device.Start();
        a.Dictating();
        a.Stop();
        Raw.Deliver(Pcm(10), Now + 1);
        Advance(5_000);
        _device.Suspend(); // locked, paused or the mic changed
        Advance(15_000);
        var shortcut = _device.Start(); // Right Ctrl + C: opens cold, never becomes a dictation
        shortcut.Stop();
        Raw.Deliver(Pcm(10), Now + 1);
        Assert.True(Raw.Disposed);
    }

    [Fact]
    public void ASuspendDuringAHold_IsKept_EvenIfTheNextPressComesBeforeTheTail()
    {
        var a = _device.Start();
        a.Dictating();
        _device.Suspend(); // during A: takes effect when A ends
        a.Stop();
        var stale = Raw;
        var b = _device.Start(); // pressed again before A's tail buffer arrived
        Assert.True(stale.Disposed);
        Assert.Equal(MicStart.Cold, b.MicStart);
    }

    [Fact]
    public void Suspend_ClosesAtOnce_AndTheNextHoldOpensCold()
    {
        var a = _device.Start();
        a.Dictating();
        a.Stop();
        Raw.Deliver(Pcm(10), Now + 1);
        _device.Suspend();
        Assert.True(Raw.Disposed);
        var b = _device.Start();
        Assert.Equal(2, _opened.Count);
        Assert.Equal(MicStart.Cold, b.MicStart);
    }

    [Fact]
    public async Task SuspendDuringAHold_WaitsForTheHoldToEnd()
    {
        var a = _device.Start();
        a.Dictating();
        _device.Suspend();
        Assert.False(Raw.Disposed);
        Raw.Deliver(Pcm(100), Now);
        a.Stop();
        Advance(10);
        Raw.Deliver(Pcm(10), Now);
        Assert.Equal(100 * AudioFormat.BytesPerMs, await Bytes(a));
        Assert.True(Raw.Disposed);
    }

    [Fact]
    public async Task ADeviceErrorWithNoHold_ClosesSilently_AndADeviceErrorInAHold_FailsIt()
    {
        var a = _device.Start();
        a.Dictating();
        Raw.Deliver(Pcm(100), Now);
        Raw.Fail(new AudioCaptureException(CaptureError.DeviceLost));
        await Assert.ThrowsAsync<AudioCaptureException>(() => Bytes(a));
        var b = _device.Start();
        Assert.Equal(2, _opened.Count);
        b.Dictating();
        b.Stop();
        Raw.Deliver(Pcm(10), Now + 1);
        Raw.Fail(new AudioCaptureException(CaptureError.DeviceLost)); // between holds: nothing to tell anyone
        var c = _device.Start();
        Assert.Equal(3, _opened.Count);
        Assert.Equal(MicStart.Cold, c.MicStart);
    }

    [Fact]
    public async Task DisposingAnOldHold_AfterTheNextBegan_LeavesTheNextRecording()
    {
        var a = _device.Start();
        a.Dictating();
        a.Stop();
        Raw.Deliver(Pcm(10), Now + 1);
        var b = _device.Start();
        await a.DisposeAsync();
        Advance(10);
        Raw.Deliver(Pcm(100), Now);
        b.Stop();
        Advance(10);
        Raw.Deliver(Pcm(10), Now);
        Assert.Equal(100 * AudioFormat.BytesPerMs, await Bytes(b));
    }

    [Fact]
    public async Task DisposeWithoutStop_ClosesTheGate()
    {
        var a = _device.Start();
        await a.DisposeAsync();
        Raw.Deliver(Pcm(100), Now);
        var b = _device.Start();
        Advance(10);
        Raw.Deliver(Pcm(100), Now);
        b.Stop();
        Advance(10);
        Raw.Deliver(Pcm(10), Now);
        Assert.Equal(100 * AudioFormat.BytesPerMs, await Bytes(b));
    }

    private long Ms(int ms) => ms * _time.TimestampFrequency / 1000;

    private sealed class FakeRaw : IRawCapture
    {
        public event RawBufferHandler? Buffer;
        public event Action<AudioCaptureException>? Failed;
        public bool Disposed { get; private set; }
        public MicKind Kind => MicKind.Usb;

        public void Deliver(byte[] pcm, long capturedAt) => Buffer?.Invoke(pcm, capturedAt, silent: false);

        public void Fail(AudioCaptureException ex) => Failed?.Invoke(ex);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ColdDevice : ICaptureDevice
    {
        public int Starts;

        public ILiveCapture Start()
        {
            Starts++;
            return new NullLive();
        }

        private sealed class NullLive : ILiveCapture
        {
            public void Stop() { }
            public async IAsyncEnumerable<AudioChunk> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
            {
                await Task.CompletedTask;
                yield break;
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
