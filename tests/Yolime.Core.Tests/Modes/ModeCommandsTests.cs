using Yolime.Core.Modes;

namespace Yolime.Core.Tests.Modes;

public class ModeCommandsTests
{
    [Theory]
    [InlineData("Write a prompt. Fix the login bug.", Mode.Prompt, "Fix the login bug.")]
    [InlineData("write a prompt: fix the login bug", Mode.Prompt, "Fix the login bug")]
    [InlineData("Write a prompt — fix the login bug", Mode.Prompt, "Fix the login bug")]
    [InlineData("Write a prompt… fix it", Mode.Prompt, "Fix it")]
    [InlineData("Write me a prompt that explains closures", Mode.Prompt, "That explains closures")]
    [InlineData("Make this a prompt. Something", Mode.Prompt, "Something")]
    [InlineData("Make a prompt, something", Mode.Prompt, "Something")]
    [InlineData("Prompt mode. Something", Mode.Prompt, "Something")]
    [InlineData("Write a reply. Sounds good, see you then.", Mode.Reply, "Sounds good, see you then.")]
    [InlineData("WRITE A REPLY: thanks", Mode.Reply, "Thanks")]
    [InlineData("Write me a reply saying yes", Mode.Reply, "Saying yes")]
    [InlineData("Draft a reply, say no politely", Mode.Reply, "Say no politely")]
    [InlineData("Write a response. Agree with him", Mode.Reply, "Agree with him")]
    [InlineData("Draft a response. Agree", Mode.Reply, "Agree")]
    [InlineData("Reply mode. Thanks!", Mode.Reply, "Thanks!")]
    [InlineData("Raw mode. hello there", Mode.Raw, "hello there")]
    [InlineData("Verbatim mode: um, hello", Mode.Raw, "um, hello")]
    [InlineData("Prompt. Fix the bug", Mode.Prompt, "Fix the bug")]
    [InlineData("Reply. Thanks!", Mode.Reply, "Thanks!")]
    [InlineData("Reply: thanks", Mode.Reply, "Thanks")]
    [InlineData("Raw. hello there", Mode.Raw, "hello there")]
    [InlineData("Verbatim. He said what?", Mode.Raw, "He said what?")]
    [InlineData("Write a reply", Mode.Reply, "")]
    [InlineData("Write a prompt.", Mode.Prompt, "")]
    [InlineData("Reply", Mode.Reply, "")]
    public void Commands_MatchAndStrip(string transcript, Mode mode, string rest)
    {
        var (match, stripped) = ModeCommands.Split(transcript);
        Assert.NotNull(match);
        Assert.Equal(mode, match.Mode);
        Assert.Equal(rest, stripped);
    }

    [Theory]
    [InlineData("Um, write a reply. Thanks", Mode.Reply, "Thanks")]
    [InlineData("Okay. Write a prompt: fix it", Mode.Prompt, "Fix it")]
    [InlineData("So, write a prompt to fix it", Mode.Prompt, "To fix it")]
    [InlineData("  \"Write a prompt.\" fix it", Mode.Prompt, "\" fix it")]
    [InlineData(" Write a reply. Thanks", Mode.Reply, "Thanks")]
    [InlineData("Write  a\tprompt. Fix it", Mode.Prompt, "Fix it")]
    public void Fillers_LeadingQuotesAndOddWhitespace(string transcript, Mode mode, string rest)
    {
        var (match, stripped) = ModeCommands.Split(transcript);
        Assert.Equal(mode, match?.Mode);
        Assert.Equal(rest, stripped);
    }

    [Theory]
    [InlineData("Reply all is broken in Outlook.")]
    [InlineData("Raw, unfiltered thoughts on the plan.")]
    [InlineData("Verbatim, he said no.")]
    [InlineData("Prompted by the review, I changed it.")]
    [InlineData("Prompt payment is due on Friday.")]
    [InlineData("Raw milk is not allowed.")]
    [InlineData("Replying now.")]
    [InlineData("reply to this email by Friday")]
    [InlineData("Remind me to write a reply to John.")]
    [InlineData("I need to write a prompt for the agent.")]
    [InlineData("Write a prompter class.")]
    [InlineData("Write a prompt-engineering guide.")]
    [InlineData("Okay so write a prompt to fix it")]
    [InlineData("Um, reply. Thanks")]
    [InlineData("Okay, so, write a prompt")]
    [InlineData("Respond to him.")]
    [InlineData("Write back soon.")]
    [InlineData("")]
    [InlineData("   ")]
    public void Dictation_IsNeverACommand(string transcript)
    {
        Assert.Null(ModeCommands.Match(transcript));
        Assert.Equal(transcript, ModeCommands.Split(transcript).Text);
    }

    [Theory]
    [InlineData("Write a reply. iPhone sales are up", "iPhone sales are up")]
    [InlineData("Write a reply. eBay sent it", "eBay sent it")]
    [InlineData("Write a reply. 3 o'clock works", "3 o'clock works")]
    [InlineData("Write a reply. a quick yes", "A quick yes")]
    public void Capitalisation_OnlyWhereItIsSafe(string transcript, string rest) =>
        Assert.Equal(rest, ModeCommands.Split(transcript).Text);

    [Fact]
    public void Raw_IsNeverRecapitalised() => Assert.Equal("hello there", ModeCommands.Split("Raw: hello there").Text);

    [Theory]
    [InlineData("Write a reply. Thanks", Mode.Reply, "Thanks")]
    [InlineData("Write a reply. Thanks", Mode.Dictate, "Write a reply. Thanks")]
    [InlineData("Write a reply. Thanks", Mode.Prompt, "Write a reply. Thanks")]
    [InlineData("Just dictation", Mode.Reply, "Just dictation")]
    public void ContentFor_StripsOnlyForTheModeTheCommandAskedFor(string transcript, Mode mode, string expected) =>
        Assert.Equal(expected, ModeCommands.ContentFor(transcript, mode));
}
