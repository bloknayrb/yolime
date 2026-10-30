using Microsoft.Extensions.Time.Testing;
using Yolime.Core.Logging;

namespace Yolime.Core.Tests.Logging;

public sealed class FileLogTests : IDisposable
{
    private readonly TempRoot _root = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _root.Dispose();

    private string[] ReadAllLines() =>
        Directory.GetFiles(_root.Paths.LogsDir, "yolime-*.log").SelectMany(File.ReadAllLines).ToArray();

    [Fact]
    public void Write_FormatsTypedFields()
    {
        var log = new FileLog(_root.Paths, _time);
        log.Write(LogEvent.StageCompleted, LogField.Tag("stage", Stage.Stt), LogField.Ms("ms", 812), LogField.HttpStatus(200));

        var line = Assert.Single(ReadAllLines());
        Assert.EndsWith("StageCompleted stage=Stt ms=812 http=200", line);
    }

    [Fact]
    public void Error_RecordsTypeAndHResultOnly()
    {
        var log = new FileLog(_root.Paths, _time);
        log.Write(LogEvent.StageFailed, LogField.Error(new HttpRequestException("SENTINEL_x")));

        var line = Assert.Single(ReadAllLines());
        Assert.Contains("error=HttpRequestException hr=", line);
        Assert.DoesNotContain("SENTINEL", line);
    }

    [Fact]
    public void Key_RejectsNonIdentifier_AndMessageOmitsInput()
    {
        var ex = Assert.Throws<ArgumentException>(() => LogField.Count("SENTINEL key", 1));
        Assert.DoesNotContain("SENTINEL", ex.ToString());
    }

    [Fact]
    public void Rotation_DeletesFilesNamedOlderThan7Days()
    {
        Directory.CreateDirectory(_root.Paths.LogsDir);
        var old = Path.Combine(_root.Paths.LogsDir, "yolime-20261019.log");
        var recent = Path.Combine(_root.Paths.LogsDir, "yolime-20261021.log");
        File.WriteAllText(old, "x\n");
        File.WriteAllText(recent, "x\n");

        _ = new FileLog(_root.Paths, _time);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }
}
