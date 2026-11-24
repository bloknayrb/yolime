namespace Yolime.Core.Modes;

/// <summary>A spoken command at the start of a transcript.</summary>
/// <param name="Length">How many characters of the transcript it covers: any filler, the phrase and the punctuation and spaces after it.</param>
public sealed record CommandMatch(Mode Mode, int Length);

/// <summary>
/// Every hold is Dictate unless it starts with a spoken command ("Write a prompt. …", "Write a reply: …", "Raw. …").
/// Matched locally from a fixed list, English only, at the very start of the transcript, so ordinary dictation is
/// never re-routed by a word in the middle of it. Pure: no settings, no I/O.
/// </summary>
public static class ModeCommands
{
    /// <summary>Phrases of two or more words. Longest first, so "write me a prompt" wins over nothing shorter by accident.</summary>
    private static readonly (string Phrase, Mode Mode)[] Phrases = new (string, Mode)[]
    {
        ("write me a prompt", Mode.Prompt),
        ("write a prompt", Mode.Prompt),
        ("make this a prompt", Mode.Prompt),
        ("make a prompt", Mode.Prompt),
        ("prompt mode", Mode.Prompt),
        ("write me a reply", Mode.Reply),
        ("write a reply", Mode.Reply),
        ("draft a reply", Mode.Reply),
        ("write a response", Mode.Reply),
        ("draft a response", Mode.Reply),
        ("reply mode", Mode.Reply),
        ("raw mode", Mode.Raw),
        ("verbatim mode", Mode.Raw),
    }.OrderByDescending(p => p.Item1.Length).ToArray();

    /// <summary>One-word commands. They need a full stop, colon, dash or the end after them: "Reply all…" and "Raw, unfiltered…" are dictation.</summary>
    private static readonly (string Word, Mode Mode)[] Words =
    [
        ("prompt", Mode.Prompt),
        ("reply", Mode.Reply),
        ("raw", Mode.Raw),
        ("verbatim", Mode.Raw),
    ];

    /// <summary>At most one, and only before a phrase of two or more words.</summary>
    private static readonly string[] Fillers = ["okay", "ok", "um", "uh", "er", "so"];

    /// <summary>What is stripped after a command, with whitespace.</summary>
    private const string Separators = ".,:;!?…-–—";

    /// <summary>What may follow a phrase. Not a hyphen: "write a prompt-engineering guide" is dictation.</summary>
    private const string PhraseEnders = ".,:;!?…–—";

    /// <summary>What may follow a one-word command. Never a comma.</summary>
    private const string WordEnders = ".:!?…–—";

    private const string LeadingSkip = "\"'“”‘’…";

    /// <summary>The phrase help text and notices use for a mode.</summary>
    public static string Example(Mode mode) => mode switch
    {
        Mode.Prompt => "write a prompt",
        Mode.Reply => "write a reply",
        Mode.Raw => "raw",
        _ => "",
    };

    /// <summary>The command the transcript starts with, or null when it is plain dictation.</summary>
    public static CommandMatch? Match(string transcript)
    {
        if (string.IsNullOrEmpty(transcript)) return null;
        var start = SkipLeading(transcript, 0);

        foreach (var (phrase, mode) in Phrases)
            if (MatchPhrase(transcript, start, phrase, PhraseEnders, allowEnd: true) is { } end)
                return new CommandMatch(mode, SkipSeparators(transcript, end));

        foreach (var (word, mode) in Words)
            if (MatchPhrase(transcript, start, word, WordEnders, allowEnd: true) is { } end)
                return new CommandMatch(mode, SkipSeparators(transcript, end));

        foreach (var filler in Fillers)
        {
            if (MatchWord(transcript, start, filler) is not { } afterFiller) continue;
            if (afterFiller >= transcript.Length || transcript[afterFiller] is not (',' or '.')) continue;
            var next = SkipWhitespace(transcript, afterFiller + 1);
            foreach (var (phrase, mode) in Phrases)
                if (MatchPhrase(transcript, next, phrase, PhraseEnders, allowEnd: true) is { } end)
                    return new CommandMatch(mode, SkipSeparators(transcript, end));
        }
        return null;
    }

    /// <summary>
    /// The words after the command. The first letter is capitalised when the command took the start of the sentence,
    /// except in Raw (kept as heard) and before a word like "iPhone" whose second letter is already a capital.
    /// </summary>
    public static string Strip(string transcript, CommandMatch match)
    {
        var rest = transcript[Math.Min(match.Length, transcript.Length)..];
        if (match.Mode == Mode.Raw || rest.Length == 0 || !char.IsLower(rest[0])) return rest;
        if (rest.Length > 1 && char.IsUpper(rest[1])) return rest;
        return char.ToUpperInvariant(rest[0]) + rest[1..];
    }

    /// <summary>Matches and strips in one go; with no command the transcript comes back unchanged.</summary>
    public static (CommandMatch? Match, string Text) Split(string transcript) =>
        Match(transcript) is { } m ? (m, Strip(transcript, m)) : (null, transcript);

    /// <summary>
    /// The text a run of <paramref name="mode"/> should work on: the transcript without its command, but only when
    /// the command asked for that mode. A transcript re-run in another mode (a misheard command) keeps every word.
    /// </summary>
    public static string ContentFor(string transcript, Mode mode) =>
        Match(transcript) is { } m && m.Mode == mode ? Strip(transcript, m) : transcript;

    private static int SkipLeading(string s, int i)
    {
        while (i < s.Length && (char.IsWhiteSpace(s[i]) || LeadingSkip.Contains(s[i]))) i++;
        return i;
    }

    private static int SkipWhitespace(string s, int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return i;
    }

    private static int SkipSeparators(string s, int i)
    {
        while (i < s.Length && (char.IsWhiteSpace(s[i]) || Separators.Contains(s[i]))) i++;
        return i;
    }

    /// <summary>A single word at <paramref name="i"/>, followed by a non-letter; returns the index after it.</summary>
    private static int? MatchWord(string s, int i, string word)
    {
        if (i + word.Length > s.Length) return null;
        if (string.Compare(s, i, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) != 0) return null;
        var end = i + word.Length;
        return end < s.Length && char.IsLetterOrDigit(s[end]) ? null : end;
    }

    /// <summary>
    /// The phrase at <paramref name="i"/>, each space in it matching one or more whitespace characters, followed by
    /// the end (if allowed) or one of <paramref name="enders"/> (whitespace counts only for multi-word phrases).
    /// </summary>
    private static int? MatchPhrase(string s, int i, string phrase, string enders, bool allowEnd)
    {
        var p = 0;
        while (p < phrase.Length)
        {
            if (phrase[p] == ' ')
            {
                if (i >= s.Length || !char.IsWhiteSpace(s[i])) return null;
                i = SkipWhitespace(s, i);
                p++;
                continue;
            }
            if (i >= s.Length || char.ToLowerInvariant(s[i]) != phrase[p]) return null;
            i++;
            p++;
        }
        if (i == s.Length) return allowEnd ? i : null;
        var multiWord = phrase.Contains(' ');
        if (multiWord && char.IsWhiteSpace(s[i])) return i;
        return enders.Contains(s[i]) ? i : null;
    }
}
