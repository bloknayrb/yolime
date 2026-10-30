using Yolime.Core.Llm;
using Yolime.Core.Modes;

namespace Yolime.Core.Tests.Llm;

public class RewriteRulesTests
{
    [Theory]
    [InlineData("hello world", true)]
    [InlineData("send the report to Alex by Friday please", true)]
    [InlineData("one two three four five six seven eight nine ten eleven", true)]
    [InlineData("one two three four five six seven eight nine ten eleven twelve", false)]
    [InlineData("um send it to Alex", false)]
    [InlineData("send it Tuesday no wait Wednesday", false)]
    [InlineData("send it to Sam sorry Alex", false)]
    [InlineData("dear Alex new line thanks", false)]
    [InlineData("first item new paragraph second", false)]
    [InlineData("bullet milk bullet eggs", false)]
    [InlineData("yes comma that works period", false)]
    [InlineData("slash help", false)]
    [InlineData("scratch that", false)]
    public void ShortDictation_SkipsTheLlmOnlyWhenNothingNeedsIt(string raw, bool skip) =>
        Assert.Equal(skip, ShortDictation.CanSkip(raw));

    [Theory]
    [InlineData("hello world", Profile.Casual, "Hello world.")]
    [InlineData("  is it ready  ", Profile.Neutral, "Is it ready.")]
    [InlineData("is it ready?", Profile.Neutral, "Is it ready?")]
    [InlineData("i think i can", Profile.Email, "I think I can.")]
    [InlineData("call   me", Profile.Casual, "Call me.")]
    [InlineData("git status", Profile.Terminal, "git status")]
    [InlineData("  npm run build ", Profile.Code, "npm run build")]
    public void RegexCleanup_IsProfileAware(string raw, Profile profile, string expected) =>
        Assert.Equal(expected, RegexCleanup.Apply(raw, profile));

    [Theory]
    [InlineData("Profile: email\nHi Sam, thanks.", "Hi Sam, thanks.")]
    [InlineData("Profile: code\nSections: Task / Context / Requirements / Done when\nTask: fix it", "Task: fix it")]
    [InlineData("<transcript-ab12cd34>\nHello there.\n</transcript-ab12cd34>", "Hello there.")]
    [InlineData("Output goes into a terminal: write a single paragraph on one line.\nls -la", "ls -la")]
    [InlineData("Task: keep my Profile: line", "Task: keep my Profile: line")]
    // The user's own words that merely look like a header stay (final review).
    [InlineData("Length: 20 cm\nWidth: 10 cm", "Length: 20 cm\nWidth: 10 cm")]
    [InlineData("Profile: Sam's account\nNeeds a reset", "Profile: Sam's account\nNeeds a reset")]
    [InlineData("Use the <context-menu> element", "Use the <context-menu> element")]
    public void OutputText_StripsAnEchoOfTheRequest(string echoed, string expected) =>
        Assert.Equal(expected, OutputText.Normalise(echoed));

    [Fact]
    public void OutputText_StripsInvisibleControls_KeepsEmojiJoiners()
    {
        var text = "Hi\u202Ethere\u200B\r\nnext \U0001F469\u200D\U0001F4BB\uFEFF\n";
        Assert.Equal("Hithere\nnext \U0001F469\u200D\U0001F4BB", OutputText.Normalise(text));
    }
}
