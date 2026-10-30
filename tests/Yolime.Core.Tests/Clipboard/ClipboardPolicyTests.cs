using Yolime.Core.Clipboard;

namespace Yolime.Core.Tests.Clipboard;

public class ClipboardPolicyTests
{
    private static ClipFormat F(uint id, string? name = null) => new(id, name);

    [Fact]
    public void Saveable_KeepsTextImageFilesHtml()
    {
        var present = new[] { F(13), F(17), F(15), F(16), F(0xC100, "HTML Format"), F(0xC101, "PNG"), F(0xC102, "Rich Text Format") };
        Assert.Equal(present, ClipboardPolicy.Saveable(present));
    }

    [Fact]
    public void Saveable_DropsSynthesizedAndHandleFormats()
    {
        var present = new[] { F(1), F(7), F(2), F(14), F(3), F(9), F(0x80), F(0x250), F(0x320), F(13), F(0xC200, "Embed Source"), F(0xC201, "Ole Private Data"), F(0xC202, "FileGroupDescriptorW") };
        Assert.Equal([F(13)], ClipboardPolicy.Saveable(present));
    }

    [Fact]
    public void Saveable_KeepsOtherRegisteredFormats_SoCutAndExcelSurvive()
    {
        var present = new[] { F(15), F(0xC300, "Preferred DropEffect"), F(0xC301, "Biff12"), F(0xC302, "XML Spreadsheet") };
        Assert.Equal(present, ClipboardPolicy.Saveable(present));
    }

    [Fact]
    public void Saveable_PrefersDibV5()
    {
        Assert.Equal([F(17)], ClipboardPolicy.Saveable([F(8), F(17)]));
        Assert.Equal([F(8)], ClipboardPolicy.Saveable([F(8)]));
    }

    [Fact]
    public void Sensitive_Detected()
    {
        Assert.True(ClipboardPolicy.IsSensitive([F(13), F(0xC300, "ExcludeClipboardContentFromMonitorProcessing")]));
        Assert.True(ClipboardPolicy.IsSensitive([F(0xC301, "Clipboard Viewer Ignore")]));
        Assert.False(ClipboardPolicy.IsSensitive([F(13)]));
    }

    [Fact]
    public void Exclusions_AlwaysBlockCloud_HistoryOnlyWhenAsked()
    {
        Assert.Contains("CanUploadToCloudClipboard", ClipboardPolicy.ExclusionFormats(excludeFromHistory: false));
        Assert.DoesNotContain("CanIncludeInClipboardHistory", ClipboardPolicy.ExclusionFormats(excludeFromHistory: false));
        var all = ClipboardPolicy.ExclusionFormats(excludeFromHistory: true);
        Assert.Contains("CanIncludeInClipboardHistory", all);
        Assert.Contains("ExcludeClipboardContentFromMonitorProcessing", all);
        Assert.Contains("Clipboard Viewer Ignore", all);
    }

    [Fact]
    public void Restore_OnlyWhenSequenceStillOurs()
    {
        Assert.True(ClipboardPolicy.ShouldRestore(41, 41));
        Assert.False(ClipboardPolicy.ShouldRestore(41, 42));
        Assert.Equal(TimeSpan.FromMilliseconds(800), ClipboardPolicy.RestoreDelay);
    }

    [Fact]
    public void RetryDelay_BacksOffAndCaps()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(10), ClipboardPolicy.OpenRetryDelay(0));
        Assert.Equal(TimeSpan.FromMilliseconds(80), ClipboardPolicy.OpenRetryDelay(3));
        Assert.Equal(TimeSpan.FromMilliseconds(200), ClipboardPolicy.OpenRetryDelay(9));
        Assert.Equal(10, ClipboardPolicy.MaxOpenAttempts);
    }
}
