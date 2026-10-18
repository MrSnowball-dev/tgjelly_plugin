namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// Which newer Jellyfin releases the update checker reports (spec §7.6).
/// </summary>
public enum UpdateCheckMode
{
    /// <summary>Only newer releases with the same major version.</summary>
    SameMajor,

    /// <summary>Any newer release.</summary>
    AnyNewer,
}
