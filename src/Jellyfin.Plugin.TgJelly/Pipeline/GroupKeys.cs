using System;
using System.Globalization;
using Jellyfin.Plugin.TgJelly.Library;

namespace Jellyfin.Plugin.TgJelly.Pipeline;

/// <summary>
/// Which group an item joins: episodes by season, audiobook parts by folder, everything else alone.
/// </summary>
public static class GroupKeys
{
    /// <summary>
    /// Returns whether items of a kind are announced in groups.
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <returns><c>true</c> for every kind <see cref="For"/> accepts.</returns>
    public static bool IsGrouped(MediaKind kind) => kind is MediaKind.Movie or MediaKind.MusicVideo or MediaKind.Book
        or MediaKind.AudioBook or MediaKind.Episode or MediaKind.MusicAlbum;

    /// <summary>
    /// Returns an item's group.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The group key, the event key, and the container the message points at (Guid <c>N</c>).</returns>
    /// <exception cref="ArgumentOutOfRangeException">The kind isn't announced.</exception>
    public static (string Key, string EventKey, string ContainerId) For(MediaItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var id = N(item.Id);
        return item.Kind switch
        {
            MediaKind.Movie => ("movie:" + id, "media.movie", id),
            MediaKind.MusicVideo => ("musicvideo:" + id, "media.musicvideo", id),
            MediaKind.Book => ("book:" + id, "media.book", id),
            MediaKind.AudioBook => ("audiobook:" + N(item.ParentId), "media.book", N(item.ParentId)),
            MediaKind.MusicAlbum => ("album:" + id, "media.album", id),
            MediaKind.Episode when item.SeasonId != Guid.Empty
                => ("episodes:" + N(item.SeriesId) + ":" + N(item.SeasonId), "media.episodes", N(item.SeasonId)),
            MediaKind.Episode => (
                string.Create(CultureInfo.InvariantCulture, $"episodes:{N(item.SeriesId)}:s{item.SeasonNumber}"),
                "media.episodes",
                N(item.SeriesId)),
            _ => throw new ArgumentOutOfRangeException(nameof(item), item.Kind, "Items of this kind aren't announced."),
        };
    }

    private static string N(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);
}
