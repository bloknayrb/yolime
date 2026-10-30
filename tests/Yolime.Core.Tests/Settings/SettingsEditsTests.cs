using Yolime.Core.Settings;

namespace Yolime.Core.Tests.Settings;

public class SettingsEditsTests
{
    [Fact]
    public void Vocabulary_IsOneTermPerLine_Cleaned()
    {
        Assert.Equal(["yolime", "Kubernetes", "Bryan Kolb"],
            SettingsEdits.ParseVocabulary("  yolime \r\n\nKubernetes\nkubernetes\r\n Bryan Kolb\n"));
        Assert.Equal(SettingsEdits.MaxVocabularyTerms, SettingsEdits.ParseVocabulary(string.Join("\n", Enumerable.Range(0, 500).Select(i => "t" + i))).Count);
    }

    [Fact]
    public void AppModes_AreSetAndRemoved_WithoutTouchingTheOriginal()
    {
        var before = new YolimeSettings();
        var after = SettingsEdits.WithAppMode(before, " Slack.exe ", Mode.Reply);
        Assert.Equal("Reply", after.AppStartingModes["slack"]);
        Assert.Empty(before.AppStartingModes);
        Assert.Empty(SettingsEdits.WithAppMode(after, "SLACK", null).AppStartingModes);
        Assert.Same(before, SettingsEdits.WithAppMode(before, "  ", Mode.Raw));
    }

    [Fact]
    public void PreviewModes_Toggle_WithoutDuplicates()
    {
        var s = SettingsEdits.WithPreview(new YolimeSettings(), Mode.Prompt, true);
        s = SettingsEdits.WithPreview(s, Mode.Prompt, true);
        Assert.Equal(["Prompt"], s.PreviewModes);
        Assert.True(s.PreviewsAlways(Mode.Prompt));
        Assert.Empty(SettingsEdits.WithPreview(s, Mode.Prompt, false).PreviewModes);
    }
}
