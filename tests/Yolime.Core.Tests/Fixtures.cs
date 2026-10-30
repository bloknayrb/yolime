namespace Yolime.Core.Tests;

public static class Fixtures
{
    /// <summary>Finds <c>tests/fixtures/audio/name</c> by walking up from the test binary.</summary>
    public static string Audio(string name) => Find("audio", name, "run tools/make-test-audio.ps1");

    /// <summary>Finds <c>tests/fixtures/updates/name</c>: GitHub API answers recorded from a real release.</summary>
    public static string Update(string name) => Find("updates", name, "it is committed with the tests");

    private static string Find(string folder, string name, string hint)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "fixtures", folder, name);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"fixture {name} not found; {hint}");
    }
}
