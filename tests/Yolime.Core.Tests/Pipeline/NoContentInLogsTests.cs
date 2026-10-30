using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Yolime.Core.Audio;
using Yolime.Core.History;
using Yolime.Core.Logging;
using Yolime.Core.Net;
using Yolime.Core.Pipeline;
using Yolime.Core.Settings;
using Yolime.Core.Stt;
using Yolime.Core.Tests.Stt;

namespace Yolime.Core.Tests.Pipeline;

/// <summary>
/// The logging rule, end to end: real logger, timing log, STT session, history and pending store, with every piece
/// of content set to a SENTINEL string, driven through every outcome. Nothing written outside history and pending
/// audio may contain SENTINEL.
/// </summary>
public sealed class NoContentInLogsTests : IDisposable
{
    private readonly TempRoot _root = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _root.Dispose();
    }

    private sealed class Keys : IApiKeySource
    {
        public string? Get(string name) => name == SttEndpoint.CustomKeyName ? "SENTINEL_KEY" : null;
    }

    private enum Reply { Ok, Unauthorized, Quota, Malformed, NetworkError, Hang }

    [Fact]
    public async Task Sentinels_NeverReachLogsOrTimings()
    {
        var reply = Reply.Ok;
        TaskCompletionSource? hanging = null;
        var fake = new FakeHttpHandler(_ => reply switch
        {
            Reply.Ok => Json(HttpStatusCode.OK, """{"text":"SENTINEL_TRANSCRIPT words"}"""),
            Reply.Unauthorized => Json(HttpStatusCode.Unauthorized, """{"error":{"message":"SENTINEL_BODY"}}"""),
            Reply.Quota => Json(HttpStatusCode.TooManyRequests, """{"error":{"code":"insufficient_quota","message":"SENTINEL_BODY"}}"""),
            Reply.Malformed => Json(HttpStatusCode.OK, "SENTINEL_BODY not json"),
            Reply.NetworkError => throw new HttpRequestException("SENTINEL_EXCEPTION at https://sentinel-host.example/"),
            _ => throw new InvalidOperationException(),
        });
        var handler = new HangingHandler(fake, () => reply == Reply.Hang, () => hanging?.TrySetResult());

        var settings = new YolimeSettings
        {
            SttProvider = SttProviderKind.Custom,
            CustomSttBaseUrl = "https://sentinel-host.example/v1",
            CustomSttModel = "SENTINEL_MODEL",
            Vocabulary = ["SENTINEL_VOCAB"],
        };
        var log = new FileLog(_root.Paths, _time);
        var pending = new PendingAudioStore(_root.Paths, log, _time);
        using var history = new HistoryStore(_root.Paths, log, _time, pending);
        var stt = new BatchTranscriptionSessionFactory(() => settings, new Keys(), HttpClients.Create(handler), log);
        var rewriter = new FakeRewriter(_ => ["SENTINEL_OUTPUT"]);
        var sink = new FakeSink();
        var pipeline = new DictationPipeline(stt, rewriter, sink, history, pending, log, new JsonlTimingLog(_root.Paths, _time), () => settings, _time);

        RunRequest Request(IAudioSource? audio = null, Mode mode = Mode.Prompt) =>
            new(audio ?? PipelineRig.Speech(), mode, "SENTINEL_PROC", Task.FromResult(new RunIntent(mode, false)), Context: Task.FromResult<CapturedContext?>(new CapturedContext("SENTINEL_CTX", ContextSource.Selection, 0)));

        Assert.Equal(RunOutcome.Completed, (await pipeline.RunAsync(Request(), CancellationToken.None)).Outcome);
        Assert.Equal(RunOutcome.Completed, (await pipeline.RunAsync(Request(mode: Mode.Raw), CancellationToken.None)).Outcome);

        reply = Reply.Unauthorized;
        var failed = await pipeline.RunAsync(Request(), CancellationToken.None);
        Assert.Equal(SttError.Auth, failed.SttError);
        reply = Reply.Quota;
        Assert.Equal(SttError.QuotaExceeded, (await pipeline.RunAsync(Request(), CancellationToken.None)).SttError);
        reply = Reply.Malformed;
        Assert.Equal(SttError.MalformedResponse, (await pipeline.RunAsync(Request(), CancellationToken.None)).SttError);
        reply = Reply.NetworkError;
        Assert.Equal(SttError.Network, (await pipeline.RunAsync(Request(), CancellationToken.None)).SttError);

        // Timeout, with a Busy rejection while it hangs.
        reply = Reply.Hang;
        hanging = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = pipeline.RunAsync(Request(), CancellationToken.None);
        await hanging.Task;
        Assert.Equal(RunOutcome.Busy, (await pipeline.RunAsync(Request(), CancellationToken.None)).Outcome);
        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(SttError.Timeout, (await slow).SttError);

        // Cancel mid-STT.
        hanging = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var cts = new CancellationTokenSource())
        {
            var cancelled = pipeline.RunAsync(Request(), cts.Token);
            await hanging.Task;
            cts.Cancel();
            Assert.Equal(RunOutcome.Cancelled, (await cancelled).Outcome);
        }

        reply = Reply.Ok;
        Assert.Equal(RunOutcome.NoSpeech, (await pipeline.RunAsync(Request(new FileAudioSource(Fixtures.Audio("noise-floor-5s.wav"))), CancellationToken.None)).Outcome);
        Assert.Equal(RunOutcome.Completed, (await pipeline.RetryAsync(failed.HistoryId!.Value, null, null, CancellationToken.None)).Outcome);

        var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(_root.Dir, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("history.db", StringComparison.Ordinal) || file.Contains($"{Path.DirectorySeparatorChar}pending{Path.DirectorySeparatorChar}")) continue;
            var text = Encoding.UTF8.GetString(File.ReadAllBytes(file));
            Assert.False(text.Contains("SENTINEL", StringComparison.OrdinalIgnoreCase), $"content leaked into {name}");
            scanned++;
        }
        Assert.True(scanned >= 2, "expected both a log file and a timings file");
        Assert.NotEmpty(File.ReadAllText(Directory.GetFiles(_root.Paths.LogsDir, "yolime-*.log").Single()));
        Assert.Equal(10, File.ReadAllLines(Directory.GetFiles(_root.Paths.LogsDir, "timings-*.jsonl").Single()).Length);
    }

    [Fact]
    public async Task Sentinels_NeverReachLogsOrTimings_WhenStreaming()
    {
        // The streamed upload's own paths: streamed, a status that falls back, a drop that falls back, an error it reports.
        var fake = new StreamingFakeHandler();
        var settings = new YolimeSettings { StreamingUpload = true, OpenAiSttModel = "SENTINEL_MODEL", Vocabulary = ["SENTINEL_VOCAB"] };
        var log = new FileLog(_root.Paths, _time);
        var pending = new PendingAudioStore(_root.Paths, log, _time);
        using var history = new HistoryStore(_root.Paths, log, _time, pending);
        var stt = new BatchTranscriptionSessionFactory(() => settings, new OpenAiKeys(), HttpClients.Create(fake), log, _time);
        var pipeline = new DictationPipeline(stt, new FakeRewriter(_ => ["SENTINEL_OUTPUT"]), new FakeSink(), history, pending, log, new JsonlTimingLog(_root.Paths, _time), () => settings, _time);
        RunRequest Request() => new(PipelineRig.Speech(), Mode.Raw, "SENTINEL_PROC", Task.FromResult(new RunIntent(Mode.Raw, false)));

        fake.Respond = _ => Json(HttpStatusCode.OK, """{"text":"SENTINEL_TRANSCRIPT words"}""");
        Assert.Equal(RunOutcome.Completed, (await pipeline.RunAsync(Request(), CancellationToken.None)).Outcome);
        var streamed = fake.Count;
        Assert.True(fake.Request(0).Chunked);

        fake.Respond = i => i == streamed ? Json(HttpStatusCode.BadRequest, """{"error":{"message":"SENTINEL_BODY"}}""") : Json(HttpStatusCode.OK, """{"text":"SENTINEL_TRANSCRIPT"}""");
        Assert.Equal(RunOutcome.Completed, (await pipeline.RunAsync(Request(), CancellationToken.None)).Outcome);

        var dropAt = fake.Count;
        fake.DropAfterBytes = i => i == dropAt ? 4000 : null;
        Assert.Equal(RunOutcome.Completed, (await pipeline.RunAsync(Request(), CancellationToken.None)).Outcome);
        fake.DropAfterBytes = null;

        fake.Respond = _ => Json(HttpStatusCode.Unauthorized, """{"error":{"message":"SENTINEL_BODY"}}""");
        Assert.Equal(SttError.Auth, (await pipeline.RunAsync(Request(), CancellationToken.None)).SttError);

        foreach (var file in Directory.EnumerateFiles(_root.Dir, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("history.db", StringComparison.Ordinal) || file.Contains($"{Path.DirectorySeparatorChar}pending{Path.DirectorySeparatorChar}")) continue;
            Assert.False(Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains("SENTINEL", StringComparison.OrdinalIgnoreCase), $"content leaked into {name}");
        }
        var logText = File.ReadAllText(Directory.GetFiles(_root.Paths.LogsDir, "yolime-*.log").Single());
        Assert.Contains("StreamFellBack", logText);
        Assert.Contains("\"SttUpload\":\"StreamFellBack\"", File.ReadAllText(Directory.GetFiles(_root.Paths.LogsDir, "timings-*.jsonl").Single()));
    }

    private sealed class OpenAiKeys : IApiKeySource
    {
        public string? Get(string name) => name == SttEndpoint.OpenAiKeyName ? "SENTINEL_KEY" : null;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Passes through to <paramref name="inner"/> unless told to hang until the request is cancelled.</summary>
    private sealed class HangingHandler(HttpMessageHandler inner, Func<bool> hang, Action entered) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!hang()) return await base.SendAsync(request, ct);
            entered();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }
    }
}
