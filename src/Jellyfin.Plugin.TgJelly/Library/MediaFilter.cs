namespace Jellyfin.Plugin.TgJelly.Library;

/// <summary>
/// The new-media filter (spec §7.4 step 1, F12). Pure and O(1): it runs inside Jellyfin's ItemAdded callback.
/// </summary>
public static class MediaFilter
{
    /// <summary>
    /// Decides whether an added item is announced.
    /// </summary>
    /// <param name="facts">The item.</param>
    /// <returns>The verdict.</returns>
    public static FilterVerdict Classify(ItemFacts facts)
    {
        System.ArgumentNullException.ThrowIfNull(facts);
        if (facts.IsVirtual || facts.HasOwner || facts.IsExtra || facts.IsAlternateVersion || facts.IsItemByName)
        {
            return FilterVerdict.Drop;
        }

        if (facts.Kind == MediaKind.Series)
        {
            return FilterVerdict.SeriesContext;
        }

        // Audio without an album is not announced (spec §7.1).
        if (facts.Kind == MediaKind.Audio && facts.PendingId == System.Guid.Empty)
        {
            return FilterVerdict.Drop;
        }

        return IsAnnounced(facts.Kind) ? FilterVerdict.Keep : FilterVerdict.Drop;
    }

    /// <summary>
    /// Returns whether items of a kind are announced.
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <returns><c>true</c> for movies, episodes, albums, tracks, music videos, books and audiobooks.</returns>
    public static bool IsAnnounced(MediaKind kind)
        => kind is MediaKind.Movie or MediaKind.Episode or MediaKind.MusicAlbum or MediaKind.Audio
            or MediaKind.MusicVideo or MediaKind.Book or MediaKind.AudioBook;
}
