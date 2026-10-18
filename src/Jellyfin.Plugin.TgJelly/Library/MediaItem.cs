using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TgJelly.Library;

/// <summary>
/// A snapshot of a library item with what the pipeline and the layouts need. Plain data: no Jellyfin objects.
/// </summary>
public sealed class MediaItem
{
    /// <summary>Gets the item ID.</summary>
    public Guid Id { get; init; }

    /// <summary>Gets the item kind.</summary>
    public MediaKind Kind { get; init; }

    /// <summary>Gets the name (title).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the production year.</summary>
    public int? Year { get; init; }

    /// <summary>Gets the overview.</summary>
    public string? Overview { get; init; }

    /// <summary>Gets the community rating.</summary>
    public float? CommunityRating { get; init; }

    /// <summary>Gets the runtime.</summary>
    public TimeSpan? RunTime { get; init; }

    /// <summary>Gets the genres.</summary>
    public IReadOnlyList<string> Genres { get; init; } = [];

    /// <summary>Gets the provider IDs (keys are Jellyfin's provider names, e.g. <c>Imdb</c>).</summary>
    public IReadOnlyDictionary<string, string> ProviderIds { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets, for episodes, the series ID.</summary>
    public Guid SeriesId { get; init; }

    /// <summary>Gets, for episodes, the series name.</summary>
    public string? SeriesName { get; init; }

    /// <summary>Gets, for episodes, the season ID.</summary>
    public Guid SeasonId { get; init; }

    /// <summary>Gets, for episodes, the season number.</summary>
    public int? SeasonNumber { get; init; }

    /// <summary>Gets, for episodes, the episode number.</summary>
    public int? EpisodeNumber { get; init; }

    /// <summary>Gets, for albums, the album artist.</summary>
    public string? AlbumArtist { get; init; }

    /// <summary>Gets the parent item's ID (an audiobook's folder).</summary>
    public Guid ParentId { get; init; }

    /// <summary>Gets the parent item's name.</summary>
    public string? ParentName { get; init; }

    /// <summary>Gets, for albums, the number of tracks; 0 otherwise.</summary>
    public int ChildCount { get; init; }

    /// <summary>Gets the Primary image, if any.</summary>
    public ImageRef? Primary { get; init; }

    /// <summary>Gets the identity set (spec §7.4 step 8).</summary>
    public IReadOnlyList<string> Identities { get; init; } = [];
}
