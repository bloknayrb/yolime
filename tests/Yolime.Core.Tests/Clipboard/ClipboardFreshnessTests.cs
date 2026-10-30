using Microsoft.Extensions.Time.Testing;
using Yolime.Core.Clipboard;

namespace Yolime.Core.Tests.Clipboard;

public class ClipboardFreshnessTests
{
    private readonly FakeTimeProvider _time = new();
    private ClipboardFreshness Fresh() => new(_time);

    [Fact]
    public void UserCopy_IsFreshFor120s_ThenStale()
    {
        var f = Fresh();
        f.OnChanged(5);
        _time.Advance(TimeSpan.FromSeconds(119));
        Assert.Equal(119_000, f.UserCopyAgeMs(5));
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(f.UserCopyAgeMs(5));
    }

    [Fact]
    public void ContentPresentAtStartup_HasUnknownAge_AndIsStale() => Assert.Null(Fresh().UserCopyAgeMs(1));

    [Fact]
    public void OurOwnPaste_IsNeverAUserCopy_EvenIfTheUpdateArrivesFirst()
    {
        var f = Fresh();
        f.OnChanged(7); // WM_CLIPBOARDUPDATE can beat SetTextAsync's return
        f.MarkOwn(7);
        Assert.Null(f.UserCopyAgeMs(7));
        f.OnChanged(8);
        f.MarkOwn(8);
        Assert.Null(f.UserCopyAgeMs(8));
    }

    [Fact]
    public void ARestore_InheritsTheOriginalCopysAge()
    {
        var f = Fresh();
        f.OnChanged(10); // the user copies
        _time.Advance(TimeSpan.FromSeconds(30));
        f.MarkOwn(11); // our paste
        f.OnChanged(11);
        _time.Advance(TimeSpan.FromSeconds(1));
        f.OnChanged(12); // our restore puts the user's copy back
        f.MarkRestored(12);
        Assert.Equal(31_000, f.UserCopyAgeMs(12));
        _time.Advance(TimeSpan.FromSeconds(90));
        Assert.Null(f.UserCopyAgeMs(12));
    }

    [Fact]
    public void ARestoreOfStartupContent_StaysStale()
    {
        var f = Fresh();
        f.MarkOwn(2);
        f.MarkRestored(3);
        Assert.Null(f.UserCopyAgeMs(3));
    }

    [Fact]
    public void ACopyFallback_IsOurs_NotTheUsers()
    {
        var f = Fresh();
        f.OnChanged(20);
        f.MarkOwn(21); // "Copied, press Ctrl+V"
        f.OnChanged(21);
        Assert.Null(f.UserCopyAgeMs(21));
    }

    [Fact]
    public void ASequenceNeverSeen_IsUnknown()
    {
        var f = Fresh();
        f.OnChanged(30);
        Assert.Null(f.UserCopyAgeMs(31)); // a change the listener missed
    }
}
