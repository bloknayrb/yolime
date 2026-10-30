using Yolime.Core.Updates;

namespace Yolime.Core.Tests.Updates;

public class AppVersionTests
{
    [Theory]
    [InlineData("v0.2.0", 0, 2, 0)]
    [InlineData("v10.0.3", 10, 0, 3)]
    public void Tag_ParsesOnlyVMajorMinorPatch(string tag, int major, int minor, int patch)
    {
        Assert.True(AppVersion.TryParseTag(tag, out var v));
        Assert.Equal(new AppVersion(major, minor, patch, Prerelease: false), v);
    }

    [Theory]
    [InlineData("0.2.0")]
    [InlineData("v0.2")]
    [InlineData("v0.2.0-beta.1")]
    [InlineData("v0.2.0 ")]
    [InlineData("v0.2.0\n")]
    [InlineData("v0.2.0/../x")]
    [InlineData("")]
    public void Tag_RefusesAnythingElse(string tag) => Assert.False(AppVersion.TryParseTag(tag, out _));

    [Theory]
    [InlineData("0.1.0+6063823f8ed8dcf33531f1fc2610072b504573b3       ", 0, 1, 0, false)]
    [InlineData("0.1.0", 0, 1, 0, false)]
    [InlineData("0.2.0-pre.3+abc", 0, 2, 0, true)]
    public void ProductVersion_TakesTheBaseAndNotesAPrerelease(string product, int major, int minor, int patch, bool pre)
    {
        Assert.Equal(new AppVersion(major, minor, patch, pre), AppVersion.FromProductVersion(product));
    }

    [Fact]
    public void ProductVersion_Unreadable_IsNull() => Assert.Null(AppVersion.FromProductVersion("dev"));

    [Fact]
    public void Ordering_IsNumeric_AndAPrereleaseIsOlderThanItsRelease()
    {
        Assert.True(new AppVersion(0, 10, 0, false) > new AppVersion(0, 9, 9, false));
        Assert.True(new AppVersion(0, 2, 0, false) > new AppVersion(0, 2, 0, true));
        Assert.False(new AppVersion(0, 2, 0, false) > new AppVersion(0, 2, 0, false));
    }

    [Fact]
    public void ShortCommit_IsSevenCharacters() =>
        Assert.Equal("0.1.0+6063823", AppVersion.Display("0.1.0+6063823f8ed8dcf33531f1fc2610072b504573b3   "));
}

public class ReleaseFeedTests
{
    private static readonly AppVersion Current = new(0, 1, 0, false);

    internal static string Feed(string tag = "v0.2.0", string setupName = "yolime-setup-0.2.0.exe", string uploader = "github-actions[bot]",
        bool draft = false, bool prerelease = false, string extraAssets = "", bool withSums = true) =>
        $$"""
        {"tag_name":"{{tag}}","draft":{{draft.ToString().ToLowerInvariant()}},"prerelease":{{prerelease.ToString().ToLowerInvariant()}},"body":"SENTINEL notes","assets":[
          {"id":101,"name":"{{setupName}}","size":80000000,"uploader":{"login":"{{uploader}}"},"browser_download_url":"https://evil.example/x.exe"}
          {{(withSums ? """,{"id":102,"name":"SHA256SUMS.txt","size":120,"uploader":{"login":"github-actions[bot]"}}""" : "")}}
          {{extraAssets}}
        ]}
        """;

    [Fact]
    public void Newer_GivesTheAssetIds_FromTheNamesWeBuilt()
    {
        var r = ReleaseFeed.Parse(Feed(), Current);
        Assert.Equal(FeedOutcome.Newer, r.Outcome);
        Assert.Equal(new AppVersion(0, 2, 0, false), r.Release!.Version);
        Assert.Equal(101, r.Release.SetupAssetId);
        Assert.Equal(102, r.Release.SumsAssetId);
        Assert.Equal(80000000, r.Release.SetupSize);
    }

    /// <summary>
    /// GitHub's real answer, recorded from the release workflow's dry run (a throwaway repo, 2026-10-30): the shape the
    /// hand-written <see cref="Feed"/> imitates, with every field GitHub actually sends.
    /// </summary>
    [Fact]
    public void ARecordedRelease_FromTheWorkflow_IsOffered()
    {
        var json = File.ReadAllText(Fixtures.Update("latest-release.json"));
        var r = ReleaseFeed.Parse(json, Current);
        Assert.Equal(FeedOutcome.Newer, r.Outcome);
        Assert.Equal(new AppVersion(0, 1, 1, false), r.Release!.Version);
        Assert.Equal(692171554, r.Release.SetupAssetId);
        Assert.Equal(692171667, r.Release.SumsAssetId);
        Assert.Equal(78869216, r.Release.SetupSize);
        Assert.Equal(FeedOutcome.UpToDate, ReleaseFeed.Parse(json, new AppVersion(0, 1, 1, false)).Outcome);
    }

    [Fact]
    public void ExtraAssets_AreIgnored_SoALaterSignatureFileDoesntBreakIt() =>
        Assert.Equal(FeedOutcome.Newer, ReleaseFeed.Parse(Feed(extraAssets: """,{"id":103,"name":"SHA256SUMS.txt.sig","size":64,"uploader":{"login":"github-actions[bot]"}}"""), Current).Outcome);

    [Theory]
    [InlineData("v0.1.0")]
    [InlineData("v0.0.9")]
    public void SameOrOlder_IsUpToDate_NeverADowngrade(string tag) =>
        Assert.Equal(FeedOutcome.UpToDate, ReleaseFeed.Parse(Feed(tag: tag, setupName: $"yolime-setup-{tag[1..]}.exe"), Current).Outcome);

    [Fact]
    public void NewerThanAPrerelease_OfTheSameVersion() =>
        Assert.Equal(FeedOutcome.Newer, ReleaseFeed.Parse(Feed(tag: "v0.1.0", setupName: "yolime-setup-0.1.0.exe"), new AppVersion(0, 1, 0, true)).Outcome);

    [Theory]
    [InlineData("v0.2.0-beta", "yolime-setup-0.2.0.exe", "github-actions[bot]", false, false)]   // tag shape
    [InlineData("v0.2.0", "yolime-setup-0.2.1.exe", "github-actions[bot]", false, false)]        // setup named for another version
    [InlineData("v0.2.0", "yolime-setup-0.2.0.exe", "bloknayrb", false, false)]                   // uploaded by hand
    [InlineData("v0.2.0", "yolime-setup-0.2.0.exe", "github-actions[bot]", true, false)]         // a draft
    [InlineData("v0.2.0", "yolime-setup-0.2.0.exe", "github-actions[bot]", false, true)]         // a prerelease
    public void Untrusted_IsNotOffered(string tag, string setup, string uploader, bool draft, bool pre) =>
        Assert.Equal(FeedOutcome.NotOffered, ReleaseFeed.Parse(Feed(tag, setup, uploader, draft, pre), Current).Outcome);

    [Fact]
    public void MissingSums_IsNotOffered_YetTryLater() =>
        Assert.Equal(FeedOutcome.TryLater, ReleaseFeed.Parse(Feed(withSums: false), Current).Outcome);

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"tag_name":"v0.2.0","assets":[1]}""")]
    [InlineData("""{"tag_name":"v0.2.0","assets":[null,{"id":101}]}""")]
    [InlineData("""{"tag_name":"v0.2.0","assets":[{"id":"x","name":"yolime-setup-0.2.0.exe"}]}""")]
    [InlineData("""{"tag_name":"v0.2.0","assets":[{"id":-1,"name":"yolime-setup-0.2.0.exe","size":1,"uploader":{"login":"github-actions[bot]"}},{"id":2,"name":"SHA256SUMS.txt","size":1,"uploader":{"login":"github-actions[bot]"}}]}""")]
    public void Malformed_IsTryLater(string json) => Assert.Equal(FeedOutcome.TryLater, ReleaseFeed.Parse(json, Current).Outcome);
}

public class Sha256SumsTests
{
    private const string Hash = "9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08";

    [Theory]
    [InlineData("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08  yolime-setup-0.2.0.exe\n")]
    [InlineData("\uFEFF9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08 *yolime-setup-0.2.0.exe\r\n")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000  other.exe\r\n9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08  yolime-setup-0.2.0.exe")]
    public void FindsTheLine_ForThatNameOnly(string text) => Assert.Equal(Hash, Sha256Sums.HashFor(text, "yolime-setup-0.2.0.exe"));

    [Theory]
    [InlineData("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08  yolime-setup-0.2.0.exe.bak\n")]
    [InlineData("9f86d081  yolime-setup-0.2.0.exe\n")]
    [InlineData("zz86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08  yolime-setup-0.2.0.exe\n")]
    [InlineData("")]
    public void NoValidLine_IsNull(string text) => Assert.Null(Sha256Sums.HashFor(text, "yolime-setup-0.2.0.exe"));
}

public class UpdateScheduleTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Now = Start.AddHours(3);

    [Fact]
    public void FirstCheck_AMinuteAfterStart() => Assert.Equal(Start.AddMinutes(1), UpdateSchedule.NextCheck(Start, Start, lastChecked: null, lastFailed: null));

    [Fact]
    public void AfterACheck_A_DayLater() =>
        Assert.Equal(Start.AddHours(-2).AddDays(1), UpdateSchedule.NextCheck(Now, Start, lastChecked: Start.AddHours(-2), lastFailed: null));

    [Fact]
    public void ADayOverdue_StillWaitsTheMinuteAfterStart() =>
        Assert.Equal(Start.AddMinutes(1), UpdateSchedule.NextCheck(Start, Start, lastChecked: Start.AddDays(-3), lastFailed: null));

    [Fact]
    public void AfterAFailure_AnHourLater_NotADay() =>
        Assert.Equal(Start.AddMinutes(30).AddHours(1), UpdateSchedule.NextCheck(Now, Start, lastChecked: Start.AddDays(-2), lastFailed: Start.AddMinutes(30)));

    [Fact]
    public void AFailureOlderThanTheLastCheck_DoesntCount() =>
        Assert.Equal(Start.AddHours(1).AddDays(1), UpdateSchedule.NextCheck(Now, Start, lastChecked: Start.AddHours(1), lastFailed: Start));

    /// <summary>A clock that once ran far ahead and was corrected mustn't stop checks until that future date.</summary>
    [Fact]
    public void ATimeInTheFuture_IsIgnored() =>
        Assert.Equal(Start.AddMinutes(1), UpdateSchedule.NextCheck(Start, Start, lastChecked: Start.AddDays(30), lastFailed: Start.AddDays(20)));
}
