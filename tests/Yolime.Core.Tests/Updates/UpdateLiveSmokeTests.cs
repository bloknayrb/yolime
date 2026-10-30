using Yolime.Core.Net;
using Yolime.Core.Updates;

namespace Yolime.Core.Tests.Updates;

/// <summary>
/// The real chain against a real repo's latest release: the feed, the asset redirect, the sums file, the download and
/// its hash. Runs only with YOLIME_LIVE=1 and YOLIME_UPDATE_REPO=owner/repo (the release workflow's smoke job sets
/// them for the release it just published), on Windows, which reads the setup's file version as yolime does.
/// </summary>
public sealed class UpdateLiveSmokeTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "yolime-update-live", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    [Fact]
    public async Task LatestRelease_DownloadsAndMatchesItsChecksum()
    {
        if (Environment.GetEnvironmentVariable("YOLIME_LIVE") != "1" || Environment.GetEnvironmentVariable("YOLIME_UPDATE_REPO") is not { } repo)
        {
            Assert.Skip("set YOLIME_LIVE=1 and YOLIME_UPDATE_REPO=owner/repo");
            return;
        }
        var parts = repo.Split('/');
        // CI passes its job token: shared runner IPs use up the anonymous rate limit.
        var log = new RecordingLog();
        var client = new UpdateClient(HttpClients.Shared, log, parts[0], parts[1], apiToken: Environment.GetEnvironmentVariable("YOLIME_UPDATE_TOKEN"));
        var check = await client.CheckAsync(new AppVersion(0, 0, 0, false), TestContext.Current.CancellationToken);
        string Trail() => string.Join("; ", log.Lines.Select(l => l.Event + " " + string.Join(" ", l.Fields)));
        Assert.True(check.Outcome == CheckOutcome.Available, $"check: {check.Outcome} ({Trail()})");
        // The release workflow passes the version it just published, so another "latest" can't pass for it.
        if (Environment.GetEnvironmentVariable("YOLIME_UPDATE_EXPECT") is { Length: > 0 } expected)
            Assert.Equal(expected, check.Release!.Version.ToString());
        var download = await client.DownloadAsync(check.Release!, check.SetupHash!, _folder, null, TestContext.Current.CancellationToken);
        Assert.True(download.Outcome == DownloadOutcome.Ready, $"download: {download.Outcome} ({Trail()})");
    }
}
