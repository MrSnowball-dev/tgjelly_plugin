namespace Jellyfin.Plugin.TgJelly.Library;

/// <summary>
/// What the media filter decides for an added item.
/// </summary>
public enum FilterVerdict
{
    /// <summary>Not announced.</summary>
    Drop,

    /// <summary>Tracked until it is ready.</summary>
    Keep,

    /// <summary>A series: only recorded, so its first batch says "New series".</summary>
    SeriesContext,
}
