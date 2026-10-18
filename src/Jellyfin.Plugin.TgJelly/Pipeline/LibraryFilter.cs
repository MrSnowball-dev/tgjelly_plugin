using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;

namespace Jellyfin.Plugin.TgJelly.Pipeline;

/// <summary>
/// A destination's library filter (spec §7.3): empty means all; IDs of removed libraries are kept but match nothing.
/// </summary>
public static class LibraryFilter
{
    /// <summary>
    /// Returns whether an item in <paramref name="libraryIds"/> passes the destination's filter.
    /// </summary>
    /// <param name="destination">The destination.</param>
    /// <param name="libraryIds">The item's libraries.</param>
    /// <returns><c>true</c> when it passes.</returns>
    public static bool Matches(Destination destination, IReadOnlyCollection<Guid> libraryIds)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(libraryIds);
        return destination.LibraryIds.Length == 0 || Parse(destination).Any(libraryIds.Contains);
    }

    /// <summary>
    /// Returns the filter's IDs that no longer name a library.
    /// </summary>
    /// <param name="destination">The destination.</param>
    /// <param name="existing">The existing libraries.</param>
    /// <returns>The removed IDs, as stored.</returns>
    public static IReadOnlyList<string> Removed(Destination destination, IReadOnlyCollection<Guid> existing)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(existing);
        return [.. destination.LibraryIds.Where(id => !Guid.TryParseExact(id, "N", out var g) || !existing.Contains(g))];
    }

    /// <summary>
    /// Returns whether the filter is non-empty and every ID in it names a removed library ("Library filter refers to
    /// removed libraries").
    /// </summary>
    /// <param name="destination">The destination.</param>
    /// <param name="existing">The existing libraries.</param>
    /// <returns><c>true</c> when the filter can match nothing.</returns>
    public static bool RefersOnlyToRemoved(Destination destination, IReadOnlyCollection<Guid> existing)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return destination.LibraryIds.Length > 0 && Removed(destination, existing).Count == destination.LibraryIds.Length;
    }

    private static IEnumerable<Guid> Parse(Destination destination)
    {
        foreach (var id in destination.LibraryIds)
        {
            if (Guid.TryParseExact(id, "N", out var guid))
            {
                yield return guid;
            }
        }
    }
}
