using System;
using Jellyfin.Plugin.TgJelly.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.TgJelly.Compat;

/// <summary>
/// Reads Jellyfin items into <see cref="ItemFacts"/>. Touches stored properties only, so it is O(1) inside the item
/// callbacks; the one place items differ between 10.11 and 12.
/// </summary>
public static class JellyfinItems
{
    /// <summary>
    /// Describes an item.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="albumOf">Finds a track's album ID (<see cref="Guid.Empty"/> when none); called only for <see cref="Audio"/>.</param>
    /// <returns>The facts.</returns>
    public static ItemFacts Describe(BaseItem item, Func<Audio, Guid> albumOf)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(albumOf);

        // Exact type tests, most-derived first: AudioBook derives from Audio.
        var kind = item switch
        {
            AudioBook => MediaKind.AudioBook,
            Audio => MediaKind.Audio,
            MusicAlbum => MediaKind.MusicAlbum,
            MusicVideo => MediaKind.MusicVideo,
            Movie => MediaKind.Movie,
            Episode => MediaKind.Episode,
            Book => MediaKind.Book,
            Series => MediaKind.Series,
            _ => MediaKind.Other,
        };

        var pendingId = kind == MediaKind.Audio ? albumOf((Audio)item) : item.Id;
        var identities = Identities.Of(kind, item.ProviderIds, (item as Episode)?.SeriesId ?? Guid.Empty, item.ParentIndexNumber, item.IndexNumber);
        return new ItemFacts(
            item.Id,
            kind,
            pendingId,
            item.IsVirtualItem,
            item.OwnerId != Guid.Empty,
            item.ExtraType is not null,
            item is Video video && IsAlternateVersion(video),
            item is IItemByName,
            item.DateLastRefreshed,
            identities);
    }

    /// <summary>
    /// Finds a track's album through Jellyfin's item cache; the production <c>albumOf</c>.
    /// </summary>
    /// <param name="audio">The track.</param>
    /// <returns>The album ID, or <see cref="Guid.Empty"/>.</returns>
    public static Guid AlbumOf(Audio audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        return audio.AlbumEntity?.Id ?? Guid.Empty;
    }

    /// <summary>
    /// Returns whether a video is an alternate version of another one.
    /// </summary>
    /// <param name="video">The video.</param>
    /// <returns><c>true</c> when it has a primary version.</returns>
    internal static bool IsAlternateVersion(Video video)
#if JF12
        => video.PrimaryVersionId is { } id && id != Guid.Empty;
#else
        => !string.IsNullOrEmpty(video.PrimaryVersionId);
#endif
}
