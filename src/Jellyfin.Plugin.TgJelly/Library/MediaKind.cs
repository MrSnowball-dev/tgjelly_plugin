namespace Jellyfin.Plugin.TgJelly.Library;

/// <summary>
/// The kinds of library item the pipeline tells apart. Type tests are exact, most-derived first.
/// </summary>
public enum MediaKind
{
    /// <summary>Anything TgJelly doesn't announce.</summary>
    Other,

    /// <summary>A movie.</summary>
    Movie,

    /// <summary>An episode, batched by series and season.</summary>
    Episode,

    /// <summary>A series; only context ("New series").</summary>
    Series,

    /// <summary>A music album.</summary>
    MusicAlbum,

    /// <summary>A track; tracked by its album.</summary>
    Audio,

    /// <summary>A music video.</summary>
    MusicVideo,

    /// <summary>A book.</summary>
    Book,

    /// <summary>One file of an audiobook; grouped by its folder.</summary>
    AudioBook,
}
