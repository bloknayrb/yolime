using Yolime.Core.Logging;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Yolime.Core.Audio;
using Yolime.Core.Capture;
using Yolime.Core.Feedback;
using Yolime.Core.Hotkeys;
using Yolime.Core.Output;
using Yolime.Core.Overlay;
using Yolime.Core.Pipeline;
using Yolime.Core.Tests.Pipeline;

namespace Yolime.Core.Tests.Capture;

/// <summary>The coordinator as the overlay's source: what it reports, in what order, and that notices go there.</summary>
public sealed class CoordinatorOverlayTests : IDisposable
{
    private static readonly TargetSnapshot Word = new(100, 1, "WINWORD", "OpusApp", "42.1", "_WwG", false, true, false);

    private readonly PipelineRig _rig = new();
    private readonly HoldStateMachine _machine = new(new HoldOptions());
    private readonly List<OverlayInput> _overlay = [];
    private readonly List<Notice> _balloons = [];
    private readonly Device _device = new();
    private readonly CaptureCoordinator _c;
    private int _taps;

    public CoordinatorOverlayTests()
    {
        // A slow-mic test that failed before resetting it mustn't hand slow mics to the tests after it.
        Device.Live.NextIsSlow = false;
        _c = new CaptureCoordinator(_rig.Build(), _device, new Targets(), _rig.Stt, _rig.History, new Balloons(_balloons), new NoSounds(),
            act => act(_machine), _rig.Time, _rig.Log, null, () => _rig.Settings,
            overlay: i => { lock (_overlay) _overlay.Add(i); }, displayName: p => p == "WINWORD" ? "Word" : p ?? "");
        _c.HotkeyTapped += () => _taps++;
    }

    public void Dispose() => _rig.Dispose();

    private void Input(HotkeyKind kind, long at)
    {
        foreach (var e in _machine.Handle(new HotkeyInput(kind, at, true), Mode.Dictate).Effects) _c.Handle(e);
    }

    private List<OverlayInput> Seen()
    {
        lock (_overlay) return [.. _overlay];
    }

    private async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    [Fact]
    public async Task AHold_ReportsStart_End_Transcript_AndTheResult_InOrder()
    {
        Input(HotkeyKind.HoldDown, 0);
        _device.Last!.Speak();
        Input(HotkeyKind.Tick, 300);
        Input(HotkeyKind.HoldUp, 1500);
        await Until(() => Seen().OfType<RunFinished>().Any());

        var kinds = Seen().Where(i => i is not Level and not StageBegan and not AppIdentified).Select(i => i.GetType().Name).ToList();
        Assert.Equal(["RecordingStarted", "RecordingEnded", "TranscriptShown", "RunFinished"], kinds);
        Assert.Contains(Seen(), i => i is StageBegan { Stage: Stage.Output });
        // The target is read beside the mic and may land after the notch shows: the name follows when it does.
        Assert.Equal("Word", Seen().OfType<AppIdentified>().Single().AppName);
        Assert.Contains(Seen(), i => i is Level);
        Assert.Empty(_balloons); // the overlay is there: nothing goes to the tray
    }

    [Fact]
    public async Task ARunNotice_GoesToTheOverlay_WithTheResult()
    {
        _rig.Stt.Throw = new Yolime.Core.Stt.SttException(Yolime.Core.Stt.SttError.Network);
        Input(HotkeyKind.HoldDown, 0);
        _device.Last!.Speak();
        Input(HotkeyKind.Tick, 300);
        Input(HotkeyKind.HoldUp, 1500);
        await Until(() => Seen().OfType<RunFinished>().Any());

        var done = Seen().OfType<RunFinished>().Single();
        Assert.Equal(NoticeKind.ServiceUnreachable, done.Notice!.Kind);
        Assert.Empty(_balloons);
    }

    [Fact]
    public async Task AHoldTheBusyPipelineTurnsAway_LeavesListening_AndSaysBusy()
    {
        // A retry (or anything) already running: the new hold's run comes back Busy with no run id of its own.
        var pipeline = _rig.Build();
        var c = new CaptureCoordinator(pipeline, _device, new Targets(), _rig.Stt, _rig.History, new Balloons(_balloons), new NoSounds(),
            act => act(_machine), _rig.Time, _rig.Log, null, () => _rig.Settings, overlay: i => { lock (_overlay) _overlay.Add(i); });
        using var occupy = new CancellationTokenSource();
        var running = pipeline.RunAsync(new RunRequest(new Never(), Mode.Raw, "x", new TaskCompletionSource<RunIntent>().Task), occupy.Token);

        foreach (var (k, at) in new[] { (HotkeyKind.HoldDown, 0L), (HotkeyKind.Tick, 300L) })
            foreach (var e in _machine.Handle(new HotkeyInput(k, at, true), Mode.Dictate).Effects) c.Handle(e);
        await Until(() => Seen().OfType<NoticeOnly>().Any(n => n.Notice.Kind == NoticeKind.Busy));
        Assert.Contains(Seen(), i => i is Dismissed);

        occupy.Cancel();
        await running;
    }

    private sealed class Never : IAudioSource
    {
        public async IAsyncEnumerable<AudioChunk> ReadAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            yield break;
        }
    }

    [Fact]
    public void AModeCycle_IsReported()
    {
        Input(HotkeyKind.HoldDown, 0);
        Input(HotkeyKind.Tick, 300);
        Input(HotkeyKind.ShiftDown, 500);
        Assert.Empty(Seen().OfType<ModeChanged>()); // on the up, so a Shift held at release can preview instead
        Input(HotkeyKind.ShiftUp, 550);
        Assert.Equal(Mode.Prompt, Seen().OfType<ModeChanged>().Single().Mode);
    }

    [Fact]
    public void ABareShortTap_IsReported_SoAnErrorCardCanRetry()
    {
        Input(HotkeyKind.HoldDown, 0);
        Input(HotkeyKind.HoldUp, 120);
        Assert.Equal(1, _taps);
        Assert.DoesNotContain(Seen(), i => i is RecordingStarted); // a tap never shows the notch
    }

    [Fact]
    public void AWatchdogTap_IsNotReported_SoItCanNeverRetry()
    {
        // A tap inferred from the key's state, not a key-up the hook saw: it may be a hold misread at key-down.
        Input(HotkeyKind.HoldDown, 0);
        _c.Handle(new Discard(DiscardReason.ShortTap, FromWatchdog: true));
        Assert.Equal(0, _taps);
    }

    [Fact]
    public void EveryDiscard_IsLogged()
    {
        Input(HotkeyKind.HoldDown, 0);
        Input(HotkeyKind.HoldUp, 120);
        Assert.Contains(LogEvent.HoldDiscarded, _rig.Log.Events);
    }

    [Fact]
    public void ADiscardWhileRecording_HidesTheNotch_AndSaysWhyOnIt()
    {
        Input(HotkeyKind.HoldDown, 0);
        Input(HotkeyKind.Tick, 300);
        Input(HotkeyKind.OtherKey, 600);
        Assert.Contains(Seen(), i => i is Dismissed);
        Assert.Equal(NoticeKind.Discarded, Seen().OfType<NoticeOnly>().Single().Notice.Kind);
        Assert.Empty(_balloons);
    }

    // "Mic starting…": the card is told whether the mic is delivering yet, and when it starts (latency leftovers, PR A).

    private void SlowMic(bool slow) => Device.Live.NextIsSlow = slow;

    [Fact]
    public async Task ASlowMic_StartsTheCardNotReady_ThenReportsItFlowing()
    {
        SlowMic(true);
        Input(HotkeyKind.HoldDown, 0);
        Input(HotkeyKind.Tick, 300);
        var started = Seen().OfType<RecordingStarted>().Single();
        Assert.False(started.MicReady);
        Assert.Empty(Seen().OfType<AudioFlowing>());
        _device.Last!.FirstAudioSource.SetResult();
        await Until(() => Seen().OfType<AudioFlowing>().Any());
        var flowing = Seen().OfType<AudioFlowing>().Single();
        Assert.True(flowing.Delivered);
        Assert.Equal(started.RunId, flowing.RunId);
        SlowMic(false);
        Input(HotkeyKind.HoldUp, 1500);
    }

    [Fact]
    public async Task AMicAlreadyDelivering_StartsTheCardReady_WithNoFlowingReport()
    {
        SlowMic(false);
        Input(HotkeyKind.HoldDown, 0);
        Input(HotkeyKind.Tick, 300);
        Assert.True(Seen().OfType<RecordingStarted>().Single().MicReady);
        await Task.Delay(100);
        Assert.Empty(Seen().OfType<AudioFlowing>());
        Input(HotkeyKind.HoldUp, 1500);
    }

    [Fact]
    public async Task AMicThatNeverDelivers_IsReportedNotDelivered_AtTheCap()
    {
        SlowMic(true);
        Input(HotkeyKind.HoldDown, 0);
        Input(HotkeyKind.Tick, 300);
        await Task.Delay(50);
        _rig.Time.Advance(TimeSpan.FromSeconds(2));
        await Until(() => Seen().OfType<AudioFlowing>().Any());
        Assert.False(Seen().OfType<AudioFlowing>().Single().Delivered);
        SlowMic(false);
        Input(HotkeyKind.HoldUp, 3000);
    }

    [Fact]
    public async Task AFirstBufferAfterTheHoldEnded_ReportsNothing_EvenIntoTheNextHold()
    {
        // A stale "flowing" must never land on the next hold's card.
        SlowMic(true);
        Input(HotkeyKind.HoldDown, 0);
        Input(HotkeyKind.Tick, 300);
        var first = _device.Last!;
        Input(HotkeyKind.HoldUp, 800);
        await Until(() => Seen().OfType<RunFinished>().Any());
        SlowMic(false);
        Input(HotkeyKind.HoldDown, 5_000);
        Input(HotkeyKind.Tick, 5_300);
        first.FirstAudioSource.SetResult();
        await Task.Delay(100);
        Assert.Empty(Seen().OfType<AudioFlowing>());
        Input(HotkeyKind.HoldUp, 6_500);
    }

    [Fact]
    public void TheCapWarning_StartsACountdown()
    {
        var machine = new HoldStateMachine(new HoldOptions(HeldCapMs: 20_000, CapWarningMs: 10_000));
        var c = new CaptureCoordinator(_rig.Build(), _device, new Targets(), _rig.Stt, _rig.History, new Balloons(_balloons), new NoSounds(),
            act => act(machine), _rig.Time, _rig.Log, null, () => _rig.Settings, overlay: i => { lock (_overlay) _overlay.Add(i); });
        foreach (var (k, at) in new[] { (HotkeyKind.HoldDown, 0L), (HotkeyKind.Tick, 300L), (HotkeyKind.Tick, 10_000L) })
            foreach (var e in machine.Handle(new HotkeyInput(k, at, true), Mode.Dictate).Effects) c.Handle(e);
        Assert.Equal(10, Seen().OfType<CapCountdown>().Single().SecondsLeft);
    }

    private sealed class Balloons(List<Notice> shown) : INoticeSink
    {
        public void Show(Notice notice)
        {
            lock (shown) shown.Add(notice);
        }
    }

    private sealed class NoSounds : ISounds
    {
        public void Play(SoundKind sound) { }
        public void PlayModeCue(Mode mode) { }
    }

    private sealed class Targets : ITargetReader
    {
        public TargetSnapshot Snapshot() => Word;
        public nint Foreground() => Word.Hwnd;
        public Task WaitForIdleAsync(nint hwnd, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Device : ICaptureDevice
    {
        public Live? Last;
        public ILiveCapture Start() => Last = new Live();

        public sealed class Live : ILiveCapture
        {
            private readonly Channel<AudioChunk> _chunks = Channel.CreateUnbounded<AudioChunk>();

            /// <summary>Set before the hold starts: the mic hasn't delivered by the time the card shows.</summary>
            public static bool NextIsSlow;
            public TaskCompletionSource FirstAudioSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly bool _slow = NextIsSlow;
            public Task FirstAudio => _slow ? FirstAudioSource.Task : Task.CompletedTask;

            public void Speak()
            {
                var pcm = WavFile.ReadPcm(Fixtures.Audio("speech-short.wav"));
                for (var o = 0; o < pcm.Length; o += 3200)
                {
                    var c = pcm.AsMemory(o, Math.Min(3200, pcm.Length - o));
                    _chunks.Writer.TryWrite(new AudioChunk(c, Rms.Of(c.Span)));
                }
            }

            public void Stop() => _chunks.Writer.TryComplete();

            public async IAsyncEnumerable<AudioChunk> ReadAsync([EnumeratorCancellation] CancellationToken ct)
            {
                await foreach (var c in _chunks.Reader.ReadAllAsync(ct)) yield return c;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
