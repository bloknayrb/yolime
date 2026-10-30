using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Yolime.Core.Audio;
using Yolime.Core.Capture;
using Yolime.Core.Feedback;
using Yolime.Core.Hotkeys;
using Yolime.Core.Llm;
using Yolime.Core.Modes;
using Yolime.Core.Pipeline;
using Yolime.Core.Output;
using Yolime.Core.Stt;
using Yolime.Core.Tests.Pipeline;

namespace Yolime.Core.Tests.Capture;

public sealed class CaptureCoordinatorTests : IDisposable
{
    private static readonly TargetSnapshot Word = new(100, 1, "WINWORD", "OpusApp", "42.1", "_WwG", false, true, false);

    private readonly PipelineRig _rig = new();
    private readonly FakeDevice _device = new();
    private readonly FakeTargets _targets = new();
    private readonly Notes _notes = new();
    private readonly List<SoundKind> _sounds = [];
    private readonly List<Mode> _cues = [];
    private readonly FakeContext _context = new();
    private readonly HoldStateMachine _machine = new(new HoldOptions());
    private CaptureCoordinator _c;

    public CaptureCoordinatorTests()
    {
        var pipeline = _rig.Build();
        _c = new CaptureCoordinator(pipeline, _device, _targets, _rig.Stt, _rig.History, _notes, new Sounds(_sounds, _cues),
            act => act(_machine), _rig.Time, _rig.Log, _context, () => _rig.Settings);
    }

    public void Dispose() => _rig.Dispose();

    // Drives the real machine and forwards its effects, exactly as the hook thread does.
    private void Input(HotkeyKind kind, long at, bool down = true, Mode start = Mode.Dictate)
    {
        var (_, effects) = _machine.Handle(new HotkeyInput(kind, at, down), start);
        foreach (var e in effects) _c.Handle(e);
    }

    private async Task WaitIdle()
    {
        for (var i = 0; i < 200 && _machine.State != HoldState.Idle; i++) await Task.Delay(10);
    }

    [Fact]
    public async Task Hold_FullFlow_Delivers_AndFreesTheMachine()
    {
        Input(HotkeyKind.HoldDown, 0);
        _device.Last!.Speak();
        Input(HotkeyKind.Tick, 300);
        Input(HotkeyKind.HoldUp, 1500);
        await WaitIdle();

        Assert.Equal(HoldState.Idle, _machine.State);
        // Dictate, two words, nothing to fix: local cleanup, no LLM.
        Assert.Equal("Hello world.", Assert.Single(_rig.Sink.Delivered).Text);
        Assert.Equal([SoundKind.Start, SoundKind.Stop], _sounds);
        Assert.Empty(_notes.Shown);
        Assert.True(_device.Last.Stopped);
    }

    [Fact]
    public async Task ShortcutTap_TouchesNoDisk_AndSaysNothing()
    {
        Input(HotkeyKind.HoldDown, 0);
        Input(HotkeyKind.OtherKey, 50); // Ctrl+C
        Input(HotkeyKind.HoldUp, 120);
        await Task.Delay(50);

        Assert.Empty(_rig.History.Recent(10));
        Assert.Empty(_rig.Pending.List());
        Assert.Empty(_notes.Shown);
        Assert.Empty(_sounds);
        Assert.True(_device.Last!.Stopped);
    }

    [Fact]
    public void NoKey_IsSilentOnATap_AndAnnouncedOnAHold()
    {
        _rig.Stt.Readiness = SttReadiness.MissingKey;
        Input(HotkeyKind.HoldDown, 0);
        Input(HotkeyKind.HoldUp, 100);
        Assert.Empty(_notes.Shown);

        Input(HotkeyKind.HoldDown, 1000);
        Input(HotkeyKind.Tick, 1300);
        Assert.Equal(NoticeKind.NoKey, Assert.Single(_notes.Shown).Kind);
        // The machine was released at once, so the hotkey isn't stuck.
        Assert.Equal(HoldState.Idle, _machine.State);
        Assert.Equal(SoundKind.Error, Assert.Single(_sounds));
    }

    [Fact]
    public void MicDenied_AnnouncedAt300ms()
    {
        _device.Fail = CaptureError.AccessDenied;
        Input(HotkeyKind.HoldDown, 0);
        Assert.Empty(_notes.Shown);
        Input(HotkeyKind.Tick, 300);
        Assert.Equal(NoticeKind.MicDenied, Assert.Single(_notes.Shown).Kind);
        Assert.Equal(HoldState.Idle, _machine.State);
    }

    [Fact]
    public async Task PasswordFieldAtKeyDown_NothingRecordedOrUploaded()
    {
        _targets.Now = Word with { IsPassword = true };
        Input(HotkeyKind.HoldDown, 0);
        _device.Last!.Speak();
        Input(HotkeyKind.Tick, 300);
        await WaitIdle();
        // As below: the machine is freed just before the notice is shown.
        for (var i = 0; i < 200 && !Saw(NoticeKind.PasswordRefused); i++) await Task.Delay(10);

        Assert.Equal(NoticeKind.PasswordRefused, Assert.Single(_notes.Shown).Kind);
        Assert.Equal(0, _rig.Stt.Completions);
        Assert.Empty(_rig.History.Recent(10));
        Assert.Empty(_rig.Pending.List());
        Assert.Equal(HoldState.Idle, _machine.State);
    }

    [Fact]
    public async Task PasswordFoundOnlyAfterRelease_StillFreesTheMachine()
    {
        // A slow UIA read: the user lets go before the password field is detected.
        _targets.Gate = new ManualResetEventSlim(false);
        _targets.Now = Word with { IsPassword = true };
        Input(HotkeyKind.HoldDown, 0);
        _device.Last!.Speak();
        Input(HotkeyKind.Tick, 300);
        Input(HotkeyKind.HoldUp, 1000);
        Assert.Equal(HoldState.Processing, _machine.State);
        _targets.Gate.Set();
        await WaitIdle();

        Assert.Equal(HoldState.Idle, _machine.State);
        Assert.Equal(0, _rig.Stt.Completions);
        // The machine is freed just before the notice is shown, so idle alone doesn't mean it's there yet.
        for (var i = 0; i < 200 && !Saw(NoticeKind.PasswordRefused); i++) await Task.Delay(10);
        Assert.True(Saw(NoticeKind.PasswordRefused));
    }

    private bool Saw(NoticeKind kind)
    {
        lock (_notes.Shown) return _notes.Shown.Exists(n => n.Kind == kind);
    }

    [Fact]
    public void DeviceThrowsUnexpectedly_TreatedAsAMicProblem_NotARun()
    {
        _device.Unexpected = new UnauthorizedAccessException();
        Input(HotkeyKind.HoldDown, 0);
        Input(HotkeyKind.Tick, 300);
        Assert.Equal(NoticeKind.MicDenied, Assert.Single(_notes.Shown).Kind);
        Assert.Equal(HoldState.Idle, _machine.State);
        Assert.Empty(_rig.History.Recent(10));
    }

    [Fact]
    public async Task DiscardAfter300_SaysWhy_AndKeepsNothing()
    {
        Input(HotkeyKind.HoldDown, 0);
        _device.Last!.Speak();
        Input(HotkeyKind.Tick, 300);
        Input(HotkeyKind.Mouse, 900);
        await Task.Delay(100);

        Assert.Equal(NoticeKind.Discarded, Assert.Single(_notes.Shown).Kind);
        Assert.Empty(_rig.Sink.Delivered);
        Assert.Empty(_rig.Pending.List());
    }

    [Fact]
    public async Task TapDuringProcessing_Cancels_WithNotice()
    {
        _rig.Stt.WaitForCancel = true;
        _rig.Stt.Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Input(HotkeyKind.HoldDown, 0);
        _device.Last!.Speak();
        Input(HotkeyKind.Tick, 300);
        Input(HotkeyKind.HoldUp, 1000);
        await _rig.Stt.Entered.Task;

        Input(HotkeyKind.HoldDown, 2000);
        Input(HotkeyKind.HoldUp, 2100);
        await WaitIdle();

        Assert.Contains(_notes.Shown, n => n.Kind == NoticeKind.Cancelled);
        Assert.Empty(_rig.Sink.Delivered);
        Assert.Equal(HoldState.Idle, _machine.State);
    }

    [Fact]
    public async Task HoldDuringProcessing_SaysBusy_WithTheRunningMode()
    {
        _rig.Stt.WaitForCancel = true;
        _rig.Stt.Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Input(HotkeyKind.HoldDown, 0);
        _device.Last!.Speak();
        Input(HotkeyKind.Tick, 300);
        Input(HotkeyKind.HoldUp, 1000);
        await _rig.Stt.Entered.Task;

        Input(HotkeyKind.HoldDown, 2000);
        Input(HotkeyKind.Tick, 2300);
        var busy = Assert.Single(_notes.Shown);
        Assert.Equal((NoticeKind.Busy, Mode.Dictate), (busy.Kind, busy.Mode));
        Input(HotkeyKind.HoldUp, 3000);
        _c.Handle(new CancelProcessing());
        await WaitIdle();
    }

    [Fact]
    public async Task StaleCompletion_DoesNotDisturbANewerHold()
    {
        _rig.Stt.WaitForCancel = true;
        _rig.Stt.Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Input(HotkeyKind.HoldDown, 0);
        _device.Last!.Speak();
        Input(HotkeyKind.Tick, 300);
        Input(HotkeyKind.Esc, 500); // discards; the run is cancelled and unwinds in the background
        Input(HotkeyKind.HoldUp, 600);

        Input(HotkeyKind.HoldDown, 700);
        await Task.Delay(100);
        Assert.Equal(HoldState.Pressed, _machine.State);
    }

    [Fact]
    public async Task Retry_WithNothingToRetry_SaysSo()
    {
        await _c.RetryLastAsync(default);
        Assert.Equal(NoticeKind.RetryUnavailable, Assert.Single(_notes.Shown).Kind);
    }

    [Fact]
    public async Task StartSnapshot_IsAvailableToTheOutputStep()
    {
        TargetSnapshot? seen = null;
        _rig.Sink.OnDeliver = item => seen = _c.StartSnapshot(item.RunId);
        Input(HotkeyKind.HoldDown, 0);
        _device.Last!.Speak();
        Input(HotkeyKind.Tick, 300);
        Input(HotkeyKind.HoldUp, 1000);
        await WaitIdle();
        Assert.Equal(Word, seen);
    }

    private sealed class FakeDevice : ICaptureDevice
    {
        public CaptureError? Fail;
        public Exception? Unexpected;
        public FakeLive? Last;
        public bool SlowStarts;

        public ILiveCapture Start()
        {
            if (Unexpected is { } u) throw u;
            if (Fail is { } f) throw new AudioCaptureException(f);
            return Last = new FakeLive { SlowStart = SlowStarts };
        }
    }

    private sealed class FakeLive : ILiveCapture
    {
        private readonly Channel<AudioChunk> _chunks = Channel.CreateUnbounded<AudioChunk>();
        public bool Stopped;
        public TaskCompletionSource FirstAudioSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SlowStart;

        public Task FirstAudio => SlowStart ? FirstAudioSource.Task : Task.CompletedTask;

        public long? FirstAudioAt { get; set; }

        public MicKind Kind => MicKind.Usb;

        public bool Dictated { get; private set; }

        public void Dictating() => Dictated = true;

        public void Speak()
        {
            var pcm = WavFile.ReadPcm(Fixtures.Audio("speech-short.wav"));
            for (var o = 0; o < pcm.Length; o += 3200)
            {
                var c = pcm.AsMemory(o, Math.Min(3200, pcm.Length - o));
                _chunks.Writer.TryWrite(new AudioChunk(c, Rms.Of(c.Span)));
            }
        }

        public void Stop()
        {
            Stopped = true;
            _chunks.Writer.TryComplete();
        }

        public async IAsyncEnumerable<AudioChunk> ReadAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var c in _chunks.Reader.ReadAllAsync(ct)) yield return c;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeTargets : ITargetReader
    {
        public TargetSnapshot Now = Word;
        public ManualResetEventSlim? Gate;

        public TargetSnapshot Snapshot()
        {
            Gate?.Wait(5000);
            return Now;
        }
        public nint Foreground() => Now.Hwnd;
        public Task WaitForIdleAsync(nint hwnd, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Notes : INoticeSink
    {
        public List<Notice> Shown { get; } = [];
        public void Show(Notice notice)
        {
            lock (Shown) Shown.Add(notice);
        }
    }

    private sealed class Sounds(List<SoundKind> played, List<Mode> cues) : ISounds
    {
        public void Play(SoundKind sound)
        {
            lock (played) played.Add(sound);
        }

        public void PlayModeCue(Mode mode)
        {
            lock (cues) cues.Add(mode);
        }
    }

    private sealed class FakeContext : IContextReader
    {
        public int Calls;
        public CapturedContext? Result = new("Lunch Friday?", ContextSource.Selection, 0);
        public Exception? Throw;

        public Task<CapturedContext?> ReadAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Throw is { } ex ? Task.FromException<CapturedContext?>(ex) : Task.FromResult(Result);
        }
    }

    private FakeRewriter Rewriting()
    {
        var fake = new FakeRewriter(_ => ["Sure."]);
        _rig.Rewriter = fake;
        _c = Rebuild(); // the rig captures the rewriter when the pipeline is built
        return fake;
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    private CaptureCoordinator WithPasteLast(LastResult last) =>
        new(_rig.Build(), _device, _targets, _rig.Stt, _rig.History, _notes, new Sounds(_sounds, _cues),
            act => act(_machine), _rig.Time, _rig.Log, _context, () => _rig.Settings, lastResult: last, output: _rig.Sink);

    [Fact]
    public async Task PasteLast_PastesTheLastResultAgain_IntoTheWindowFocusedNow()
    {
        _c = WithPasteLast(new LastResult(_rig.Time, () => false));
        Input(HotkeyKind.HoldDown, 0);
        _device.Last!.Speak();
        Input(HotkeyKind.Tick, 300);
        Input(HotkeyKind.HoldUp, 1500);
        await WaitIdle();
        await Until(() => _rig.Sink.Delivered.Count == 1);

        var chrome = Word with { Hwnd = 200, ProcessName = "chrome" };
        _targets.Now = chrome;
        TargetSnapshot? seenStart = null;
        _rig.Sink.OnDeliver = item => seenStart = _c.StartSnapshot(item.RunId);
        await _c.PasteLastAsync(default);

        Assert.Equal(2, _rig.Sink.Delivered.Count);
        var again = _rig.Sink.Delivered[1];
        Assert.Equal("Hello world.", again.Text);
        Assert.Equal(DeliveryIntent.Paste, again.Intent);
        Assert.NotEqual(_rig.Sink.Delivered[0].RunId, again.RunId);
        Assert.Equal(chrome, seenStart); // the planner sees the target as it is now, not the original one
        Assert.Null(_c.StartSnapshot(again.RunId));
    }

    [Fact]
    public async Task PasteLast_WithNothingKept_SaysSo()
    {
        _c = WithPasteLast(new LastResult(_rig.Time, () => false));
        await _c.PasteLastAsync(default);
        Assert.Empty(_rig.Sink.Delivered);
        Assert.Equal(NoticeKind.PasteLastUnavailable, Assert.Single(_notes.Shown).Kind);
    }

    [Fact]
    public async Task PasteLast_WhileRecording_IsBusy()
    {
        var last = new LastResult(_rig.Time, () => false);
        last.Record(new RunResult { RunId = Guid.NewGuid(), Outcome = RunOutcome.Completed, Mode = Mode.Dictate, Output = "x", Delivery = Yolime.Core.History.DeliveryKind.Pasted });
        _c = WithPasteLast(last);
        Input(HotkeyKind.HoldDown, 0);
        await _c.PasteLastAsync(default);
        Assert.Empty(_rig.Sink.Delivered);
        Assert.Contains(_notes.Shown, n => n.Kind == NoticeKind.Busy);
        Input(HotkeyKind.HoldUp, 50);
    }

    [Fact]
    public async Task ReRun_IsKeptAsTheLastResult_AndAnnounced()
    {
        var last = new LastResult(_rig.Time, () => false);
        _c = WithPasteLast(last);
        var changed = 0;
        _c.HistoryChanged += () => Interlocked.Increment(ref changed);
        var source = _rig.History.Insert(new Yolime.Core.History.HistoryRow
        {
            RunId = Guid.NewGuid(), StartedUtc = _rig.Time.GetUtcNow(), Mode = Mode.Dictate, RawText = "kept words",
            Output = "Kept words.", Outcome = RunOutcome.Completed,
        });

        var result = await _c.RerunAsync(source, Mode.Raw, default);

        Assert.Equal(RunOutcome.Completed, result!.Outcome);
        Assert.True(last.TryGet(out var text, out var mode));
        Assert.Equal(("kept words", Mode.Raw), (text, mode));
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task AFinishedHold_AnnouncesAHistoryChange()
    {
        var changed = 0;
        _c.HistoryChanged += () => Interlocked.Increment(ref changed);
        Input(HotkeyKind.HoldDown, 0);
        _device.Last!.Speak();
        Input(HotkeyKind.Tick, 300);
        Input(HotkeyKind.HoldUp, 1500);
        await WaitIdle();
        await Until(() => Volatile.Read(ref changed) == 1);
    }

    [Fact]
    public async Task AThrowingHistorySubscriber_DoesntTouchTheRun()
    {
        // A window reading its dispatcher from the wrong thread threw here once, and the run was "completed" twice.
        var inputs = new List<Yolime.Core.Overlay.OverlayInput>();
        _c = new CaptureCoordinator(_rig.Build(), _device, _targets, _rig.Stt, _rig.History, _notes, new Sounds(_sounds, _cues),
            act => act(_machine), _rig.Time, _rig.Log, _context, () => _rig.Settings, overlay: i => { lock (inputs) inputs.Add(i); });
        var finished = 0;
        _c.RunFinished += _ => Interlocked.Increment(ref finished);
        _c.HistoryChanged += () => throw new InvalidOperationException("subscriber bug");
        Input(HotkeyKind.HoldDown, 0);
        _device.Last!.Speak();
        Input(HotkeyKind.Tick, 300);
        Input(HotkeyKind.HoldUp, 1500);
        await WaitIdle();
        await Until(() => _rig.Sink.Delivered.Count == 1);
        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref finished));
        Assert.Contains(Yolime.Core.Logging.LogEvent.UnhandledException, _rig.Log.Events);
        // The notch keeps the finished card: no stray Dismissed from a second "completion".
        lock (inputs) Assert.IsType<Yolime.Core.Overlay.RunFinished>(inputs.Last(i => i is Yolime.Core.Overlay.RunFinished or Yolime.Core.Overlay.Dismissed));
    }

    [Fact]
    public async Task PasteText_ForAnExpectedWindow_DoesNothingIfAnotherIsInFront()
    {
        // History's Re-insert: a popup that took the foreground during the settle must not receive the text.
        _c = WithPasteLast(new LastResult(_rig.Time, () => false));
        var delivery = await _c.PasteTextAsync("from history", Mode.Dictate, default, expectHwnd: _targets.Now.Hwnd + 1);
        Assert.Equal(Yolime.Core.History.DeliveryKind.None, delivery);
        Assert.Empty(_rig.Sink.Delivered);
    }

    [Fact]
    public async Task PasteText_DeliversThroughTheExecutor_WithTheWindowInFrontNow()
    {
        _c = WithPasteLast(new LastResult(_rig.Time, () => false));
        TargetSnapshot? seen = null;
        _rig.Sink.OnDeliver = item => seen = _c.StartSnapshot(item.RunId);
        var delivery = await _c.PasteTextAsync("from history", Mode.Dictate, default);
        Assert.Equal(Yolime.Core.History.DeliveryKind.Pasted, delivery);
        Assert.Equal("from history", Assert.Single(_rig.Sink.Delivered).Text);
        Assert.Equal(_targets.Now, seen);
    }

    [Fact]
    public async Task TheStartCue_WaitsForTheMicToDeliverAudio()
    {
        // A Bluetooth headset can take a second to switch profiles: beeping before audio flows clips the first words of
        // anyone who waits for the beep.
        _device.SlowStarts = true;
        Input(HotkeyKind.HoldDown, 0);
        Input(HotkeyKind.Tick, 300);
        await Task.Delay(100);
        Assert.DoesNotContain(SoundKind.Start, _sounds);
        _device.Last!.FirstAudioSource.SetResult();
        await Until(() => _sounds.Contains(SoundKind.Start));
        Input(HotkeyKind.HoldUp, 1500);
        await WaitIdle();
    }

    [Fact]
    public async Task TheTimingLog_RecordsKeyDownToFirstAudio_AndTheMicKind()
    {
        // What decides whether a warm mic is worth it: how long each kind of mic takes to start, measured per hold.
        Input(HotkeyKind.HoldDown, 0);
        _rig.Time.Advance(TimeSpan.FromMilliseconds(150));
        _device.Last!.FirstAudioAt = _rig.Time.GetTimestamp();
        _device.Last.Speak();
        Input(HotkeyKind.Tick, 300);
        Input(HotkeyKind.HoldUp, 1000);
        await WaitIdle();
        var timing = _rig.Timings.Last();
        Assert.Equal(150, timing.MicFirstAudioMs);
        Assert.Equal(MicKind.Usb, timing.MicKind);
    }

    [Fact]
    public async Task AHoldPastTheTapWindow_TellsTheMicItsADictation_ATapDoesNot()
    {
        // A kept-ready mic stays open only after real dictations, never after a Right-Ctrl shortcut or a tap.
        Input(HotkeyKind.HoldDown, 0);
        var tap = _device.Last!;
        Input(HotkeyKind.HoldUp, 120);
        Assert.False(tap.Dictated);
        Input(HotkeyKind.HoldDown, 1000);
        Input(HotkeyKind.Tick, 1300);
        Assert.True(_device.Last!.Dictated);
        Input(HotkeyKind.HoldUp, 1500);
        await WaitIdle();
    }

    [Fact]
    public async Task AHoldWhoseMicNeverDelivered_RecordsNoFirstAudioTime()
    {
        Input(HotkeyKind.HoldDown, 0);
        Input(HotkeyKind.Tick, 300);
        Input(HotkeyKind.HoldUp, 1000);
        await WaitIdle();
        Assert.Null(_rig.Timings.Last().MicFirstAudioMs);
    }

    [Fact]
    public async Task TheStartCue_PlaysAnywayAfterTwoSeconds_WhenTheMicIsSilent()
    {
        _device.SlowStarts = true;
        Input(HotkeyKind.HoldDown, 0);
        Input(HotkeyKind.Tick, 300);
        await Task.Delay(100);
        Assert.DoesNotContain(SoundKind.Start, _sounds);
        _rig.Time.Advance(TimeSpan.FromSeconds(2));
        // Well inside two real seconds: the cap runs on the injected clock, not the wall clock.
        for (var i = 0; i < 50 && !_sounds.Contains(SoundKind.Start); i++) await Task.Delay(10);
        Assert.Contains(SoundKind.Start, _sounds);
        Input(HotkeyKind.HoldUp, 2500);
        await WaitIdle();
    }

    private CaptureCoordinator Rebuild()
    {
        return new CaptureCoordinator(_rig.Build(), _device, _targets, _rig.Stt, _rig.History, _notes, new Sounds(_sounds, _cues),
            act => act(_machine), _rig.Time, _rig.Log, _context, () => _rig.Settings);
    }

    [Fact]
    public async Task RetryLast_OnAFailedPromptRewrite_RewritesTheTranscript_WithoutStt()
    {
        _rig.Rewriter = new FakeRewriter(_ => [], RewriteFailure.Server);
        _c = Rebuild();
        Input(HotkeyKind.HoldDown, 0, start: Mode.Prompt);
        _device.Last!.Speak();
        Input(HotkeyKind.Tick, 300, start: Mode.Prompt);
        Input(HotkeyKind.HoldUp, 1500, start: Mode.Prompt);
        await WaitIdle();
        var sttCalls = _rig.Stt.Completions;

        var fake = Rewriting();
        await _c.RetryLastAsync(default);

        Assert.Equal(sttCalls, _rig.Stt.Completions);
        Assert.Single(fake.Requests);
        Assert.StartsWith("Sure.", _rig.Sink.Delivered[^1].Text); // plus the selection Prompt appends
    }

    [Fact]
    public async Task ReplyHold_ReadsTheContextOnce_AndTheRewriteGetsIt()
    {
        var fake = Rewriting();
        Input(HotkeyKind.HoldDown, 0, start: Mode.Reply);
        _device.Last!.Speak();
        Input(HotkeyKind.Tick, 300, start: Mode.Reply);
        Input(HotkeyKind.HoldUp, 1500, start: Mode.Reply);
        await WaitIdle();

        Assert.Equal(1, _context.Calls);
        Assert.Equal("Lunch Friday?", Assert.Single(fake.Requests).Context!.Text);
    }

    [Fact]
    public async Task DictateHold_NeverReadsContext()
    {
        Input(HotkeyKind.HoldDown, 0);
        _device.Last!.Speak();
        Input(HotkeyKind.Tick, 300);
        Input(HotkeyKind.HoldUp, 1500);
        await WaitIdle();
        Assert.Equal(0, _context.Calls);
    }

    [Fact]
    public async Task CyclingIntoPrompt_ReadsContextOnce_AndEachCycleCues()
    {
        _rig.Stt.Text = PipelineRig.LongTranscript;
        var fake = Rewriting();
        Input(HotkeyKind.HoldDown, 0);
        _device.Last!.Speak();
        Input(HotkeyKind.Tick, 300);
        Assert.Equal(0, _context.Calls);
        Input(HotkeyKind.ShiftDown, 500);
        Input(HotkeyKind.ShiftUp, 520, down: false);
        await Until(() => _context.Calls == 1); // Dictate → Prompt (the read runs off the hook thread)
        Input(HotkeyKind.ShiftDown, 700);
        Input(HotkeyKind.ShiftUp, 720, down: false);
        await Task.Delay(50);
        Assert.Equal(1, _context.Calls); // Prompt → Reply: already read
        Input(HotkeyKind.HoldUp, 1500);
        await WaitIdle();

        Assert.Equal([Mode.Prompt, Mode.Reply], _cues);
        var sent = Assert.Single(fake.Requests);
        Assert.Equal(Mode.Reply, sent.Mode);
        Assert.Equal("Lunch Friday?", sent.Context!.Text);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Cues_NeedBothSoundsAndCueSettings(bool sounds, bool cues)
    {
        _rig.Settings = _rig.Settings with { Sounds = sounds, ModeCycleCues = cues };
        Input(HotkeyKind.HoldDown, 0);
        Input(HotkeyKind.Tick, 300);
        Input(HotkeyKind.ShiftDown, 500);
        Input(HotkeyKind.ShiftUp, 550, down: false);
        Assert.Empty(_cues);
    }

    [Fact]
    public void ACue_PlaysWhenBothAreOn()
    {
        // The other half of the cue-settings test: the same taps do cue, so its empty list means something.
        Input(HotkeyKind.HoldDown, 0);
        Input(HotkeyKind.Tick, 300);
        Input(HotkeyKind.ShiftDown, 500);
        Input(HotkeyKind.ShiftUp, 550, down: false);
        Assert.Equal([Mode.Prompt], _cues);
    }

    [Fact]
    public async Task ShiftHeldAtRelease_PreviewsInTheStartingMode()
    {
        _rig.Stt.Text = PipelineRig.LongTranscript;
        Input(HotkeyKind.HoldDown, 0);
        _device.Last!.Speak();
        Input(HotkeyKind.Tick, 300);
        Input(HotkeyKind.ShiftDown, 500);
        Input(HotkeyKind.HoldUp, 900);
        Input(HotkeyKind.ShiftUp, 950, down: false);
        await WaitIdle();
        Assert.Empty(_cues);
        var run = Assert.Single(_rig.History.All());
        Assert.Equal(Mode.Dictate, run.Mode);
    }

    [Fact]
    public async Task AFailingContextRead_IsNoContext_NotAFailedRun()
    {
        _context.Throw = new InvalidOperationException("UIA died");
        var fake = Rewriting();
        Input(HotkeyKind.HoldDown, 0, start: Mode.Reply);
        _device.Last!.Speak();
        Input(HotkeyKind.Tick, 300, start: Mode.Reply);
        Input(HotkeyKind.HoldUp, 1500, start: Mode.Reply);
        await WaitIdle();

        Assert.Null(Assert.Single(fake.Requests).Context);
        Assert.Equal("Sure.", Assert.Single(_rig.Sink.Delivered).Text);
    }

    [Fact]
    public async Task TheTargetsProfile_ReachesTheRewrite()
    {
        _rig.Stt.Text = PipelineRig.LongTranscript;
        _targets.Now = Word with { ProcessName = "Code", WindowClass = "Chrome_WidgetWin_1" };
        var fake = Rewriting();
        Input(HotkeyKind.HoldDown, 0);
        _device.Last!.Speak();
        Input(HotkeyKind.Tick, 300);
        Input(HotkeyKind.HoldUp, 1500);
        await WaitIdle();
        Assert.Equal(Profile.Code, Assert.Single(fake.Requests).Profile);
    }
}
