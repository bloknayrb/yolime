namespace Yolime.Core.Tests;

/// <summary>A throwaway roaming + local data root, so no test ever touches the real %APPDATA% folders.</summary>
public sealed class TempRoot : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "yolime-tests", Guid.NewGuid().ToString("N"));

    public AppPaths Paths { get; }

    public TempRoot()
    {
        Paths = new AppPaths(Path.Combine(Dir, "roaming"), Path.Combine(Dir, "local"));
        Directory.CreateDirectory(Dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
