using System.Text;
using Yolime.Core.Llm;
using Yolime.Core.Modes;
using Yolime.Core.Net;
using Yolime.Core.Prompts;
using Yolime.Core.Settings;
using Yolime.Core.Stt;

namespace Yolime.Core.Tests.Llm;

/// <summary>
/// Real Claude calls, one per model, so a request shape the API rejects shows up as a failure here, not in use.
/// Skipped unless YOLIME_LIVE=1 and YOLIME_ANTHROPIC_API_KEY are set; costs well under a cent per run.
/// </summary>
public sealed class LiveRewriteSmokeTests : IDisposable
{
    private readonly TempRoot _root = new();

    public void Dispose() => _root.Dispose();

    [Theory]
    [InlineData(Mode.Dictate, ModelIds.Haiku)]
    [InlineData(Mode.Prompt, ModelIds.Sonnet)]
    [InlineData(Mode.Reply, ModelIds.Sonnet)]
    public async Task EachModelAndShape_IsAccepted(Mode mode, string model)
    {
        if (Environment.GetEnvironmentVariable("YOLIME_LIVE") != "1") Assert.Skip("set YOLIME_LIVE=1 to call the real Claude API");
        var keys = new EnvironmentApiKeySource();
        if (keys.Get(ClaudeRewriter.KeyName) is null) Assert.Skip($"set {ClaudeRewriter.KeyName} to call the real Claude API");

        var settings = new YolimeSettings { PromptModel = model };
        var rewriter = new ClaudeRewriter(HttpClients.Shared, keys, new PromptBuilder(new PromptLibrary(_root.Paths)), () => settings, new RecordingLog());
        var context = mode == Mode.Reply ? new Yolime.Core.Pipeline.CapturedContext("Are we still on for lunch Friday?", Yolime.Core.Pipeline.ContextSource.Selection, 0) : null;
        var request = new RewriteRequest(mode, "um yeah so tell them uh friday works for me at noon", context, Profile.Casual);

        var text = new StringBuilder();
        await foreach (var d in rewriter.StreamAsync(request, TestContext.Current.CancellationToken)) text.Append(d);
        Assert.Contains("noon", text.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
