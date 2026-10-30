using Yolime.Core.Modes;
using Yolime.Core.Output;
using Yolime.Core.Settings;

namespace Yolime.Core.Tests.Modes;

public class ProfilesTests
{
    private static TargetSnapshot Target(string process, string cls = "Chrome_WidgetWin_1", string? focusClass = null) =>
        new(1, 1, process, cls, null, focusClass, false, null, false);

    [Theory]
    [InlineData("slack", Profile.Casual)]
    [InlineData("Discord", Profile.Casual)]
    [InlineData("ms-teams", Profile.Casual)]
    [InlineData("OUTLOOK", Profile.Email)]
    [InlineData("olk", Profile.Email)]
    [InlineData("Code", Profile.Code)]
    [InlineData("Cursor", Profile.Code)]
    [InlineData("devenv", Profile.Code)]
    [InlineData("WINWORD", Profile.Neutral)]
    [InlineData("chrome", Profile.Neutral)]
    [InlineData("somethingelse", Profile.Neutral)]
    public void ProcessProfiles(string process, Profile expected) => Assert.Equal(expected, Profiles.For(Target(process)));

    [Fact]
    public void Terminal_ComesFromTheDetector_IncludingAnEditorsTerminal()
    {
        Assert.Equal(Profile.Terminal, Profiles.For(Target("WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS")));
        Assert.Equal(Profile.Terminal, Profiles.For(Target("Code", focusClass: "xterm-helper-textarea")));
        Assert.Equal(Profile.Code, Profiles.For(Target("Code", focusClass: "native-edit-context")));
    }

    [Fact]
    public void ProfileTerminalFlag_AgreesWithTheDetector_ForEveryProbe()
    {
        string[] processes = ["conhost", "WindowsTerminal", "mintty", "alacritty", "Code", "Cursor", "slack", "WINWORD"];
        string[] classes = ["ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "mintty", "Chrome_WidgetWin_1", "OpusApp"];
        string?[] focus = [null, "xterm-helper-textarea", "native-edit-context"];
        foreach (var p in processes)
        foreach (var c in classes)
        foreach (var f in focus)
        {
            var t = Target(p, c, f);
            Assert.Equal(TerminalDetector.IsTerminal(t), Profiles.For(t) == Profile.Terminal);
        }
    }

    [Fact]
    public void NoTarget_IsNeutral() => Assert.Equal(Profile.Neutral, Profiles.For(null));

    [Theory]
    [InlineData(Profile.Code, SectionSet.Coding)]
    [InlineData(Profile.Terminal, SectionSet.Coding)]
    [InlineData(Profile.Neutral, SectionSet.Chat)]
    [InlineData(Profile.Casual, SectionSet.Chat)]
    [InlineData(Profile.Email, SectionSet.Chat)]
    public void Sections(Profile profile, SectionSet expected) => Assert.Equal(expected, Profiles.SectionsFor(profile));
}

public class StartingModesTests
{
    private static TargetSnapshot Target(string process, string cls = "Chrome_WidgetWin_1") =>
        new(1, 1, process, cls, null, null, false, null, false);

    [Fact]
    public void PerAppSetting_WinsOverEverything()
    {
        var s = new YolimeSettings { StartingMode = Mode.Raw, AppStartingModes = new() { ["Code"] = "Reply", ["slack"] = "Prompt" } };
        Assert.Equal(Mode.Reply, StartingModes.For(Target("code"), s));
        Assert.Equal(Mode.Prompt, StartingModes.For(Target("Slack"), s));
    }

    [Fact]
    public void CodeAndTerminalApps_StartInPrompt_ByDefault()
    {
        var s = new YolimeSettings();
        Assert.Equal(Mode.Prompt, StartingModes.For(Target("Code"), s));
        Assert.Equal(Mode.Prompt, StartingModes.For(Target("WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS"), s));
    }

    [Fact]
    public void OtherApps_UseTheGlobalStartingMode()
    {
        Assert.Equal(Mode.Dictate, StartingModes.For(Target("slack"), new YolimeSettings()));
        Assert.Equal(Mode.Raw, StartingModes.For(Target("slack"), new YolimeSettings { StartingMode = Mode.Raw }));
        Assert.Equal(Mode.Dictate, StartingModes.For(null, new YolimeSettings()));
    }

    [Fact]
    public void ABadPerAppEntry_IsIgnored()
    {
        var s = new YolimeSettings { AppStartingModes = new() { ["Code"] = "Promptt", ["slack"] = "7" } };
        Assert.Equal(Mode.Prompt, StartingModes.For(Target("Code"), s));
        Assert.Equal(Mode.Dictate, StartingModes.For(Target("slack"), s));
    }
}
