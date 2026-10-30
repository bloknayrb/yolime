using System.Reflection;
using System.Text.RegularExpressions;

namespace Yolime.Core.Prompts;

public sealed record PromptFile(string Id, int Version, string Body, bool IsUserOverride);

/// <summary>
/// System prompts: versioned defaults embedded as <c>yolime.prompts.&lt;id&gt;.v&lt;N&gt;.md</c>, overridable by
/// <c>prompts\&lt;id&gt;.md</c> in the roaming folder. An override's optional first line
/// <c>&lt;!-- version: N --&gt;</c> records which default it was based on.
/// </summary>
public sealed partial class PromptLibrary
{
    private const int KeepBackups = 5;
    private readonly AppPaths _paths;
    private readonly Assembly _resources;
    private readonly TimeProvider _time;

    public PromptLibrary(AppPaths paths)
        : this(paths, typeof(PromptLibrary).Assembly, TimeProvider.System)
    {
    }

    internal PromptLibrary(AppPaths paths, Assembly resources, TimeProvider time)
    {
        _paths = paths;
        _resources = resources;
        _time = time;
    }

    public PromptFile Get(string id)
    {
        var overridePath = OverridePath(id);
        if (File.Exists(overridePath))
        {
            var (version, body) = Split(File.ReadAllText(overridePath));
            return new PromptFile(id, version, body, IsUserOverride: true);
        }
        var (v, name) = LatestDefault(id) ?? throw new KeyNotFoundException($"no prompt with id {id}");
        using var stream = _resources.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return new PromptFile(id, v, Split(reader.ReadToEnd()).Body, IsUserOverride: false);
    }

    /// <summary>
    /// True when an override was based on an older default. An override with no version line was written from
    /// scratch, so it never triggers the notice.
    /// </summary>
    public bool NewerDefaultExists(string id)
    {
        var current = Get(id);
        return current.IsUserOverride && current.Version >= 1 && LatestDefault(id) is { } latest && latest.Version > current.Version;
    }

    /// <summary>
    /// The override file for editing: created from the current default, stamped with its version so a newer default
    /// can be flagged later. An existing override is left as it is.
    /// </summary>
    public string CreateOverride(string id)
    {
        var path = OverridePath(id);
        if (File.Exists(path)) return path;
        var current = Get(id);
        Directory.CreateDirectory(_paths.PromptsDir);
        File.WriteAllText(path, $"<!-- version: {current.Version} -->\n" + current.Body);
        return path;
    }

    /// <summary>Moves the override aside (never deletes it) so the embedded default applies again.</summary>
    public void ResetToDefault(string id)
    {
        var path = OverridePath(id);
        if (!File.Exists(path)) return;
        var backup = $"{path}.bak-{_time.GetUtcNow().UtcDateTime:yyyyMMddHHmmss}";
        var target = backup;
        for (var i = 1; File.Exists(target); i++) target = $"{backup}-{i}";
        File.Move(path, target);

        var all = Directory.GetFiles(_paths.PromptsDir, $"{id}.md.bak-*").Order(StringComparer.Ordinal).ToList();
        foreach (var old in all.Take(Math.Max(0, all.Count - KeepBackups)))
        {
            try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private string OverridePath(string id)
    {
        if (!IdPattern().IsMatch(id)) throw new ArgumentException("invalid prompt id", nameof(id));
        return Path.Combine(_paths.PromptsDir, id + ".md");
    }

    private (int Version, string Name)? LatestDefault(string id)
    {
        var prefix = $"yolime.prompts.{id}.v";
        (int, string)? best = null;
        foreach (var name in _resources.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(".md", StringComparison.Ordinal)) continue;
            if (int.TryParse(name[prefix.Length..^3], out var v) && (best is null || v > best.Value.Item1)) best = (v, name);
        }
        return best;
    }

    private static (int Version, string Body) Split(string text)
    {
        var m = VersionLine().Match(text);
        return m.Success ? (int.Parse(m.Groups[1].Value), text[m.Length..]) : (0, text);
    }

    [GeneratedRegex(@"^<!--\s*version:\s*(\d+)\s*-->\r?\n")]
    private static partial Regex VersionLine();

    [GeneratedRegex("^[a-z][a-z0-9-]*$")]
    private static partial Regex IdPattern();
}
