using System.Net;
using System.Security.Cryptography;
using System.Text;
using Yolime.Core.Net;
using Yolime.Core.Tests.Stt;
using Yolime.Core.Updates;

namespace Yolime.Core.Tests.Updates;

public sealed class UpdateClientTests : IDisposable
{
    private static readonly AppVersion Current = new(0, 1, 0, false);
    private static readonly byte[] Setup = Encoding.ASCII.GetBytes("pretend this is yolime-setup-0.2.0.exe");
    private static readonly string SetupHash = Convert.ToHexString(SHA256.HashData(Setup));
    private const string FeedUrl = "https://api.github.com/repos/bloknayrb/yolime/releases/latest";
    private const string SetupUrl = "https://api.github.com/repos/bloknayrb/yolime/releases/assets/101";
    private const string SumsUrl = "https://api.github.com/repos/bloknayrb/yolime/releases/assets/102";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "yolime-update-tests", Guid.NewGuid().ToString("N"));
    private readonly RecordingLog _log = new();
    private AppVersion? _fileVersion = new(0, 2, 0, false);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private static HttpResponseMessage Ok(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };

    private static HttpResponseMessage Ok(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private static HttpResponseMessage Redirect(string location) => new(HttpStatusCode.Found) { Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) } };

    private static string Sums(string hash) => $"{hash.ToLowerInvariant()}  yolime-setup-0.2.0.exe\n";

    private (UpdateClient Client, FakeHttpHandler Handler) Make(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeHttpHandler(respond);
        // The production wrapper, so host pinning is part of what's tested.
        return (new UpdateClient(HttpClients.Create(handler), _log, readFileVersion: _ => _fileVersion), handler);
    }

    /// <summary>The feed, the sums file and the setup, each answered as GitHub does (assets by a redirect).</summary>
    private static HttpResponseMessage GitHub(HttpRequestMessage r, string? sums = null, byte[]? setup = null) => r.RequestUri!.ToString() switch
    {
        FeedUrl => Ok(ReleaseFeedTests.Feed()),
        SumsUrl => Redirect("https://release-assets.githubusercontent.com/sums?sig=SECRET"),
        SetupUrl => Redirect("https://release-assets.githubusercontent.com/setup?sig=SECRET"),
        "https://release-assets.githubusercontent.com/sums?sig=SECRET" => Ok(sums ?? Sums(SetupHash)),
        "https://release-assets.githubusercontent.com/setup?sig=SECRET" => Ok(setup ?? Setup),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    };

    [Fact]
    public async Task Check_Newer_IsAvailable_WithTheSetupsHash()
    {
        var (client, handler) = Make(r => GitHub(r));
        var result = await client.CheckAsync(Current, CancellationToken.None);
        Assert.Equal(CheckOutcome.Available, result.Outcome);
        Assert.Equal(new AppVersion(0, 2, 0, false), result.Release!.Version);
        Assert.Equal(SetupHash, result.SetupHash);
        Assert.All(handler.Requests, r => Assert.StartsWith("yolime/", r.Request.Headers.UserAgent.ToString()));
        // The redirect's request carries nothing from the first one but the user agent.
        var hop = handler.Requests.Single(r => r.Request.RequestUri!.Host == "release-assets.githubusercontent.com").Request;
        Assert.DoesNotContain(hop.Headers, h => h.Key == "Accept");
    }

    [Fact]
    public async Task Check_UpToDate_FetchesNothingMore()
    {
        var (client, handler) = Make(r => GitHub(r));
        var result = await client.CheckAsync(new AppVersion(0, 2, 0, false), CancellationToken.None);
        Assert.Equal(CheckOutcome.UpToDate, result.Outcome);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Check_AnErrorStatus_IsTryLater(HttpStatusCode status)
    {
        var (client, _) = Make(_ => new HttpResponseMessage(status) { Content = new StringContent("SENTINEL body") });
        Assert.Equal(CheckOutcome.TryLater, (await client.CheckAsync(Current, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Check_NoNetwork_IsTryLater()
    {
        var (client, _) = Make(_ => throw new HttpRequestException("SENTINEL https://sentinel.example/"));
        Assert.Equal(CheckOutcome.TryLater, (await client.CheckAsync(Current, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Check_SumsWithoutTheSetupsLine_IsTryLater() =>
        Assert.Equal(CheckOutcome.TryLater, (await Make(r => GitHub(r, sums: "abc  other.exe\n")).Client.CheckAsync(Current, CancellationToken.None)).Outcome);

    [Theory]
    [InlineData("https://evil.example/sums")]                                   // another host
    [InlineData("http://release-assets.githubusercontent.com/sums")]            // not https
    [InlineData("https://release-assets.githubusercontent.com:8443/sums")]       // another port
    [InlineData("https://user@release-assets.githubusercontent.com/sums")]       // user info
    [InlineData("/sums")]                                                       // relative
    public async Task Check_ARedirectAnywhereElse_IsRefused_AndNeverFollowed(string location)
    {
        var (client, handler) = Make(r => r.RequestUri!.ToString() == SumsUrl ? Redirect(location) : GitHub(r));
        Assert.Equal(CheckOutcome.TryLater, (await client.CheckAsync(Current, CancellationToken.None)).Outcome);
        Assert.DoesNotContain(handler.Requests, r => r.Request.RequestUri!.ToString().Contains("sums", StringComparison.Ordinal) && r.Request.RequestUri.ToString() != SumsUrl);
    }

    [Fact]
    public async Task Check_ASecondRedirect_IsRefused()
    {
        var (client, handler) = Make(r => r.RequestUri!.Host == "release-assets.githubusercontent.com"
            ? Redirect("https://objects.githubusercontent.com/again") : GitHub(r));
        Assert.Equal(CheckOutcome.TryLater, (await client.CheckAsync(Current, CancellationToken.None)).Outcome);
        Assert.DoesNotContain(handler.Requests, r => r.Request.RequestUri!.Host == "objects.githubusercontent.com");
    }

    [Fact]
    public async Task Check_AnAssetServedDirectly_IsAccepted()
    {
        var (client, _) = Make(r => r.RequestUri!.ToString() == SumsUrl ? Ok(Sums(SetupHash)) : GitHub(r));
        Assert.Equal(CheckOutcome.Available, (await client.CheckAsync(Current, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Download_Good_IsInPlace_UnderTheNameWeBuilt()
    {
        var (client, _) = Make(r => GitHub(r));
        var check = await client.CheckAsync(Current, CancellationToken.None);
        var result = await client.DownloadAsync(check.Release!, check.SetupHash!, _folder, null, CancellationToken.None);
        Assert.Equal(DownloadOutcome.Ready, result.Outcome);
        Assert.Equal(Path.Combine(_folder, "yolime-setup-0.2.0.exe"), result.Path);
        Assert.Equal(Setup, await File.ReadAllBytesAsync(result.Path!));
    }

    [Fact]
    public async Task Download_ChecksumMismatch_LeavesNothing()
    {
        var (client, _) = Make(r => GitHub(r, setup: Encoding.ASCII.GetBytes("tampered")));
        var check = await client.CheckAsync(Current, CancellationToken.None);
        var result = await client.DownloadAsync(check.Release!, check.SetupHash!, _folder, null, CancellationToken.None);
        Assert.Equal(DownloadOutcome.ChecksumMismatch, result.Outcome);
        Assert.Empty(Directory.Exists(_folder) ? Directory.GetFiles(_folder) : []);
    }

    [Fact]
    public async Task Download_TheWrongFileVersion_IsRefused()
    {
        _fileVersion = new AppVersion(0, 1, 9, false);
        var (client, _) = Make(r => GitHub(r));
        var check = await client.CheckAsync(Current, CancellationToken.None);
        var result = await client.DownloadAsync(check.Release!, check.SetupHash!, _folder, null, CancellationToken.None);
        Assert.Equal(DownloadOutcome.WrongVersion, result.Outcome);
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task Download_LargerThanTheFeedSaid_IsRefused()
    {
        var big = new byte[1024];
        var (client, _) = Make(r => GitHub(r, setup: big));
        var check = await client.CheckAsync(Current, CancellationToken.None);
        var small = check.Release! with { SetupSize = 100 };
        var result = await client.DownloadAsync(small, check.SetupHash!, _folder, null, CancellationToken.None);
        Assert.Equal(DownloadOutcome.TooLarge, result.Outcome);
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task Download_NetworkFailure_LeavesNothing()
    {
        var failSetup = false;
        var (client, _) = Make(r => failSetup && r.RequestUri!.Host == "release-assets.githubusercontent.com" && r.RequestUri.AbsolutePath == "/setup"
            ? throw new HttpRequestException("SENTINEL") : GitHub(r));
        var check = await client.CheckAsync(Current, CancellationToken.None);
        failSetup = true;
        var result = await client.DownloadAsync(check.Release!, check.SetupHash!, _folder, null, CancellationToken.None);
        Assert.Equal(DownloadOutcome.Failed, result.Outcome);
        Assert.Empty(Directory.Exists(_folder) ? Directory.GetFiles(_folder) : []);
    }

    /// <summary>A body with no Content-Length (chunked), so only the running cap can stop it.</summary>
    private sealed class UnsizedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    /// <summary>A body that sends a little, then nothing more (the connection stays open).</summary>
    private sealed class StallingStream : Stream
    {
        private bool _sent;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!_sent) { _sent = true; buffer.Span[0] = 1; return 1; }
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
    }

    /// <summary>A body that breaks off partway (the connection drops).</summary>
    private sealed class TruncatedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count) => Position >= 4 ? throw new IOException("SENTINEL reset") : base.Read(buffer, offset, Math.Min(count, 4));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            Position >= 4 ? throw new IOException("SENTINEL reset") : base.ReadAsync(buffer[..Math.Min(buffer.Length, 4)], ct);
    }

    private static HttpResponseMessage Body(Stream stream) => new(HttpStatusCode.OK) { Content = new StreamContent(stream) };

    private async Task<DownloadResult> DownloadWith(Func<HttpResponseMessage> setupBody, TimeSpan? idle = null)
    {
        var handler = new FakeHttpHandler(r => r.RequestUri!.AbsolutePath == "/setup" ? setupBody() : GitHub(r));
        var client = new UpdateClient(HttpClients.Create(handler), _log, readFileVersion: _ => _fileVersion, idleTimeout: idle);
        var check = await client.CheckAsync(Current, CancellationToken.None);
        return await client.DownloadAsync(check.Release!, check.SetupHash!, _folder, null, CancellationToken.None);
    }

    [Fact]
    public async Task Download_AnUnsizedBodyPastTheCap_IsRefused_AndLeavesNothing()
    {
        var handler = new FakeHttpHandler(r => r.RequestUri!.AbsolutePath == "/setup" ? Body(new UnsizedStream(new byte[4096])) : GitHub(r));
        var client = new UpdateClient(HttpClients.Create(handler), _log, readFileVersion: _ => _fileVersion);
        var check = await client.CheckAsync(Current, CancellationToken.None);
        var result = await client.DownloadAsync(check.Release! with { SetupSize = 100 }, check.SetupHash!, _folder, null, CancellationToken.None);
        Assert.Equal(DownloadOutcome.TooLarge, result.Outcome);
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task Download_AStalledBody_GivesUp_AndLeavesNothing()
    {
        var result = await DownloadWith(() => Body(new StallingStream()), idle: TimeSpan.FromMilliseconds(300));
        Assert.Equal(DownloadOutcome.Failed, result.Outcome);
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task Download_ATruncatedBody_Fails_AndLeavesNothing()
    {
        var result = await DownloadWith(() => Body(new TruncatedStream(Setup)));
        Assert.Equal(DownloadOutcome.Failed, result.Outcome);
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task Download_AFolderThatCantBeMade_Fails()
    {
        var (client, _) = Make(r => GitHub(r));
        var check = await client.CheckAsync(Current, CancellationToken.None);
        var file = Path.Combine(Path.GetTempPath(), "yolime-update-tests-file-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(file, "x");
        try
        {
            // A file where the folder should be: CreateDirectory fails.
            var result = await client.DownloadAsync(check.Release!, check.SetupHash!, Path.Combine(file, "updates"), null, CancellationToken.None);
            Assert.Equal(DownloadOutcome.Failed, result.Outcome);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task Token_GoesOnlyToTheApi_NeverOnTheRedirect()
    {
        var handler = new FakeHttpHandler(r => GitHub(r));
        var client = new UpdateClient(HttpClients.Create(handler), _log, readFileVersion: _ => _fileVersion, apiToken: "TOKEN");
        Assert.Equal(CheckOutcome.Available, (await client.CheckAsync(Current, CancellationToken.None)).Outcome);
        foreach (var (request, _) in handler.Requests)
        {
            var auth = request.Headers.Authorization?.ToString();
            if (request.RequestUri!.Host == "api.github.com") Assert.Equal("Bearer TOKEN", auth);
            else Assert.Null(auth);
        }
    }

    [Fact]
    public async Task TheCallersCancel_IsThrown_NotTurnedIntoTryLater()
    {
        var (client, _) = Make(r => GitHub(r));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CheckAsync(Current, cts.Token));
    }

    [Fact]
    public async Task TheCallersCancel_MidDownload_IsThrown_AndLeavesNothing()
    {
        var handler = new FakeHttpHandler(r => r.RequestUri!.AbsolutePath == "/setup" ? Body(new StallingStream()) : GitHub(r));
        var client = new UpdateClient(HttpClients.Create(handler), _log, readFileVersion: _ => _fileVersion);
        var check = await client.CheckAsync(Current, CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DownloadAsync(check.Release!, check.SetupHash!, _folder, null, cts.Token));
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task Logs_HoldNoContent()
    {
        var (client, _) = Make(r => r.RequestUri!.ToString() == SumsUrl ? Redirect("https://evil.example/SECRET") : GitHub(r));
        await client.CheckAsync(Current, CancellationToken.None);
        var (bad, _) = Make(_ => throw new HttpRequestException("SENTINEL https://sentinel.example/"));
        await bad.CheckAsync(Current, CancellationToken.None);
        // And the download's failures: a refused redirect, a broken body.
        var (good, _) = Make(r => GitHub(r));
        var check = await good.CheckAsync(Current, CancellationToken.None);
        var (redirected, _) = Make(r => r.RequestUri!.ToString() == SetupUrl ? Redirect("https://evil.example/SECRET") : GitHub(r));
        await redirected.DownloadAsync(check.Release!, check.SetupHash!, _folder, null, CancellationToken.None);
        await DownloadWith(() => Body(new TruncatedStream(Setup)));
        Assert.NotEmpty(_log.Lines);
        var all = string.Join("\n", _log.Lines.Select(l => l.Event + " " + string.Join(" ", l.Fields)));
        foreach (var leak in new[] { "SENTINEL", "SECRET", "evil", "sentinel", "github" }) Assert.DoesNotContain(leak, all, StringComparison.OrdinalIgnoreCase);
    }
}
