using System.Buffers.Binary;
using Yolime.Core.Settings;

namespace Yolime.Core.Tests.Design;

/// <summary>
/// The committed icons (tools/make-icons.py) hold the sizes Windows asks for, well-formed, and there is one
/// tray icon per tray state and taskbar theme.
/// </summary>
public class IconFileTests
{
    private static readonly int[] AppSizes = [16, 20, 24, 30, 32, 36, 40, 48, 64, 256];
    private static readonly int[] TraySizes = [16, 20, 24, 28, 32, 36, 40, 48];

    [Fact]
    public void AppIcon_HasEverySize()
    {
        var entries = Read(Path.Combine(Assets(), "yolime.ico"));
        Assert.Equal(AppSizes, entries.Select(e => e.Size).ToArray());
        AssertWellFormed(entries);
    }

    [Fact]
    public void TrayIcons_CoverEveryStateAndTaskbar()
    {
        var expected = Enum.GetValues<TrayIconState>()
            .SelectMany(s => new[] { "light", "dark" }.Select(v => $"tray-{FileState(s)}-{v}.ico"))
            .Order().ToArray();
        var actual = Directory.GetFiles(Assets(), "tray-*.ico").Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(expected, actual);
        foreach (var file in actual)
        {
            var entries = Read(Path.Combine(Assets(), file!));
            Assert.Equal(TraySizes, entries.Select(e => e.Size).ToArray());
            AssertWellFormed(entries);
        }
    }

    /// <summary>The state's name in the tray file names.</summary>
    public static string FileState(TrayIconState state) => state switch
    {
        TrayIconState.SetupNeeded => "setup",
        _ => state.ToString().ToLowerInvariant(),
    };

    private static void AssertWellFormed(List<Entry> entries)
    {
        foreach (var e in entries)
        {
            Assert.Equal(32, e.BitCount);
            if (e.Size >= 256)
            {
                // PNG signature, and the PNG's own width matches the directory entry.
                Assert.True(e.Data.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), $"{e.Size}px entry isn't a PNG");
                Assert.Equal(e.Size, BinaryPrimitives.ReadInt32BigEndian(e.Data.AsSpan(16, 4)));
            }
            else
            {
                // A BITMAPINFOHEADER at 32 bpp, height doubled for the AND mask, and room for pixels plus mask.
                Assert.Equal(40, BinaryPrimitives.ReadInt32LittleEndian(e.Data.AsSpan(0, 4)));
                Assert.Equal(e.Size, BinaryPrimitives.ReadInt32LittleEndian(e.Data.AsSpan(4, 4)));
                Assert.Equal(e.Size * 2, BinaryPrimitives.ReadInt32LittleEndian(e.Data.AsSpan(8, 4)));
                Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(e.Data.AsSpan(14, 2)));
                Assert.Equal(40 + e.Size * e.Size * 4 + (e.Size + 31) / 32 * 4 * e.Size, e.Data.Length);
            }
        }
    }

    private sealed record Entry(int Size, int BitCount, byte[] Data);

    private static List<Entry> Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0, 2)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2, 2)));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2));
        var list = new List<Entry>();
        for (var i = 0; i < count; i++)
        {
            var at = 6 + 16 * i;
            var size = bytes[at] == 0 ? 256 : bytes[at];
            var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 6, 2));
            var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 8, 4));
            var offset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 12, 4));
            list.Add(new Entry(size, bitCount, bytes.AsSpan(offset, length).ToArray()));
        }
        return list;
    }

    private static string Assets() => Path.Combine(RepoRoot.Find(), "src", "Yolime.App", "Assets");
}
