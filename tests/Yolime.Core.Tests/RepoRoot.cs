namespace Yolime.Core.Tests;

/// <summary>The repository root, found by walking up from the test binary to Yolime.slnx.</summary>
public static class RepoRoot
{
    public static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Yolime.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("Yolime.slnx not found above the test binary");
    }
}
