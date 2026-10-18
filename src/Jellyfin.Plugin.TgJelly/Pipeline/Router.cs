using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;

namespace Jellyfin.Plugin.TgJelly.Pipeline;

/// <summary>
/// Picks the destinations for an event (spec §5.3, §7.2, §7.3): enabled, subscribed by exact key, and for media the
/// library filter. Pure.
/// </summary>
public static class Router
{
    /// <summary>
    /// Returns the destinations that get an event.
    /// </summary>
    /// <param name="eventKey">The event key.</param>
    /// <param name="libraryIds">For media, the item's libraries; <c>null</c> for other events.</param>
    /// <param name="destinations">All destinations.</param>
    /// <returns>The matching destinations, in their configured order.</returns>
    public static IReadOnlyList<Destination> Route(string eventKey, IReadOnlyCollection<Guid>? libraryIds, IReadOnlyList<Destination> destinations)
    {
        ArgumentNullException.ThrowIfNull(eventKey);
        ArgumentNullException.ThrowIfNull(destinations);
        var media = EventCatalog.GroupOf(eventKey) == "media";
        return [.. destinations.Where(d => d.Enabled
            && d.Events.Contains(eventKey, StringComparer.Ordinal)
            && (!media || libraryIds is null || LibraryFilter.Matches(d, libraryIds)))];
    }
}
