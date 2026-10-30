using Yolime.Core.Startup;

namespace Yolime.Core.Tests.Startup;

public class StartWithWindowsTests
{
    private const string Exe = @"C:\Users\b\AppData\Local\Programs\yolime\yolime.exe";

    [Fact]
    public void TheCommand_IsQuoted() => Assert.Equal("\"" + Exe + "\"", StartWithWindows.CommandFor(Exe));

    [Theory]
    [InlineData(null, StartupStatus.Off)]
    [InlineData("", StartupStatus.Off)]
    [InlineData("\"" + Exe + "\"", StartupStatus.On)]
    [InlineData(Exe, StartupStatus.On)]
    [InlineData("\"C:\\Users\\B\\APPDATA\\Local\\Programs\\yolime\\YOLIME.EXE\" --tray", StartupStatus.On)]
    [InlineData("\"C:\\old\\yolime.exe\"", StartupStatus.OtherCopy)]
    public void TheRunValue_IsRead(string? run, StartupStatus expected) =>
        Assert.Equal(expected, StartWithWindows.From(run, null, Exe));

    [Fact]
    public void TaskManagersSwitch_Wins()
    {
        var run = StartWithWindows.CommandFor(Exe);
        Assert.Equal(StartupStatus.DisabledInTaskManager, StartWithWindows.From(run, [0x03, 1, 2, 3, 4, 5, 6, 7, 0, 0, 0, 0], Exe));
        Assert.Equal(StartupStatus.On, StartWithWindows.From(run, StartWithWindows.Enabled(), Exe));
        Assert.Equal(StartupStatus.On, StartWithWindows.From(run, [0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], Exe));
    }
}
