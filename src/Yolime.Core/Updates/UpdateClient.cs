using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Yolime.Core.Logging;
using Yolime.Core.Net;

namespace Yolime.Core.Updates;

public enum CheckOutcome { UpToDate, Available, TryLater }

/// <param name="SetupHash">The setup's SHA-256 (upper-case hex) from the release's SHA256SUMS.txt.</param>
public sealed record CheckResult(CheckOutcome Outcome, ReleaseInfo? Release = null, string? SetupHash = null);

public enum DownloadOutcome { Ready, Failed, TooLarge, ChecksumMismatch, WrongVersion }

public sealed record DownloadResult(DownloadOutcome Outcome, string? Path = null);

/// <summary>
/// Looks for a newer yolime on GitHub Releases and downloads it. Everything goes to api.github.com, pinned like every
/// other yolime request; an asset may be served there directly or after exactly one redirect, which must be https, on
/// the default port, with no user info, to GitHub's asset hosts. The setup is kept only if its SHA-256 matches the
/// release's SHA256SUMS.txt and its file version is the release's. Logs carry status codes, counts and versions,
/// never the feed, a URL or an exception's message.
/// </summary>
/// <param name="apiToken">Only for CI's smoke test (shared runner IPs hit the anonymous rate limit): sent to
/// api.github.com only, never on the redirect to the asset host. yolime itself never has one.</param>
/// <param name="idleTimeout">How long a download may go without a byte before it's given up (default a minute).</param>
public sealed class UpdateClient(HttpClient http, ILog log, string owner = "bloknayrb", string repo = "yolime",
    Func<string, AppVersion?>? readFileVersion = null, string? apiToken = null, TimeSpan? idleTimeout = null)
{
    private const string ApiHost = "api.github.com";
    private static readonly string[] AssetHosts = ["release-assets.githubusercontent.com", "objects.githubusercontent.com"];
    private const long SumsMaxBytes = 64 * 1024;
    private const long SetupMaxBytes = 512L * 1024 * 1024;
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DownloadCeiling = TimeSpan.FromMinutes(30);

    private readonly Func<string, AppVersion?> _readFileVersion = readFileVersion ?? ReadFileVersion;
    private readonly TimeSpan _idle = idleTimeout ?? TimeSpan.FromMinutes(1);

    /// <summary>"yolime/0.1.0": GitHub's API refuses requests without one, and it says nothing but the version.</summary>
    public static string UserAgent { get; } =
        "yolime/" + (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0");

    public async Task<CheckResult> CheckAsync(AppVersion current, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CheckTimeout);
        try
        {
            using var request = Request(new Uri($"https://{ApiHost}/repos/{owner}/{repo}/releases/latest"), ApiHost);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                log.Write(LogEvent.UpdateCheckFailed, LogField.HttpStatus((int)response.StatusCode));
                return new(CheckOutcome.TryLater);
            }
            var json = await ReadCappedAsync(response, 1024 * 1024, timeout.Token).ConfigureAwait(false);
            if (json is null) return Failed(null);
            var feed = ReleaseFeed.Parse(Encoding.UTF8.GetString(json), current);
            if (feed.Outcome != FeedOutcome.Newer)
            {
                log.Write(LogEvent.UpdateChecked, LogField.Tag("feed", feed.Outcome));
                return new(feed.Outcome == FeedOutcome.TryLater ? CheckOutcome.TryLater : CheckOutcome.UpToDate);
            }
            var release = feed.Release!;
            var sums = await GetAssetAsync(release.SumsAssetId, SumsMaxBytes, timeout.Token).ConfigureAwait(false);
            var hash = sums is null ? null : Sha256Sums.HashFor(Encoding.UTF8.GetString(sums), release.SetupName);
            if (hash is null)
            {
                log.Write(LogEvent.UpdateCheckFailed, LogField.Tag("feed", FeedOutcome.TryLater));
                return new(CheckOutcome.TryLater);
            }
            log.Write(LogEvent.UpdateChecked, LogField.Tag("feed", FeedOutcome.Newer), LogField.Count("major", release.Version.Major),
                LogField.Count("minor", release.Version.Minor), LogField.Count("patch", release.Version.Patch));
            return new(CheckOutcome.Available, release, hash);
        }
        catch (Exception ex) when (ex is HttpRequestException or HostPinningException or OperationCanceledException or IOException && !ct.IsCancellationRequested)
        {
            return Failed(ex);
        }
    }

    /// <summary>
    /// Downloads the setup into <paramref name="folder"/> under the name built from the version, checking its size as
    /// it arrives and its hash and file version at the end. Nothing is left behind unless it passed.
    /// </summary>
    public async Task<DownloadResult> DownloadAsync(ReleaseInfo release, string setupHash, string folder, IProgress<double>? progress, CancellationToken ct)
    {
        var final = Path.Combine(folder, release.SetupName);
        var part = final + ".part";
        var cap = Math.Min(release.SetupSize, SetupMaxBytes);
        // An overall ceiling, and an idle limit reset on every read: a connection that stays open but sends nothing
        // must not hang the download (and keep the .part locked) for good.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(DownloadCeiling);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(limit.Token);
        try
        {
            Directory.CreateDirectory(folder);
            idle.CancelAfter(_idle);
            using var response = await SendAssetAsync(release.SetupAssetId, LogEvent.UpdateDownloadFailed, idle.Token).ConfigureAwait(false);
            if (response is null) return Download(DownloadOutcome.Failed);
            if (response.Content.Headers.ContentLength is { } announced && announced > cap) return Download(DownloadOutcome.TooLarge);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            var tooLarge = false;
            await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            await using (var body = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false))
            {
                var buffer = new byte[81920];
                int n;
                idle.CancelAfter(_idle);
                while ((n = await body.ReadAsync(buffer, idle.Token).ConfigureAwait(false)) > 0)
                {
                    idle.CancelAfter(_idle);
                    total += n;
                    if (total > cap) { tooLarge = true; break; }
                    sha.AppendData(buffer, 0, n);
                    await file.WriteAsync(buffer.AsMemory(0, n), idle.Token).ConfigureAwait(false);
                    progress?.Report((double)total / cap);
                }
            }
            // Only once the file is closed: it was opened without delete sharing, so a delete inside would fail.
            if (tooLarge)
            {
                Delete(part);
                return Download(DownloadOutcome.TooLarge);
            }
            if (!string.Equals(Convert.ToHexString(sha.GetHashAndReset()), setupHash, StringComparison.OrdinalIgnoreCase))
            {
                Delete(part);
                return Download(DownloadOutcome.ChecksumMismatch);
            }
            if (_readFileVersion(part) is not { } fileVersion || fileVersion != release.Version)
            {
                Delete(part);
                return Download(DownloadOutcome.WrongVersion);
            }
            File.Move(part, final, overwrite: true);
            log.Write(LogEvent.UpdateDownloaded, LogField.Count("bytes", total));
            return new(DownloadOutcome.Ready, final);
        }
        catch (Exception ex) when (ex is HttpRequestException or HostPinningException or IOException or UnauthorizedAccessException
            || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            Delete(part);
            log.Write(LogEvent.UpdateDownloadFailed, LogField.Error(ex));
            return new(DownloadOutcome.Failed);
        }
        catch
        {
            Delete(part);
            throw;
        }
    }

    private DownloadResult Download(DownloadOutcome outcome)
    {
        log.Write(LogEvent.UpdateDownloadFailed, LogField.Tag("outcome", outcome));
        return new(outcome);
    }

    private CheckResult Failed(Exception? ex)
    {
        if (ex is null) log.Write(LogEvent.UpdateCheckFailed, LogField.Tag("feed", FeedOutcome.TryLater));
        else log.Write(LogEvent.UpdateCheckFailed, LogField.Error(ex));
        return new(CheckOutcome.TryLater);
    }

    private async Task<byte[]?> GetAssetAsync(long id, long maxBytes, CancellationToken ct)
    {
        using var response = await SendAssetAsync(id, LogEvent.UpdateCheckFailed, ct).ConfigureAwait(false);
        return response is null ? null : await ReadCappedAsync(response, maxBytes, ct).ConfigureAwait(false);
    }

    /// <summary>The asset's response (200), served by the API or after one checked redirect; null otherwise.</summary>
    /// <param name="failure">What a refusal is logged as: part of a check, or of a download.</param>
    private async Task<HttpResponseMessage?> SendAssetAsync(long id, LogEvent failure, CancellationToken ct)
    {
        var request = Request(new Uri($"https://{ApiHost}/repos/{owner}/{repo}/releases/assets/{id}"), ApiHost);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        HttpResponseMessage first;
        using (request) first = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (first.StatusCode == HttpStatusCode.OK) return first;
        var location = first.Headers.Location;
        var status = (int)first.StatusCode;
        first.Dispose();
        if (status is not (301 or 302 or 303 or 307 or 308) || !AllowedAssetLocation(location))
        {
            log.Write(failure, LogField.HttpStatus(status));
            return null;
        }
        // A fresh request: nothing of the first is carried over (the signed URL is the only credential it needs).
        using var hop = Request(location!, location!.Host);
        var second = await http.SendAsync(hop, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (second.StatusCode == HttpStatusCode.OK) return second;
        log.Write(failure, LogField.HttpStatus((int)second.StatusCode));
        second.Dispose();
        return null;
    }

    internal static bool AllowedAssetLocation(Uri? location) =>
        location is { IsAbsoluteUri: true } u
        && u.Scheme == Uri.UriSchemeHttps
        && u.IsDefaultPort
        && string.IsNullOrEmpty(u.UserInfo)
        && AssetHosts.Contains(u.Host, StringComparer.OrdinalIgnoreCase);

    private HttpRequestMessage Request(Uri uri, string host)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        // The token only ever goes to the API itself, never to the asset host a redirect names.
        if (apiToken is not null && string.Equals(host, ApiHost, StringComparison.Ordinal))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
        HostPinningHandler.Expect(request, host);
        return request;
    }

    private static async Task<byte[]?> ReadCappedAsync(HttpResponseMessage response, long maxBytes, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is { } n && n > maxBytes) return null;
        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = await body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static void Delete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>The setup's own file version (major.minor.build, with the private part 0), as the release's tag.</summary>
    private static AppVersion? ReadFileVersion(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        return info.FilePrivatePart == 0 ? new AppVersion(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, false) : null;
    }
}
