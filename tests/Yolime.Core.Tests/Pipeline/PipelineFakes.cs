using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Yolime.Core.Audio;
using Yolime.Core.History;
using Yolime.Core.Llm;
using Yolime.Core.Logging;
using Yolime.Core.Output;
using Yolime.Core.Pipeline;
using Yolime.Core.Settings;
using Yolime.Core.Stt;

namespace Yolime.Core.Tests.Pipeline;

public sealed class FakeStt : ITranscriptionSessionFactory
{
    public string Text { get; set; } = "hello world";
    public SttException? Throw { get; set; }
    public bool WaitForCancel { get; set; }
    public TaskCompletionSource? Entered { get; set; }
    public SttReadiness Readiness { get; set; } = SttReadiness.Ready;
    public int Completions { get; private set; }
    public long PushedBytes { get; private set; }
    public SttUpload Upload { get; set; } = SttUpload.Batch;
    public List<SttSessionOptions> Created { get; } = [];

    /// <summary>Sessions disposed without CompleteAsync: a streamed one would have been torn down, never sent whole.</summary>
    public int DisposedUncompleted { get; private set; }

    public SttProviderKind Provider => SttProviderKind.OpenAi;

    public SttReadiness CheckReady() => Readiness;

    public ITranscriptionSession Create(SttSessionOptions options)
    {
        Created.Add(options);
        return new Session(this);
    }

    private sealed class Session(FakeStt owner) : ITranscriptionSession
    {
        private bool _completed;

        public void PushAudio(ReadOnlyMemory<byte> pcm) => owner.PushedBytes += pcm.Length;

        public async Task<SttResult> CompleteAsync(CancellationToken ct)
        {
            _completed = true;
            owner.Completions++;
            owner.Entered?.TrySetResult();
            if (owner.WaitForCancel) await Task.Delay(Timeout.Infinite, ct);
            if (owner.Throw is { } ex) throw ex;
            return new SttResult(owner.Text, 5, 10, false, Upload: owner.Upload);
        }

        public ValueTask DisposeAsync()
        {
            if (!_completed) owner.DisposedUncompleted++;
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class FakeRewriter(Func<RewriteRequest, IEnumerable<string>> deltas, RewriteFailure? failAfter = null) : IRewriter
{
    public RewriterReadiness Readiness { get; set; } = RewriterReadiness.Ready;
    public List<RewriteRequest> Requests { get; } = [];

    public RewriterReadiness CheckReady() => Readiness;

    public async IAsyncEnumerable<string> StreamAsync(RewriteRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        lock (Requests) Requests.Add(request);
        foreach (var d in deltas(request))
        {
            await Task.Yield();
            yield return d;
        }
        if (failAfter is { } f) throw new RewriteException(f);
    }
}

public sealed class FakeSink : IOutputSink
{
    public List<OutputItem> Delivered { get; } = [];
    public DeliveryKind Result { get; set; } = DeliveryKind.Pasted;
    public Exception? Throw { get; set; }
    public Action<OutputItem>? OnDeliver { get; set; }

    public Task<DeliveryKind> DeliverAsync(OutputItem item, CancellationToken ct)
    {
        OnDeliver?.Invoke(item);
        if (Throw is { } ex) throw ex;
        Delivered.Add(item);
        return Task.FromResult(Result);
    }
}

public sealed class ThrowingSource(IAudioSource inner, int afterChunks, CaptureError error) : IAudioSource
{
    public async IAsyncEnumerable<AudioChunk> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var n = 0;
        await foreach (var c in inner.ReadAsync(ct))
        {
            if (n++ == afterChunks) throw new AudioCaptureException(error);
            yield return c;
        }
        if (n <= afterChunks) throw new AudioCaptureException(error);
    }
}

public sealed class ListProgress : IProgress<PipelineEvent>
{
    public List<PipelineEvent> Events { get; } = [];

    public void Report(PipelineEvent value)
    {
        lock (Events) Events.Add(value);
    }
}

public sealed class UnavailableHistory : IHistoryStore
{
    public bool IsReadOnly => true;
    public long Insert(HistoryRow row) => throw new HistoryUnavailableException();
    public void Update(long id, Func<HistoryRow, HistoryRow> change) => throw new HistoryUnavailableException();
    public void Delete(long id) => throw new HistoryUnavailableException();
    public HistoryRow? Get(long id) => null;
    public IReadOnlyList<HistoryRow> Recent(int limit) => [];
    public IReadOnlyList<HistoryRow> Page(int limit, HistoryCursor? before, Mode? mode) => [];
    public IReadOnlyList<HistoryRow> All() => [];
    public IReadOnlyList<long> DeleteRun(long id, bool keepReruns = false) => throw new HistoryUnavailableException();
    public int RerunCount(long id) => 0;
    public HistoryRow? LatestRetryable() => null;
    public int MarkInterrupted() => 0;
    public int DeleteRefused() => 0;
    public void MarkAudioExpired(IEnumerable<string> fileNames) { }
    public IReadOnlySet<string> ReferencedAudioFiles() => new HashSet<string>();
    public int PruneOlderThan(TimeSpan age) => 0;
    public void Clear() { }
    public void Checkpoint() { }
}

/// <summary>A pipeline wired to a temp root with real history and pending stores and fake STT/rewriter/sink.</summary>
public sealed class PipelineRig : IDisposable
{
    public TempRoot Root { get; } = new();
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
    public RecordingLog Log { get; } = new();
    public FakeStt Stt { get; } = new();
    public FakeSink Sink { get; } = new();
    public List<RunTiming> Timings { get; } = [];
    public PendingAudioStore Pending { get; }
    public HistoryStore History { get; }
    public IRewriter? Rewriter { get; set; }
    public YolimeSettings Settings { get; set; } = new();

    public PipelineRig()
    {
        Pending = new PendingAudioStore(Root.Paths, Log, Time);
        History = new HistoryStore(Root.Paths, Log, Time, Pending);
    }

    public DictationPipeline Build(IHistoryStore? history = null, ITranscriptionSessionFactory? stt = null) =>
        new(stt ?? Stt, Rewriter, Sink, history ?? History, Pending, Log, new ListTimingLog(Timings), () => Settings, Time);

    public static RunRequest Request(IAudioSource audio, Mode mode = Mode.Raw, string? context = null, IProgress<PipelineEvent>? progress = null) =>
        new(audio, mode, "slack", Task.FromResult(new RunIntent(mode, false)), progress,
            Context: context is null ? null : Task.FromResult<CapturedContext?>(new CapturedContext(context, ContextSource.Selection, 0)));

    /// <summary>Twelve words or more, so a Dictate run reaches the rewriter instead of local cleanup.</summary>
    public const string LongTranscript = "so I wanted to send a quick note to say the meeting moved to Friday";

    public static IAudioSource Speech() => new FileAudioSource(Fixtures.Audio("speech-short.wav"));

    public void Dispose()
    {
        History.Dispose();
        SqliteConnection.ClearAllPools();
        Root.Dispose();
    }

    private sealed class ListTimingLog(List<RunTiming> list) : ITimingLog
    {
        public void Append(RunTiming timing)
        {
            lock (list) list.Add(timing);
        }
    }
}
