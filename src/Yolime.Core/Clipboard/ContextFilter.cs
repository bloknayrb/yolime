using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Yolime.Core.Pipeline;

namespace Yolime.Core.Clipboard;

public enum ContextVerdict { Accepted, Redacted, Rejected }

/// <param name="Text">Empty when <see cref="Verdict"/> is <see cref="ContextVerdict.Rejected"/>.</param>
public sealed record FilteredContext(string Text, ContextVerdict Verdict);

/// <summary>
/// Cleans reply/prompt context before it reaches the model. Invisible characters that can hide instructions are
/// stripped. A secret in a selection is redacted, because the user pointed at that text on purpose. A secret on the
/// clipboard rejects the whole clipboard: it was probably copied for somewhere else.
/// </summary>
public static partial class ContextFilter
{
    private const string Redacted = "[redacted]";

    /// <returns>Null when nothing readable is left.</returns>
    public static FilteredContext? Apply(string raw, ContextSource source, bool sensitiveMarkers)
    {
        var text = Clean(raw);
        if (text.Trim().Length == 0) return null;
        if (sensitiveMarkers && source == ContextSource.Clipboard) return new("", ContextVerdict.Rejected);

        var redacted = Redact(text, out var found);
        if (!found) return new(text, ContextVerdict.Accepted);
        return source == ContextSource.Clipboard ? new("", ContextVerdict.Rejected) : new(redacted, ContextVerdict.Redacted);
    }

    /// <summary>
    /// Only the invisible characters go (format characters except the emoji joiner, other controls but newline and
    /// tab); everything visible stays exactly as written. For text attached verbatim, like Prompt's selection.
    /// </summary>
    public static string StripInvisible(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (char.IsHighSurrogate(c) && i + 1 < raw.Length && char.IsLowSurrogate(raw[i + 1]))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(raw, i) != UnicodeCategory.Format) sb.Append(c).Append(raw[i + 1]);
                i++;
                continue;
            }
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.Format && c != '\u200D') continue;
            if (category == UnicodeCategory.Control && c is not '\n' and not '\t' and not '\r') continue;
            if (category == UnicodeCategory.Surrogate) continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Drops format characters (bidi controls, zero-width, tag characters) and other controls, then NFKC.</summary>
    private static string Clean(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c == '\r') continue;
            if (char.IsHighSurrogate(c) && i + 1 < raw.Length && char.IsLowSurrogate(raw[i + 1]))
            {
                var cat = CharUnicodeInfo.GetUnicodeCategory(raw, i);
                if (cat != UnicodeCategory.Format) sb.Append(c).Append(raw[i + 1]);
                i++;
                continue;
            }
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.Format) continue;
            if (category == UnicodeCategory.Control && c is not '\n' and not '\t') continue;
            if (category == UnicodeCategory.Surrogate) continue; // unpaired half
            sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormKC);
    }

    private static string Redact(string text, out bool found)
    {
        var any = false;
        foreach (var pattern in Patterns)
        {
            text = pattern.Replace(text, m =>
            {
                if (pattern == CardLike() && !Luhn(m.Value)) return m.Value;
                any = true;
                return Redacted;
            });
        }
        text = TokenLike().Replace(text, m =>
        {
            if (!LooksLikeCredential(m.Value)) return m.Value;
            any = true;
            return Redacted;
        });
        found = any;
        return text;
    }

    private static readonly Regex[] Patterns =
    [
        PemKey(), Jwt(), Bearer(), KeyValue(), VendorKey(), AwsKey(), LongHex(), CardLike(),
    ];

    /// <summary>
    /// A password-shaped single token: 8–64 characters with lower, upper, a digit and a "password" symbol. URLs,
    /// e-mail addresses, paths and code punctuation don't count, so identifiers and links pass.
    /// </summary>
    private static bool LooksLikeCredential(string token)
    {
        token = token.TrimEnd('.', ',', ';', ':', '!', '?', ')', '"', '\'');
        if (token.Length is < 8 or > 64) return false;
        if (token.Contains("://", StringComparison.Ordinal) || token.Contains('\\') || token.StartsWith('/')) return false;
        if (EmailLike().IsMatch(token)) return false;
        bool lower = false, upper = false, digit = false, symbol = false;
        foreach (var c in token)
        {
            if (char.IsLower(c)) lower = true;
            else if (char.IsUpper(c)) upper = true;
            else if (char.IsDigit(c)) digit = true;
            else if ("!@#$%^&*+=~?|".Contains(c)) symbol = true;
        }
        return lower && upper && digit && symbol;
    }

    private static bool Luhn(string candidate)
    {
        var digits = candidate.Where(char.IsDigit).Select(c => c - '0').ToArray();
        if (digits.Length is < 13 or > 19) return false;
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i];
            if (i % 2 == 1) d = d * 2 > 9 ? d * 2 - 9 : d * 2;
            sum += d;
        }
        return sum % 10 == 0;
    }

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?(?:-----END [A-Z ]*PRIVATE KEY-----|$)")]
    private static partial Regex PemKey();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"(?i)\bBearer\s+[A-Za-z0-9._~+/-]{16,}=*")]
    private static partial Regex Bearer();

    [GeneratedRegex(@"(?i)\b(?:password|passwd|pwd|secret|api[_-]?key|access[_-]?token|auth[_-]?token|token)\s*[:=]\s*\S+")]
    private static partial Regex KeyValue();

    [GeneratedRegex(@"\b(?:sk-[A-Za-z0-9_-]{20,}|[sr]k_live_[A-Za-z0-9]{16,}|gsk_[A-Za-z0-9]{20,}|gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|AIza[0-9A-Za-z_-]{35}|xox[abposr]-[A-Za-z0-9-]{10,}|glpat-[A-Za-z0-9_-]{20,}|npm_[A-Za-z0-9]{36}|pypi-[A-Za-z0-9_-]{50,})")]
    private static partial Regex VendorKey();

    [GeneratedRegex(@"\b(?:AKIA|ASIA)[A-Z0-9]{16}\b")]
    private static partial Regex AwsKey();

    [GeneratedRegex(@"\b[0-9a-fA-F]{32,}\b")]
    private static partial Regex LongHex();

    [GeneratedRegex(@"\b\d(?:[ -]?\d){12,18}\b")]
    private static partial Regex CardLike();

    [GeneratedRegex(@"\S+")]
    private static partial Regex TokenLike();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[A-Za-z]{2,}$")]
    private static partial Regex EmailLike();
}
