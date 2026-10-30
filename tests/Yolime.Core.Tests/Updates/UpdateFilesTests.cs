using Yolime.Core.Updates;

namespace Yolime.Core.Tests.Updates;

public sealed class UpdateFilesTests : IDisposable
{
    private static readonly AppVersion V010 = new(0, 1, 0, false);
    private static readonly AppVersion V011 = new(0, 1, 1, false);
    private readonly TempRoot _root = new();

    private string Dir => Path.Combine(_root.Dir, "updates");

    public void Dispose() => _root.Dispose();

    [Fact]
    public void Prepare_ClearsTheLastResultAndLog_AndWritesTheMarker()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, UpdateFiles.ResultName), "ok");
        File.WriteAllText(Path.Combine(Dir, UpdateFiles.LogName), "old log");
        Assert.True(UpdateFiles.Prepare(Dir, V011));
        Assert.False(File.Exists(Path.Combine(Dir, UpdateFiles.ResultName)));
        Assert.False(File.Exists(Path.Combine(Dir, UpdateFiles.LogName)));
        Assert.Equal("0.1.1", File.ReadAllText(Path.Combine(Dir, UpdateFiles.MarkerName)));
    }

    [Fact]
    public void Prepare_StopsWhenAnOldResultCantBeDeleted()
    {
        Directory.CreateDirectory(Dir);
        var result = Path.Combine(Dir, UpdateFiles.ResultName);
        File.WriteAllText(result, "ok");
        using (new FileStream(result, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.False(UpdateFiles.Prepare(Dir, V011));
        // No marker: nothing was launched, so the next start must not report an update.
        Assert.False(File.Exists(Path.Combine(Dir, UpdateFiles.MarkerName)));
    }

    [Fact]
    public void NoMarker_MeansNoUpdateWasTried()
    {
        Assert.Null(UpdateFiles.ReadAndClear(Dir, V010));
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, UpdateFiles.ResultName), "swap-failed");
        Assert.Null(UpdateFiles.ReadAndClear(Dir, V010));
    }

    [Fact]
    public void RunningTheExpectedVersion_IsASuccess_AndBothFilesAreGone()
    {
        Assert.True(UpdateFiles.Prepare(Dir, V011));
        File.WriteAllText(Path.Combine(Dir, UpdateFiles.ResultName), "ok");
        var outcome = UpdateFiles.ReadAndClear(Dir, V011);
        Assert.NotNull(outcome);
        Assert.True(outcome!.Succeeded);
        Assert.Equal(V011, outcome.Expected);
        Assert.False(File.Exists(Path.Combine(Dir, UpdateFiles.MarkerName)));
        Assert.False(File.Exists(Path.Combine(Dir, UpdateFiles.ResultName)));
        // Once: a second start says nothing.
        Assert.Null(UpdateFiles.ReadAndClear(Dir, V011));
    }

    [Theory]
    [InlineData("swap-failed", UpdateFailure.SwapFailed)]
    [InlineData("check-failed\r\n", UpdateFailure.CheckFailed)]
    [InlineData("running", UpdateFailure.Running)]
    [InlineData("files-in-use", UpdateFailure.FilesInUse)]
    [InlineData("error", UpdateFailure.Error)]
    [InlineData("setup-failed", UpdateFailure.SetupFailed)]
    [InlineData("ok", UpdateFailure.Unexpected)] // "ok" but still the old copy
    [InlineData("data-kept", UpdateFailure.Unexpected)]
    [InlineData("<script>", UpdateFailure.Unexpected)]
    [InlineData(null, UpdateFailure.NoResult)]
    public void StillTheOldVersion_IsAFailure_WithAFixedReason(string? word, UpdateFailure failure)
    {
        Assert.True(UpdateFiles.Prepare(Dir, V011));
        if (word is not null) File.WriteAllText(Path.Combine(Dir, UpdateFiles.ResultName), word);
        var outcome = UpdateFiles.ReadAndClear(Dir, V010);
        Assert.NotNull(outcome);
        Assert.False(outcome!.Succeeded);
        Assert.Equal(failure, outcome.Failure);
        Assert.Equal(V010, outcome.Running);
    }

    [Fact]
    public void AnUnreadableMarker_IsDropped()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, UpdateFiles.MarkerName), "not a version");
        Assert.Null(UpdateFiles.ReadAndClear(Dir, V010));
        Assert.False(File.Exists(Path.Combine(Dir, UpdateFiles.MarkerName)));
    }

    [Fact]
    public void SetupArguments_AreSeparateTokens_WithNoQuoteCharacters()
    {
        // ProcessStartInfo.ArgumentList wraps a token with a space in quotes; one holding a quote would be
        // backslash-escaped, which Inno's command-line parser doesn't understand.
        var args = UpdateFiles.SetupArguments(@"C:\Users\A B\AppData\Local\Yolime\updates");
        Assert.Equal(["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/NOCANCEL", "/UPDATE",
            @"/RESULT=C:\Users\A B\AppData\Local\Yolime\updates\result.txt", @"/LOG=C:\Users\A B\AppData\Local\Yolime\updates\setup.log"], args);
        Assert.DoesNotContain(args, a => a.Contains('"', StringComparison.Ordinal));
    }

    [Fact]
    public void DownloadedSetups_AreDeleted_OthersKept()
    {
        Directory.CreateDirectory(Dir);
        foreach (var name in new[] { "yolime-setup-0.1.1.exe", "yolime-setup-0.1.1.exe.part", UpdateFiles.LogName, "other.txt" })
            File.WriteAllText(Path.Combine(Dir, name), "x");
        Assert.True(UpdateFiles.DeleteSetups(Dir));
        Assert.Equal(["other.txt", UpdateFiles.LogName], Directory.GetFiles(Dir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ASetupStillInUse_IsReportedForALaterTry()
    {
        Directory.CreateDirectory(Dir);
        var setup = Path.Combine(Dir, "yolime-setup-0.1.1.exe");
        File.WriteAllText(setup, "x");
        using (new FileStream(setup, FileMode.Open, FileAccess.Read, FileShare.Read)) Assert.False(UpdateFiles.DeleteSetups(Dir));
        Assert.True(UpdateFiles.DeleteSetups(Dir));
        Assert.True(UpdateFiles.DeleteSetups(Path.Combine(_root.Dir, "missing")));
    }
}

public class UpdateGateTests
{
    private const string Local = @"C:\Users\b\AppData\Local";

    [Theory]
    [InlineData(@"C:\Users\b\AppData\Local\Programs\Yolime", true)]
    [InlineData(@"C:\Users\b\AppData\Local\Programs\Yolime\", true)]
    [InlineData(@"c:\users\B\appdata\local\programs\yolime", true)]
    [InlineData(@"C:\Users\b\AppData\Local\Programs\Yolime\..\Yolime", true)]
    [InlineData(@"C:\Users\b\AppData\Local\Programs\Yolime.old", false)]
    [InlineData(@"C:\Users\b\AppData\Local\Programs\Yolime\sub", false)]
    [InlineData(@"C:\code\yolime\src\Yolime.App\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64", false)]
    [InlineData(@"C:\e2e\app", false)]
    public void Installed_OnlyFromTheInstallFolderItself(string exeDir, bool installed) =>
        Assert.Equal(installed, UpdateGate.IsInstalled(exeDir, Local));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void NoKnownFolder_IsNotInstalled(string? local) => Assert.False(UpdateGate.IsInstalled(@"C:\Programs\Yolime", local));

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("garbage", true)]
    [InlineData("0.1.9", true)]
    [InlineData("0.2.0", false)]
    [InlineData("0.3.0", false)]
    public void ANotice_IsShownOncePerVersion(string? lastNotified, bool show) =>
        Assert.Equal(show, UpdateGate.ShouldNotify(new AppVersion(0, 2, 0, false), lastNotified));
}
