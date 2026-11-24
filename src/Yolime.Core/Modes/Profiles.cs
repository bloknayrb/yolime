using Yolime.Core.Output;

namespace Yolime.Core.Modes;

/// <summary>The style of the app being written into. It sets tone and section names, never output safety.</summary>
public enum Profile { Neutral, Casual, Email, Code, Terminal }

/// <summary>The spec's two section sets for structured prompts.</summary>
public enum SectionSet { Coding, Chat }

public static class Profiles
{
    private static readonly HashSet<string> Casual = new(StringComparer.OrdinalIgnoreCase)
    {
        "slack", "Discord", "Teams", "ms-teams", "WhatsApp", "Telegram", "Signal",
    };

    private static readonly HashSet<string> Email = new(StringComparer.OrdinalIgnoreCase)
    {
        "OUTLOOK", "olk", "thunderbird",
    };

    private static readonly HashSet<string> Code = new(StringComparer.OrdinalIgnoreCase)
    {
        "Code", "Code - Insiders", "Cursor", "Windsurf", "VSCodium", "devenv", "idea64", "rider64", "pycharm64",
        "webstorm64", "zed", "sublime_text", "notepad++",
    };

    /// <summary>
    /// The terminal flag comes from <see cref="TerminalDetector"/>, so the profile and output safety can never
    /// disagree about what a terminal is. An editor's integrated terminal is Terminal; the editor itself is Code.
    /// </summary>
    public static Profile For(TargetSnapshot? target)
    {
        if (target is null) return Profile.Neutral;
        if (TerminalDetector.IsTerminal(target)) return Profile.Terminal;
        if (Code.Contains(target.ProcessName)) return Profile.Code;
        if (Email.Contains(target.ProcessName)) return Profile.Email;
        if (Casual.Contains(target.ProcessName)) return Profile.Casual;
        return Profile.Neutral;
    }

    public static SectionSet SectionsFor(Profile profile) =>
        profile is Profile.Code or Profile.Terminal ? SectionSet.Coding : SectionSet.Chat;
}
