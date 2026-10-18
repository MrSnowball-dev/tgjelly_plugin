namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// The event preset a destination was set up with (spec §7.2).
/// </summary>
public enum PresetKind
{
    /// <summary>All event keys.</summary>
    Everything,

    /// <summary>All media.* keys.</summary>
    NewMedia,

    /// <summary>security.* without login_succeeded, plus server.*.</summary>
    AdminAlerts,

    /// <summary>Individually chosen keys.</summary>
    Custom,
}
