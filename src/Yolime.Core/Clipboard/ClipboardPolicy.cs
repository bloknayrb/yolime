namespace Yolime.Core.Clipboard;

/// <param name="Id">The clipboard format id.</param>
/// <param name="Name">The registered name, for ids from <c>RegisterClipboardFormat</c> (0xC000 and up).</param>
public readonly record struct ClipFormat(uint Id, string? Name);

/// <summary>
/// The rules ClipboardGuard follows when it borrows the user's clipboard to paste: what to save, when to put it back,
/// and how to keep its own traffic out of Win+V history and cloud sync.
/// </summary>
public static class ClipboardPolicy
{
    private const uint CfDib = 8;
    private const uint CfDibV5 = 17;

    // Formats whose data is a plain HGLOBAL byte block, so saving the bytes and setting them back is exact.
    // CF_TEXT/CF_OEMTEXT and CF_BITMAP are synthesized by Windows from these, and handle formats (bitmaps,
    // metafiles, palettes, private and GDI ranges) can't be copied as bytes, so they're left out.
    private static readonly HashSet<uint> StandardSaveable = [13, CfDib, CfDibV5, 15, 16];
    // Registered formats are almost all HGLOBAL byte blocks (HTML, RTF, PNG, Excel's Biff12, Explorer's "Preferred
    // DropEffect", which makes a Cut a cut), so they're kept, except OLE formats that only make sense live, and the
    // markers we add ourselves.
    private static readonly HashSet<string> RegisteredNotSaveable = new(StringComparer.Ordinal)
    {
        "Embed Source", "Object Descriptor", "Link Source", "Link Source Descriptor", "Ole Private Data", "DataObject",
        // Virtual files: the descriptor is bytes but FileContents is an IStream, so a restore would be broken.
        "FileGroupDescriptorW", "FileGroupDescriptor", "FileContents",
        "ExcludeClipboardContentFromMonitorProcessing", "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard",
        "Clipboard Viewer Ignore",
    };
    private static readonly HashSet<string> SensitiveMarkers = new(StringComparer.Ordinal)
    {
        "ExcludeClipboardContentFromMonitorProcessing", "Clipboard Viewer Ignore",
    };

    public static TimeSpan RestoreDelay { get; } = TimeSpan.FromMilliseconds(800);

    /// <summary>
    /// Remote desktop and VM consoles read the clipboard late (redirection syncs it on the remote side's paste), so the
    /// restore waits longer there; restoring early would paste the user's previous clipboard remotely.
    /// </summary>
    public static readonly TimeSpan RemoteRestoreDelay = TimeSpan.FromSeconds(3);

    /// <summary>
    /// After the usual wait, how much longer the restore waits for a target that still hasn't read an offered paste.
    /// The usual wait stays the minimum, so a clipboard manager reading at once can't make the restore any earlier.
    /// </summary>
    public static readonly TimeSpan MaxReadWait = TimeSpan.FromSeconds(3);

    /// <summary>After the target's read, before the restore: OLE readers open the clipboard once per format.</summary>
    public static readonly TimeSpan ReadGrace = TimeSpan.FromMilliseconds(250);

    public const int MaxOpenAttempts = 10;

    /// <summary>
    /// Marker formats set alongside yolime's clipboard content. Cloud upload is always blocked: dictation shouldn't
    /// sync to other devices. History exclusion is for the brief paste copy and the restored original; a result
    /// left on the clipboard for the user ("copied, not pasted") stays in Win+V history.
    /// </summary>
    public static IReadOnlyList<string> ExclusionFormats(bool excludeFromHistory) => excludeFromHistory
        ? ["ExcludeClipboardContentFromMonitorProcessing", "Clipboard Viewer Ignore", "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard"]
        : ["CanUploadToCloudClipboard"];

    public static IReadOnlyList<ClipFormat> Saveable(IEnumerable<ClipFormat> present)
    {
        var list = present.ToList();
        var hasDibV5 = list.Exists(f => f.Id == CfDibV5);
        return list.Where(f => f.Name is { } name
                ? !RegisteredNotSaveable.Contains(name)
                : StandardSaveable.Contains(f.Id) && !(f.Id == CfDib && hasDibV5))
            .ToList();
    }

    /// <summary>Password managers mark their copies; such content is never used as reply context (PR3).</summary>
    public static bool IsSensitive(IEnumerable<ClipFormat> present) => present.Any(f => f.Name is { } n && SensitiveMarkers.Contains(n));

    /// <summary>Restore only if nobody has copied anything since our set: never clobber the user's own copy.</summary>
    public static bool ShouldRestore(uint ourSequence, uint currentSequence) => ourSequence == currentSequence;

    public static TimeSpan OpenRetryDelay(int attempt) =>
        TimeSpan.FromMilliseconds(Math.Min(200, 10 * Math.Pow(2, Math.Min(attempt, 10))));
}
