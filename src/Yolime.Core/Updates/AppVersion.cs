using System.Globalization;
using System.Text.RegularExpressions;

namespace Yolime.Core.Updates;

/// <summary>
/// A release's version, compared numerically. A prerelease (a "-pre" build) is older than the release of the same
/// number, so a test build always takes the real one.
/// </summary>
public readonly partial record struct AppVersion(int Major, int Minor, int Patch, bool Prerelease) : IComparable<AppVersion>
{
    /// <summary>A release tag: exactly "vX.Y.Z". Anything else (a prerelease tag, a stray suffix) isn't a release.</summary>
    public static bool TryParseTag(string? tag, out AppVersion version)
    {
        version = default;
        if (tag is null || TagPattern().Match(tag) is not { Success: true } m) return false;
        return TryParts(m, prerelease: false, out version);
    }

    /// <summary>
    /// The running build's version, from its ProductVersion ("0.1.0+&lt;commit&gt;", space-padded by Windows, or
    /// without a commit when built outside git). Null when it isn't one, as in a build with no version set.
    /// </summary>
    public static AppVersion? FromProductVersion(string? product)
    {
        if (product is null || ProductPattern().Match(product.Trim()) is not { Success: true } m) return null;
        return TryParts(m, prerelease: m.Groups["pre"].Success, out var v) ? v : null;
    }

    /// <summary>For Settings: "0.1.0+abc1234" (the commit shortened to 7), or the version alone.</summary>
    public static string Display(string? product)
    {
        var trimmed = product?.Trim() ?? "";
        var plus = trimmed.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 || trimmed.Length - plus - 1 <= 7 ? trimmed : trimmed[..(plus + 8)];
    }

    public int CompareTo(AppVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c == 0) c = other.Prerelease.CompareTo(Prerelease); // a prerelease sorts below its release
        return c;
    }

    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;

    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;

    public static bool operator >=(AppVersion a, AppVersion b) => a.CompareTo(b) >= 0;

    public static bool operator <=(AppVersion a, AppVersion b) => a.CompareTo(b) <= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (Prerelease ? "-pre" : "");

    private static bool TryParts(Match m, bool prerelease, out AppVersion version)
    {
        version = default;
        if (!int.TryParse(m.Groups["a"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var a)
            || !int.TryParse(m.Groups["b"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var b)
            || !int.TryParse(m.Groups["c"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var c)) return false;
        version = new AppVersion(a, b, c, prerelease);
        return true;
    }

    // \z, not $: "$" also matches before a final newline, and the tag must be exactly vX.Y.Z.
    [GeneratedRegex(@"^v(?<a>\d{1,5})\.(?<b>\d{1,5})\.(?<c>\d{1,5})\z", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"^(?<a>\d{1,5})\.(?<b>\d{1,5})\.(?<c>\d{1,5})(?<pre>-[0-9A-Za-z.]+)?(\+[0-9A-Za-z.]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex ProductPattern();
}
