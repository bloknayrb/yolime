namespace Yolime.Core.Updates;

/// <summary>
/// Why an update didn't install, from the one word install.ps1 (or setup itself) writes to the result file. A fixed
/// set: the file's text is never shown or logged, only which of these it was.
/// </summary>
public enum UpdateFailure { SetupFailed, Running, FilesInUse, CheckFailed, SwapFailed, Error, NoResult, Unexpected }

/// <summary>How the last update went, read once at the next start (or when setup exits while yolime still runs).</summary>
public sealed record UpdateOutcome(AppVersion Expected, AppVersion Running, UpdateFailure? Failure)
{
    public bool Succeeded => Failure is null;
}

/// <summary>
/// The updates folder: the downloaded setup, the expected-version marker written just before setup starts, setup's
/// result file and its log. The marker is what makes the next start report on the update, exactly once.
/// </summary>
public static class UpdateFiles
{
    public const string MarkerName = "expected-version";
    public const string ResultName = "result.txt";
    public const string LogName = "setup.log";

    /// <summary>
    /// Step 1 of an install: the last result and log go first (a stale "ok" must never stand for this run), then the
    /// marker. False when the old files couldn't be removed or the marker written: then nothing may be launched.
    /// </summary>
    public static bool Prepare(string dir, AppVersion expected)
    {
        try
        {
            Directory.CreateDirectory(dir);
            File.Delete(Path.Combine(dir, ResultName));
            File.Delete(Path.Combine(dir, LogName));
            File.WriteAllText(Path.Combine(dir, MarkerName), $"{expected.Major}.{expected.Minor}.{expected.Patch}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The marker is removed too: an update that never launched isn't reported.</summary>
    public static void Abandon(string dir)
    {
        try { File.Delete(Path.Combine(dir, MarkerName)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// The outcome of the update the marker names, or null when none was tried. Both files are deleted, so it's
    /// reported once. Running the expected version is a success whatever the result says (the new copy is up);
    /// anything else is a failure whose reason comes from the result word.
    /// </summary>
    public static UpdateOutcome? ReadAndClear(string dir, AppVersion running)
    {
        var marker = Path.Combine(dir, MarkerName);
        var result = Path.Combine(dir, ResultName);
        string? expectedText, word = null;
        try
        {
            if (!File.Exists(marker)) return null;
            expectedText = File.ReadAllText(marker);
            if (File.Exists(result)) word = File.ReadAllText(result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        Delete(marker);
        Delete(result);
        if (AppVersion.FromProductVersion(expectedText) is not { } expected) return null;
        if (Same(expected, running)) return new(expected, running, null);
        return new(expected, running, FromWord(word));
    }

    /// <summary>The arguments for the downloaded setup, one token each (for ProcessStartInfo.ArgumentList).</summary>
    /// <remarks>
    /// No quote characters: ArgumentList quotes a token that holds a space by itself, while a token holding a quote is
    /// backslash-escaped, which Inno's parser (it only toggles on quotes) would keep as part of the path.
    /// </remarks>
    public static IReadOnlyList<string> SetupArguments(string dir) =>
    [
        "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/NOCANCEL", "/UPDATE",
        "/RESULT=" + Path.Combine(dir, ResultName),
        "/LOG=" + Path.Combine(dir, LogName),
    ];

    /// <summary>Deletes downloaded setups (and partial downloads). False if any is still in use, to retry later.</summary>
    public static bool DeleteSetups(string dir)
    {
        string[] files;
        try
        {
            if (!Directory.Exists(dir)) return true;
            files = Directory.GetFiles(dir, "yolime-setup-*");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        var all = true;
        foreach (var f in files.Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".part", StringComparison.OrdinalIgnoreCase)))
        {
            try { File.Delete(f); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { all = false; }
        }
        return all;
    }

    private static UpdateFailure FromWord(string? word) => word?.Trim() switch
    {
        null => UpdateFailure.NoResult,
        "running" => UpdateFailure.Running,
        "files-in-use" => UpdateFailure.FilesInUse,
        "check-failed" => UpdateFailure.CheckFailed,
        "swap-failed" => UpdateFailure.SwapFailed,
        "error" => UpdateFailure.Error,
        "setup-failed" => UpdateFailure.SetupFailed,
        // "ok" with the old copy still running, "data-kept" (uninstall only), or anything else.
        _ => UpdateFailure.Unexpected,
    };

    private static bool Same(AppVersion a, AppVersion b) => a.Major == b.Major && a.Minor == b.Minor && a.Patch == b.Patch;

    private static void Delete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
