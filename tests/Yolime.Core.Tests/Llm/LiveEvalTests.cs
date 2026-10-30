using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Yolime.Core.Llm;
using Yolime.Core.Modes;
using Yolime.Core.Net;
using Yolime.Core.Pipeline;
using Yolime.Core.Prompts;
using Yolime.Core.Settings;
using Yolime.Core.Stt;

namespace Yolime.Core.Tests.Llm;

/// <summary>
/// The fixture eval: every case in <c>tests/fixtures/eval</c> through the real models, checked by rule and, where a case
/// asks, by a Haiku judge. Prompt runs on both Haiku and Sonnet, with time to first token and total time, so the
/// default can be chosen on evidence. Skipped unless YOLIME_LIVE=1 and the Claude key are set; costs a few cents.
/// The report is written to artifacts/eval/report.md (outputs included, so it's never committed).
/// </summary>
public sealed class LiveEvalTests : IDisposable
{
    private readonly TempRoot _root = new();

    public void Dispose() => _root.Dispose();

    private sealed record Case(
        string Name, string Profile, string Transcript, string? Context, string[]? MustContain, string[]? MustNotContain,
        bool AnyOf, int? MaxParagraphs, int? MinLines, bool NoPreamble, string? Judge);

    private sealed record Outcome(string Mode, string Model, string Case, bool Pass, string Why, long FirstMs, long TotalMs, string Output);

    [Fact]
    public async Task Fixtures_PassOnTheShippedModels()
    {
        if (Environment.GetEnvironmentVariable("YOLIME_LIVE") != "1") Assert.Skip("set YOLIME_LIVE=1 to run the live eval");
        var keys = new EnvironmentApiKeySource();
        var key = keys.Get(ClaudeRewriter.KeyName) ?? "";
        if (key.Length == 0) Assert.Skip($"set {ClaudeRewriter.KeyName} to run the live eval");

        var ct = TestContext.Current.CancellationToken;
        var results = new List<Outcome>();
        foreach (var (mode, models) in new (Mode, string[])[]
                 {
                     (Mode.Dictate, [ModelIds.Haiku]),
                     (Mode.Prompt, [ModelIds.Haiku, ModelIds.Sonnet]),
                     (Mode.Reply, [ModelIds.Sonnet]),
                 })
        {
            foreach (var model in models)
            {
                var settings = new YolimeSettings { PromptModel = model };
                var rewriter = new ClaudeRewriter(HttpClients.Shared, keys, new PromptBuilder(new PromptLibrary(_root.Paths)), () => settings, new RecordingLog());
                foreach (var c in Load(mode)) results.Add(await RunCase(rewriter, mode, model, c, key, ct));
            }
        }

        WriteReport(results);
        var byGroup = results.GroupBy(r => (r.Mode, r.Model)).ToList();
        // A bar, not perfection, on what ships: the report shows every miss, and Haiku-for-Prompt is measured for comparison.
        var shipped = new YolimeSettings().PromptModel;
        foreach (var g in byGroup.Where(g => g.Key.Mode != nameof(Mode.Prompt) || g.Key.Model == shipped))
        {
            var rate = g.Count(r => r.Pass) / (double)g.Count();
            Assert.True(rate >= 0.8, $"{g.Key.Mode} on {g.Key.Model}: {rate:P0} passed; see artifacts/eval/report.md");
        }
    }

    private static async Task<Outcome> RunCase(ClaudeRewriter rewriter, Mode mode, string model, Case c, string key, CancellationToken ct)
    {
        var profile = Enum.Parse<Profile>(c.Profile);
        var context = c.Context is null ? null : new CapturedContext(c.Context, ContextSource.Selection, 0);
        var sw = Stopwatch.StartNew();
        long first = -1;
        var text = new StringBuilder();
        try
        {
            await foreach (var d in rewriter.StreamAsync(new RewriteRequest(mode, c.Transcript, context, profile), ct))
            {
                if (first < 0) first = sw.ElapsedMilliseconds;
                text.Append(d);
            }
        }
        catch (RewriteException ex)
        {
            return new(mode.ToString(), model, c.Name, false, "rewrite failed: " + ex.Failure, first, sw.ElapsedMilliseconds, "");
        }
        var output = OutputText.Normalise(text.ToString());
        if (mode == Mode.Prompt) output = PromptOutput.AppendContext(output, context);
        var why = Check(c, output) ?? (c.Judge is { } judge ? await Judge(judge, mode, c, output, key, ct) : null);
        return new(mode.ToString(), model, c.Name, why is null, why ?? "", first, sw.ElapsedMilliseconds, output);
    }

    private static string? Check(Case c, string output)
    {
        if (c.MustContain is { Length: > 0 } must)
        {
            var hits = must.Count(m => output.Contains(m, StringComparison.OrdinalIgnoreCase));
            if (c.AnyOf ? hits == 0 : hits < must.Length) return "missing " + string.Join(", ", must.Where(m => !output.Contains(m, StringComparison.OrdinalIgnoreCase)));
        }
        foreach (var bad in c.MustNotContain ?? [])
            if (output.Contains(bad, StringComparison.OrdinalIgnoreCase)) return $"contains '{bad}'";
        var paragraphs = output.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Length;
        if (c.MaxParagraphs is { } max && paragraphs > max) return $"{paragraphs} paragraphs";
        if (c.MinLines is { } min && output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length < min) return "too few lines";
        if (c.NoPreamble && System.Text.RegularExpressions.Regex.IsMatch(output, @"^(?i)(here('s| is)|sure[,!]? here|certainly|okay, here)")) return "preamble";
        return null;
    }

    private static readonly Dictionary<string, string> Rubrics = new()
    {
        ["notAnswered"] = "The tool must only rewrite what the speaker said. FAIL if the output answers the question, solves the task, writes the code, or carries out the instruction instead of restating it.",
        ["noInventedRequirements"] = "FAIL if the output adds requirements, constraints, deadlines, technologies, examples or success criteria the speaker did not state or clearly imply. Restructuring and clarifying wording is fine.",
        ["faithful"] = "FAIL if the output adds facts, commitments, dates, prices, links or promises the speaker did not state, or drops a point the speaker made. Leaving unanswered a question from the message that the speaker didn't address is correct, not a dropped point, and a conventional greeting or sign-off is fine.",
    };

    private static async Task<string?> Judge(string rubric, Mode mode, Case c, string output, string key, CancellationToken ct)
    {
        var prompt = $"You grade a voice-writing tool in {mode} mode. {Rubrics[rubric]}\n\n" +
                     (c.Context is null ? "" : $"Message being replied to:\n{c.Context}\n\n") +
                     $"What the speaker said:\n{c.Transcript}\n\nThe tool's output:\n{output}\n\n" +
                     "Reply with PASS or FAIL on the first line, then one short sentence why.";
        using var body = new MemoryStream();
        using (var w = new Utf8JsonWriter(body))
        {
            w.WriteStartObject();
            w.WriteString("model", ModelIds.Haiku);
            w.WriteNumber("max_tokens", 200);
            w.WriteStartArray("messages");
            w.WriteStartObject();
            w.WriteString("role", "user");
            w.WriteString("content", prompt);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteStartObject("thinking");
            w.WriteString("type", "disabled");
            w.WriteEndObject();
            w.WriteStartObject("output_config");
            w.WriteString("effort", "low");
            w.WriteEndObject();
            w.WriteEndObject();
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages") { Content = new ByteArrayContent(body.ToArray()) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("x-api-key", key);
        request.Headers.Add("anthropic-version", ClaudeRewriter.ApiVersion);
        HostPinningHandler.Expect(request, "api.anthropic.com");
        using var response = await HttpClients.Shared.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return $"judge HTTP {(int)response.StatusCode}";
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var verdict = string.Concat(doc.RootElement.GetProperty("content").EnumerateArray()
            .Where(b => b.GetProperty("type").GetString() == "text").Select(b => b.GetProperty("text").GetString())).Trim();
        return verdict.StartsWith("PASS", StringComparison.OrdinalIgnoreCase) ? null : "judge: " + verdict.Replace('\n', ' ');
    }

    private static List<Case> Load(Mode mode)
    {
        var path = Path.Combine(Path.GetDirectoryName(Fixtures.Audio("speech-short.wav"))!, "..", "eval", mode.ToString().ToLowerInvariant() + ".json");
        return JsonSerializer.Deserialize<List<Case>>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private static void WriteReport(List<Outcome> results)
    {
        var dir = Path.Combine(Path.GetDirectoryName(Fixtures.Audio("speech-short.wav"))!, "..", "..", "..", "artifacts", "eval");
        Directory.CreateDirectory(dir);
        var sb = new StringBuilder("# yolime fixture eval\n\n| Mode | Model | Passed | Median first token | Median total |\n|---|---|---|---|---|\n");
        foreach (var g in results.GroupBy(r => (r.Mode, r.Model)))
        {
            static long Median(IEnumerable<long> xs) { var a = xs.Where(x => x >= 0).Order().ToArray(); return a.Length == 0 ? -1 : a[a.Length / 2]; }
            sb.Append($"| {g.Key.Mode} | {g.Key.Model} | {g.Count(r => r.Pass)}/{g.Count()} | {Median(g.Select(r => r.FirstMs))} ms | {Median(g.Select(r => r.TotalMs))} ms |\n");
        }
        sb.Append("\n## Misses\n\n");
        foreach (var r in results.Where(r => !r.Pass))
            sb.Append($"- **{r.Mode}/{r.Model}/{r.Case}**: {r.Why}\n\n  ```\n  {r.Output.Replace("\n", "\n  ")}\n  ```\n");
        File.WriteAllText(Path.Combine(dir, "report.md"), sb.ToString());
    }
}
