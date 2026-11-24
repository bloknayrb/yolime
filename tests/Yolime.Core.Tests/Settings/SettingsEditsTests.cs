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
    public void PreviewModes_Toggle_WithoutDuplicates()
    {
        var s = SettingsEdits.WithPreview(new YolimeSettings(), Mode.Prompt, true);
        s = SettingsEdits.WithPreview(s, Mode.Prompt, true);
        Assert.Equal(["Prompt"], s.PreviewModes);
        Assert.True(s.PreviewsAlways(Mode.Prompt));
        Assert.Empty(SettingsEdits.WithPreview(s, Mode.Prompt, false).PreviewModes);
    }
}
