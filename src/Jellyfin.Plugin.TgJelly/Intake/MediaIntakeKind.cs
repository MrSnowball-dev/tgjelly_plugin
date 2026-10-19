namespace Jellyfin.Plugin.TgJelly.Intake;

/// <summary>
/// What a media intake event reports.
/// </summary>
public enum MediaIntakeKind
{
    /// <summary>A kept item was added.</summary>
    Added,

    /// <summary>A pending item's metadata was refreshed.</summary>
    Updated,

    /// <summary>An announced kind of item was removed.</summary>
    Removed,

    /// <summary>A series was added (context only).</summary>
    SeriesAdded,

    /// <summary>A full library scan finished.</summary>
    ScanFinished,
}
