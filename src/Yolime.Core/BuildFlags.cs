namespace Yolime.Core;

/// <summary>Compile-time switches, so tests can prove which build they are running in.</summary>
public static class BuildFlags
{
#if YOLIME_E2E
    public const bool IsE2E = true;
#else
    public const bool IsE2E = false;
#endif
}
