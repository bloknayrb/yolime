using Microsoft.Extensions.Time.Testing;
using Yolime.Core.Settings;

namespace Yolime.Core.Tests.Settings;

public sealed class SettingsHolderTests : IDisposable
{
    private readonly TempRoot _root = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _root.Dispose();

    private SettingsHolder Holder(out SettingsStore store)
    {
        store = new SettingsStore(_root.Paths, new RecordingLog(), _time, null);
        return new SettingsHolder(store, store.Load().Settings);
    }

    [Fact]
    public void Update_Saves_ThenAppliesAndAnnounces()
    {
        using var h = Holder(out var store);
        var changes = new List<(YolimeSettings Old, YolimeSettings New)>();
        h.Changed += (o, n) => changes.Add((o, n));

        Assert.Equal(SaveResult.Saved, h.Update(s => s with { Sounds = false }));
        Assert.False(h.Current.Sounds);
        Assert.False(store.Load().Settings.Sounds);
        var (old, now) = Assert.Single(changes);
        Assert.True(old.Sounds);
        Assert.False(now.Sounds);
    }

    [Fact]
    public void AFailedSave_ChangesNothing()
    {
        new SettingsStore(_root.Paths, new RecordingLog(), _time, null).Save(new YolimeSettings());
        var store = new SettingsStore(_root.Paths, new RecordingLog(), _time, _ => throw new IOException("disk full"));
        using var h = new SettingsHolder(store, store.Load().Settings);
        var raised = false;
        h.Changed += (_, _) => raised = true;

        Assert.Equal(SaveResult.Failed, h.Update(s => s with { Sounds = false }));
        Assert.True(h.Current.Sounds);
        Assert.False(raised);
    }

    [Fact]
    public void AHandEdit_IsPickedUp_ButOurOwnSaveIsNotAnnouncedTwice()
    {
        using var h = Holder(out var store);
        h.Update(s => s with { HistoryRetentionDays = 10 });
        var changes = 0;
        h.Changed += (_, _) => changes++;

        h.ReloadFromDisk(); // the watcher firing for our own save
        Assert.Equal(0, changes);

        File.WriteAllText(_root.Paths.SettingsFile, File.ReadAllText(_root.Paths.SettingsFile).Replace("\"historyRetentionDays\": 10", "\"historyRetentionDays\": 5"));
        h.ReloadFromDisk();
        Assert.Equal(1, changes);
        Assert.Equal(5, h.Current.HistoryRetentionDays);
    }

    [Fact]
    public void ABrokenHandEdit_KeepsTheCurrentSettings()
    {
        using var h = Holder(out _);
        h.Update(s => s with { HistoryRetentionDays = 10 });
        File.WriteAllText(_root.Paths.SettingsFile, "{ not json");
        h.ReloadFromDisk();
        Assert.Equal(10, h.Current.HistoryRetentionDays);
        // Left where it is for the user to finish fixing, not moved aside as corrupt.
        Assert.Equal("{ not json", File.ReadAllText(_root.Paths.SettingsFile));
    }

    [Fact]
    public async Task TheWatcher_ReloadsAfterAHandEdit()
    {
        using var h = Holder(out _);
        h.Update(s => s with { HistoryRetentionDays = 10 });
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Changed += (_, n) => { if (n.HistoryRetentionDays == 7) changed.TrySetResult(); };
        h.StartWatching();

        await Task.Delay(100);
        File.WriteAllText(_root.Paths.SettingsFile, File.ReadAllText(_root.Paths.SettingsFile).Replace("\"historyRetentionDays\": 10", "\"historyRetentionDays\": 7"));
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void AfterDispose_AReloadDoesNothing()
    {
        var h = Holder(out _);
        h.Update(s => s with { HistoryRetentionDays = 10 });
        h.StartWatching();
        var changes = 0;
        h.Changed += (_, _) => changes++;
        h.Dispose();
        File.WriteAllText(_root.Paths.SettingsFile, File.ReadAllText(_root.Paths.SettingsFile).Replace("\"historyRetentionDays\": 10", "\"historyRetentionDays\": 4"));
        h.ReloadFromDisk();
        Assert.Equal(0, changes);
        Assert.Equal(10, h.Current.HistoryRetentionDays);
    }
}
