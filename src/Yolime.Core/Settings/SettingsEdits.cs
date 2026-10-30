namespace Yolime.Core.Settings;

/// <summary>
/// The edits Settings' pages make, as pure functions: each returns a new settings value with fresh collections, so
/// the record held by <see cref="SettingsHolder"/> is never mutated in place.
/// </summary>
public static class SettingsEdits
{
    public const int MaxVocabularyTerms = 200;

    /// <summary>One term per line: trimmed, blanks dropped, duplicates (ignoring case) removed, order kept.</summary>
    public static IReadOnlyList<string> ParseVocabulary(string text) =>
        text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxVocabularyTerms)
            .ToList();

    public static string VocabularyText(YolimeSettings s) => string.Join("\n", s.Vocabulary);

    /// <summary>
    /// Longest term History's "Add word" accepts: a name or a phrase, not a sentence. Every term goes out with each
    /// transcription request, so a selected paragraph mustn't end up there.
    /// </summary>
    public const int MaxTermLength = 48;

    /// <summary>
    /// Adds one term from History: invisible characters stripped, surrounding punctuation and quotes trimmed
    /// ("Kolb," becomes "Kolb"), then the same dedupe and cap as <see cref="ParseVocabulary"/>.
    /// </summary>
    public static (YolimeSettings Settings, VocabularyAdd Result) AddVocabulary(YolimeSettings s, string typed)
    {
        var cleaned = Clipboard.ContextFilter.StripInvisible(typed);
        if (cleaned.Contains('\n') || cleaned.Contains('\r')) return (s, VocabularyAdd.MultiLine);
        var term = cleaned.Trim().Trim(Trimmable).Trim();
        if (term.Length == 0) return (s, VocabularyAdd.Empty);
        if (term.Length > MaxTermLength) return (s, VocabularyAdd.TooLong);
        if (s.Vocabulary.Contains(term, StringComparer.OrdinalIgnoreCase)) return (s, VocabularyAdd.AlreadyThere);
        if (s.Vocabulary.Count >= MaxVocabularyTerms) return (s, VocabularyAdd.Full);
        return (s with { Vocabulary = ParseVocabulary(SettingsEdits.VocabularyText(s) + "\n" + term) }, VocabularyAdd.Added);
    }

    private static readonly char[] Trimmable = ['.', ',', ';', ':', '!', '?', '"', '\'', '(', ')', '[', ']', '{', '}', '\u201c', '\u201d', '\u2018', '\u2019', ' ', '\t'];

    /// <summary>
    /// Sets an app's starting mode. The name is a process name ("slack" or "slack.exe"); a null mode removes it.
    /// </summary>
    public static YolimeSettings WithAppMode(YolimeSettings s, string processName, Mode? mode)
    {
        var name = processName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        var map = new Dictionary<string, string>(s.AppStartingModes, StringComparer.OrdinalIgnoreCase);
        if (name.Length == 0) return s;
        if (mode is { } m) map[name] = m.ToString();
        else map.Remove(name);
        return s with { AppStartingModes = map };
    }

    public static YolimeSettings WithPreview(YolimeSettings s, Mode mode, bool on)
    {
        var list = s.PreviewModes.Where(m => !string.Equals(m, mode.ToString(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (on) list.Add(mode.ToString());
        return s with { PreviewModes = list };
    }
}

public enum VocabularyAdd { Added, AlreadyThere, Empty, TooLong, MultiLine, Full }
