using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Yolime.Core.Modes;

namespace Yolime.Core.Llm;

/// <summary>Spec: a short Dictate run with nothing for the model to fix skips the LLM and uses local cleanup.</summary>
public static partial class ShortDictation
{
    public const int MaxWords = 12;

    public static bool CanSkip(string raw) =>
        PromptBuilder.CountWords(raw) < MaxWords && !Cues().IsMatch(raw);

    // Fillers, self-corrections and spoken formatting: any of them means the model has work to do.
    [GeneratedRegex(@"(?i)\b(?:um+|uh+|er+|erm|hmm|like|you know|i mean|sort of|kind of|no wait|wait no|sorry|actually|scratch that|new ?line|new paragraph|bullet(?: point)?|comma|period|full stop|question mark|exclamation (?:mark|point)|colon|semicolon|slash|dash|hyphen|quote|open paren|close paren)\b")]
    private static partial Regex Cues();
}

/// <summary>The cleanup a skipped Dictate run gets. Code and terminal targets get their text as spoken.</summary>
public static partial class RegexCleanup
{
    public static string Apply(string raw, Profile profile)
    {
        var text = Spaces().Replace(raw.Trim(), " ");
        if (profile is Profile.Code or Profile.Terminal || text.Length == 0) return text;
        text = LoneI().Replace(text, "I");
        text = char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];
        if (char.IsLetterOrDigit(text[^1])) text += ".";
        return text;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\bi\b")]
    private static partial Regex LoneI();
}

/// <summary>Model output, made safe to paste: no invisible direction or zero-width characters, LF line ends, trimmed.</summary>
public static partial class OutputText
{
    public static string Normalise(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r') continue;
            if (char.IsHighSurrogate(c) && i + 1 < text.Length)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(text, i) != UnicodeCategory.Format) sb.Append(c).Append(text[i + 1]);
                i++;
                continue;
            }
            // The zero-width joiner holds emoji sequences together; every other format character goes.
            if (c != '\u200D' && CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format) continue;
            sb.Append(c);
        }
        return StripEcho(sb.ToString()).Trim();
    }

    /// <summary>
    /// Models sometimes repeat the request's header lines or tags before the answer (talkie 52f8eb0). Leading header
    /// lines and any of our tags are dropped; the same words later in the text are kept.
    /// </summary>
    private static string StripEcho(string text)
    {
        text = OurTag().Replace(text, "");
        var lines = text.TrimStart().Split('\n').ToList();
        while (lines.Count > 1 && HeaderLine().IsMatch(lines[0])) lines.RemoveAt(0);
        return string.Join('\n', lines);
    }

    // Our tags carry an 8-hex-digit nonce, so a word like <context-menu> is left alone.
    [GeneratedRegex(@"</?(?:transcript|context)-[0-9a-f]{8}\b[^>]*>\n?")]
    private static partial Regex OurTag();

    // Only the exact lines PromptBuilder writes: "Length: 20 cm" or "Profile: Sam's account" is the user's text.
    [GeneratedRegex(@"^(?:Profile: (?:neutral|casual|email|code|terminal)|Length: short; write one paragraph with no headings\.|Sections: (?:Task / Context / Requirements / Done when|Goal / Context / Constraints / Desired output)|Vocabulary \(spell exactly as written\): .*|Output goes into a terminal: write a single paragraph on one line\.)\s*$")]
    private static partial Regex HeaderLine();
}
