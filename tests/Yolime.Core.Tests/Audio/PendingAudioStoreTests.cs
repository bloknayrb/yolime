using Microsoft.Extensions.Time.Testing;
using Yolime.Core.Audio;

namespace Yolime.Core.Tests.Audio;

public sealed class PendingAudioStoreTests : IDisposable
{
    private readonly TempRoot _root = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _root.Dispose();

    private PendingAudioStore Store() => new(_root.Paths, new RecordingLog(), _time);

    [Fact]
    public async Task Pending_BeginAppendFinalise_Readable()
    {
        var store = Store();
        var id = Guid.NewGuid();
        await using (var rec = store.Begin(id))
        {
            Assert.Equal($"{id:N}.wav", rec.FileName);
            await rec.AppendAsync(new byte[3200]);
            await rec.AppendAsync(new byte[1600]);
            await rec.FinaliseAsync();
            Assert.Equal(4800, rec.DataBytes);
        }

        Assert.Equal(4800, WavFile.ReadPcm(store.PathOf($"{id:N}.wav")).Length);
    }

    [Fact]
    public async Task Pending_UnfinalisedAfterDispose_ListedAndReadable()
    {
        var store = Store();
        var rec = store.Begin(Guid.NewGuid());
        await rec.AppendAsync(new byte[3200]);
        // Simulates a crash: the stream goes away without FinaliseAsync fixing the header sizes.
        await rec.DisposeAsync();

        var name = Assert.Single(store.List());
        Assert.Equal(3200, WavFile.ReadPcm(store.PathOf(name)).Length);
    }

    [Fact]
    public void Prune_UsesCreationTime_ReturnsNames()
    {
        var store = Store();
        Directory.CreateDirectory(_root.Paths.PendingDir);
        var old = Path.Combine(_root.Paths.PendingDir, "old.wav");
        var fresh = Path.Combine(_root.Paths.PendingDir, "fresh.wav");
        File.WriteAllBytes(old, new byte[44]);
        File.WriteAllBytes(fresh, new byte[44]);
        File.SetCreationTimeUtc(old, _time.GetUtcNow().UtcDateTime.AddDays(-8));
        File.SetCreationTimeUtc(fresh, _time.GetUtcNow().UtcDateTime.AddDays(-6));

        Assert.Equal(["old.wav"], store.PruneOlderThan(TimeSpan.FromDays(7)));
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public async Task Delete_Idempotent()
    {
        var store = Store();
        var rec = store.Begin(Guid.NewGuid());
        await rec.DeleteAsync();
        await rec.DeleteAsync();
        Assert.Empty(store.List());
        store.Delete("never-existed.wav");
    }

    [Fact]
    public void PathOf_RejectsTraversal()
    {
        Assert.Throws<ArgumentException>(() => Store().PathOf(@"..\settings.json"));
    }
}
