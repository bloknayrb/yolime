using Microsoft.Extensions.Time.Testing;
using Yolime.Core.Prompts;

namespace Yolime.Core.Tests.Prompts;

public sealed class PromptLibraryTests : IDisposable
{
    private readonly TempRoot _root = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _root.Dispose();

    private PromptLibrary Library() => new(_root.Paths, typeof(PromptLibraryTests).Assembly, _time);

    private void Override(string text)
    {
        Directory.CreateDirectory(_root.Paths.PromptsDir);
        File.WriteAllText(Path.Combine(_root.Paths.PromptsDir, "sample.md"), text);
    }

    [Fact]
    public void CreateOverride_CopiesTheDefault_WithItsVersion_AndKeepsAnExistingOne()
    {
        var lib = Library();
        var path = lib.CreateOverride("sample");
        Assert.Equal(Path.Combine(_root.Paths.PromptsDir, "sample.md"), path);
        var p = lib.Get("sample");
        Assert.True(p.IsUserOverride);
        Assert.Equal(2, p.Version);
        Assert.Equal("Sample prompt v2.\n", p.Body);
        Assert.False(lib.NewerDefaultExists("sample"));

        File.WriteAllText(path, "<!-- version: 2 -->\nmine\n");
        lib.CreateOverride("sample");
        Assert.Equal("mine\n", lib.Get("sample").Body);
    }

    [Fact]
    public void Get_NoOverride_ReturnsHighestEmbedded()
    {
        var p = Library().Get("sample");
        Assert.Equal(2, p.Version);
        Assert.Equal("Sample prompt v2.\n", p.Body);
        Assert.False(p.IsUserOverride);
    }

    [Fact]
    public void Get_Override_Wins()
    {
        Override("<!-- version: 1 -->\nMine.\n");
        var p = Library().Get("sample");
        Assert.True(p.IsUserOverride);
        Assert.Equal("Mine.\n", p.Body);
        Assert.Equal(1, p.Version);
    }

    [Fact]
    public void NewerDefaultExists_OverrideV1_DefaultV2_True()
    {
        Override("<!-- version: 1 -->\nMine.\n");
        Assert.True(Library().NewerDefaultExists("sample"));
    }

    [Fact]
    public void NewerDefaultExists_UnversionedOverride_False()
    {
        Override("Written from scratch.\n");
        var lib = Library();
        Assert.Equal(0, lib.Get("sample").Version);
        Assert.False(lib.NewerDefaultExists("sample"));
    }

    [Fact]
    public void Reset_RenamesNotDeletes_Keeps5()
    {
        var lib = Library();
        for (var i = 0; i < 7; i++)
        {
            Override($"edit {i}\n");
            lib.ResetToDefault("sample");
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.False(File.Exists(Path.Combine(_root.Paths.PromptsDir, "sample.md")));
        var backups = Directory.GetFiles(_root.Paths.PromptsDir, "sample.md.bak-*").Select(File.ReadAllText).Order().ToArray();
        Assert.Equal(["edit 2\n", "edit 3\n", "edit 4\n", "edit 5\n", "edit 6\n"], backups);
        Assert.False(lib.Get("sample").IsUserOverride);
    }

    [Fact]
    public void Get_UnknownId_ThrowsKeyNotFound() => Assert.Throws<KeyNotFoundException>(() => Library().Get("nope"));
}
