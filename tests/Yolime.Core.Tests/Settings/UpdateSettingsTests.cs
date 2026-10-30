using Microsoft.Extensions.Time.Testing;
using Yolime.Core.Settings;

namespace Yolime.Core.Tests.Settings;

public sealed class UpdateSettingsTests : IDisposable
{
    private readonly TempRoot _root = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _root.Dispose();

    private SettingsStore Store() => new(_root.Paths, new RecordingLog(), _time, null);

    [Fact]
    public void Defaults_CheckAutomatically_NothingCheckedOrNotifiedYet()
    {
        var s = new YolimeSettings();
        Assert.True(s.CheckForUpdates);
        Assert.Null(s.LastUpdateCheck);
        Assert.Null(s.LastUpdateFailure);
        Assert.Null(s.LastNotifiedUpdate);
    }

    [Fact]
    public void AnOlderFile_WithoutTheFields_LoadsWithTheDefaults()
    {
        Directory.CreateDirectory(_root.Paths.RoamingDir);
        File.WriteAllText(_root.Paths.SettingsFile, """{ "schemaVersion": 1, "sounds": false }""");
        var loaded = Store().Load();
        Assert.Equal(SettingsLoadStatus.Ok, loaded.Status);
        Assert.True(loaded.Settings.CheckForUpdates);
    }

    [Fact]
    public void TheUpdateFields_RoundTrip()
    {
        var store = Store();
        var checkedAt = new DateTimeOffset(2026, 10, 29, 9, 30, 0, TimeSpan.FromHours(-4));
        Assert.Equal(SaveResult.Saved, store.Save(new YolimeSettings
        {
            CheckForUpdates = false,
            LastUpdateCheck = checkedAt,
            LastUpdateFailure = checkedAt.AddHours(1),
            LastNotifiedUpdate = "0.2.0",
        }));
        var loaded = store.Load().Settings;
        Assert.False(loaded.CheckForUpdates);
        Assert.Equal(checkedAt, loaded.LastUpdateCheck);
        Assert.Equal(checkedAt.AddHours(1), loaded.LastUpdateFailure);
        Assert.Equal("0.2.0", loaded.LastNotifiedUpdate);
        Assert.Contains("\"checkForUpdates\": false", File.ReadAllText(_root.Paths.SettingsFile));
    }
}
