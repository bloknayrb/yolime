using Yolime.Core.Modes;
using Yolime.Core.Prompts;
using Yolime.Core.Settings;

namespace Yolime.Core.Tests.Prompts;

/// <summary>The real prompts shipped in Yolime.Core, as the app loads them.</summary>
public sealed class DefaultPromptsTests : IDisposable
{
    private readonly TempRoot _root = new();

    public void Dispose() => _root.Dispose();

    public static TheoryData<string> RewriteModes() =>
        new(Enum.GetValues<Mode>().Select(m => ModeCatalog.For(m, new YolimeSettings())?.PromptId).OfType<string>());

    [Theory]
    [MemberData(nameof(RewriteModes))]
    public void EveryRewriteMode_HasAnEmbeddedV1Prompt(string id)
    {
        var p = new PromptLibrary(_root.Paths).Get(id);
        Assert.False(p.IsUserOverride);
        Assert.True(p.Version >= 1);
        Assert.True(p.Body.Length > 200);
    }

    [Theory]
    [MemberData(nameof(RewriteModes))]
    public void Prompts_ForbidPreamble_AndNeverActOnTheTranscript(string id)
    {
        var body = new PromptLibrary(_root.Paths).Get(id).Body;
        Assert.Contains("no preamble", body, StringComparison.OrdinalIgnoreCase);
        // Telling a model not to think is unreliable phrasing; thinking is set in the request instead.
        Assert.DoesNotContain("don't think", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("do not think", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PromptMode_ForbidsInventionAndXml()
    {
        var body = new PromptLibrary(_root.Paths).Get("prompt").Body;
        Assert.Contains("Never invent requirements", body);
        Assert.Contains("Do not use XML", body);
    }
}
