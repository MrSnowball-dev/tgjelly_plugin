namespace Jellyfin.Plugin.TgJelly.Compat;

/// <summary>
/// Compile-time facts about which Jellyfin line this build targets (spec §5.4 rule 6, §13.3).
/// </summary>
public static class BuildInfo
{
#if JF12
    /// <summary>
    /// The Jellyfin major version this build is compiled for.
    /// </summary>
    public const int TargetJellyfinMajor = 12;

    /// <summary>
    /// Human-readable Jellyfin line this build is compiled for.
    /// </summary>
    public const string TargetJellyfinLabel = "12";
#else
    /// <summary>
    /// The Jellyfin major version this build is compiled for.
    /// </summary>
    public const int TargetJellyfinMajor = 10;

    /// <summary>
    /// Human-readable Jellyfin line this build is compiled for.
    /// </summary>
    public const string TargetJellyfinLabel = "10.11";
#endif
}
