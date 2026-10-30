using Yolime.Core.Modes;
using Yolime.Core.Output;
using Yolime.Core.Settings;

namespace Yolime.Core.Tests.Modes;

public class ModeCatalogTests
{
    [Fact]
    public void Dictate_IsHaiku_ThinkingOff_LowEffort_PastesOnFallback()
    {
        var d = ModeCatalog.For(Mode.Dictate, new YolimeSettings())!;
        Assert.Equal(ModelIds.Haiku, d.Model);
        Assert.Equal(Thinking.Disabled, d.Thinking);
        Assert.Equal(Effort.Low, d.Effort);
        Assert.Equal("dictate", d.PromptId);
        Assert.False(d.UsesContext);
        Assert.Equal(DeliveryIntent.Paste, d.FallbackIntent);
        Assert.Equal(TimeSpan.FromSeconds(5), d.FirstTokenTimeout);
    }

    [Fact]
    public void Prompt_UsesTheModelSetting_WithContext_ClipboardOnFallback()
    {
        var d = ModeCatalog.For(Mode.Prompt, new YolimeSettings())!;
        Assert.Equal(ModelIds.Sonnet, d.Model); // the eval's pick
        Assert.Equal(Thinking.BetweenTools, d.Thinking);
        Assert.Equal(Thinking.Disabled, ModeCatalog.For(Mode.Prompt, new YolimeSettings { PromptModel = ModelIds.Haiku })!.Thinking);
        Assert.True(d.UsesContext);
        Assert.Equal(DeliveryIntent.ClipboardOnly, d.FallbackIntent);
        Assert.Equal(TimeSpan.FromSeconds(8), d.FirstTokenTimeout);
    }

    [Fact]
    public void PromptOnSonnet_GetsSonnetsThinking_NotDisabled()
    {
        // Sonnet 5.5 rejects thinking "disabled" with a 400: thinking follows the model, never the mode.
        var d = ModeCatalog.For(Mode.Prompt, new YolimeSettings { PromptModel = ModelIds.Sonnet })!;
        Assert.Equal(ModelIds.Sonnet, d.Model);
        Assert.Equal(Thinking.BetweenTools, d.Thinking);
    }

    [Fact]
    public void Reply_IsSonnet_BetweenTools_WithContext()
    {
        var d = ModeCatalog.For(Mode.Reply, new YolimeSettings())!;
        Assert.Equal(ModelIds.Sonnet, d.Model);
        Assert.Equal(Thinking.BetweenTools, d.Thinking);
        Assert.Equal(Effort.Low, d.Effort);
        Assert.True(d.UsesContext);
        Assert.Equal(DeliveryIntent.ClipboardOnly, d.FallbackIntent);
    }

    [Fact]
    public void Raw_HasNoRewrite() => Assert.Null(ModeCatalog.For(Mode.Raw, new YolimeSettings()));

    [Theory]
    [InlineData("claude-haiku-5-5", Thinking.Disabled)]
    [InlineData("claude-sonnet-5-5", Thinking.BetweenTools)]
    [InlineData("claude-opus-5-5", Thinking.Omit)]
    public void ThinkingFor_Model(string model, Thinking expected) => Assert.Equal(expected, ModeCatalog.ThinkingFor(model));

    [Fact]
    public void EveryRewriteMode_HasPositiveLimits()
    {
        foreach (var mode in Enum.GetValues<Mode>())
        {
            if (ModeCatalog.For(mode, new YolimeSettings()) is not { } d) continue;
            Assert.InRange(d.MaxTokens, 256, 8192);
            Assert.True(d.FirstTokenTimeout > TimeSpan.Zero);
        }
    }
}
