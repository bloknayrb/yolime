using System.Text.Json;

namespace Yolime.Core.Updates;

/// <summary>A release worth offering: its version and the two assets, by the ids GitHub gave them.</summary>
public sealed record ReleaseInfo(AppVersion Version, long SetupAssetId, long SetupSize, long SumsAssetId)
{
    /// <summary>The setup's file name, built here from the version, never taken from the feed.</summary>
    public string SetupName => SetupFileName(Version);

    public static string SetupFileName(AppVersion v) => $"yolime-setup-{v.Major}.{v.Minor}.{v.Patch}.exe";
}

public enum FeedOutcome
{
    /// <summary>A newer release, built by CI, with both assets.</summary>
    Newer,
    /// <summary>The latest release isn't newer than this copy.</summary>
    UpToDate,
    /// <summary>Not something to install (a draft, a prerelease, a hand upload, a misnamed asset): never offered.</summary>
    NotOffered,
    /// <summary>Unreadable, or a release still missing an asset: look again later.</summary>
    TryLater,
}

public sealed record FeedResult(FeedOutcome Outcome, ReleaseInfo? Release = null);

/// <summary>
/// Reads GitHub's "latest release" JSON. A release is offered only when it's a published release whose tag is
/// exactly "vX.Y.Z", newer than this copy, and has the setup and SHA256SUMS.txt under the names built here, both
/// uploaded by CI (github-actions[bot]). Other assets are ignored, so a later release can add files (a signature)
/// without breaking older copies. Nothing else from the feed (its URLs, notes, names) is ever used.
/// </summary>
public static class ReleaseFeed
{
    public const string SumsName = "SHA256SUMS.txt";
    private const string CiUploader = "github-actions[bot]";

    public static FeedResult Parse(string json, AppVersion current)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tag_name", out var tagProp) || tagProp.ValueKind != JsonValueKind.String)
                return new(FeedOutcome.TryLater);
            if (Bool(root, "draft") || Bool(root, "prerelease")) return new(FeedOutcome.NotOffered);
            if (!AppVersion.TryParseTag(tagProp.GetString(), out var version)) return new(FeedOutcome.NotOffered);
            if (!(version > current)) return new(FeedOutcome.UpToDate);
            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return new(FeedOutcome.TryLater);

            var setupName = ReleaseInfo.SetupFileName(version);
            (long Id, long Size)? setup = null, sums = null;
            var foreignSetup = false;
            foreach (var asset in assets.EnumerateArray())
            {
                // Not an object (a number, null): unreadable, not something to throw over.
                if (asset.ValueKind != JsonValueKind.Object) return new(FeedOutcome.TryLater);
                if (!asset.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String) continue;
                var name = nameProp.GetString();
                var isSetup = string.Equals(name, setupName, StringComparison.Ordinal);
                var isSums = string.Equals(name, SumsName, StringComparison.Ordinal);
                if (!isSetup && !isSums)
                {
                    // A setup named for another version means the release isn't what its tag says.
                    if (name is not null && name.StartsWith("yolime-setup-", StringComparison.Ordinal)) foreignSetup = true;
                    continue;
                }
                if (!asset.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.Number || !idProp.TryGetInt64(out var id) || id <= 0
                    || !asset.TryGetProperty("size", out var sizeProp) || sizeProp.ValueKind != JsonValueKind.Number || !sizeProp.TryGetInt64(out var size) || size <= 0)
                    return new(FeedOutcome.TryLater);
                if (!asset.TryGetProperty("uploader", out var up) || up.ValueKind != JsonValueKind.Object
                    || !up.TryGetProperty("login", out var login) || login.ValueKind != JsonValueKind.String
                    || !string.Equals(login.GetString(), CiUploader, StringComparison.Ordinal))
                    return new(FeedOutcome.NotOffered);
                if (isSetup) setup = (id, size); else sums = (id, size);
            }
            if (setup is null) return new(foreignSetup ? FeedOutcome.NotOffered : FeedOutcome.TryLater);
            if (sums is null) return new(FeedOutcome.TryLater);
            return new(FeedOutcome.Newer, new ReleaseInfo(version, setup.Value.Id, setup.Value.Size, sums.Value.Id));
        }
        catch (JsonException)
        {
            return new(FeedOutcome.TryLater);
        }
    }

    private static bool Bool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;
}

/// <summary>Finds a file's SHA-256 in a "sha256sum"-style list: "&lt;64 hex&gt;  name" or "&lt;64 hex&gt; *name".</summary>
public static class Sha256Sums
{
    /// <summary>The hash for exactly that file name, in upper case, or null if there's no well-formed line for it.</summary>
    public static string? HashFor(string text, string fileName)
    {
        foreach (var raw in text.TrimStart('\uFEFF').Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 66) continue;
            var hash = line[..64];
            if (!hash.All(Uri.IsHexDigit)) continue;
            var rest = line[64..];
            var name = rest.StartsWith(" *", StringComparison.Ordinal) ? rest[2..] : rest.StartsWith("  ", StringComparison.Ordinal) ? rest[2..] : null;
            if (string.Equals(name, fileName, StringComparison.Ordinal)) return hash.ToUpperInvariant();
        }
        return null;
    }
}

/// <summary>When to look for an update next.</summary>
public static class UpdateSchedule
{
    public static readonly TimeSpan AfterStart = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);
    public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);

    /// <summary>
    /// A day after the last check (an hour after a later failed one), but never sooner than a minute after start, so a
    /// sign-in isn't slowed by it.
    /// </summary>
    public static DateTimeOffset NextCheck(DateTimeOffset now, DateTimeOffset startedAt, DateTimeOffset? lastChecked, DateTimeOffset? lastFailed)
    {
        // A saved time later than now (the clock ran ahead once, then was corrected) would stop checks until then.
        if (lastChecked > now) lastChecked = null;
        if (lastFailed > now) lastFailed = null;
        var earliest = startedAt + AfterStart;
        DateTimeOffset due;
        if (lastFailed is { } f && (lastChecked is null || f > lastChecked)) due = f + RetryAfter;
        else if (lastChecked is { } c) due = c + Interval;
        else due = earliest;
        return due > earliest ? due : earliest;
    }
}
