using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Log;

namespace Jellyfin.Plugin.TgJelly.Pipeline;

/// <summary>
/// Picks the destinations for an event: enabled, subscribed by exact key, and for media the
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
        return [.. destinations.Where(d => Explain(eventKey, libraryIds, d) is null)];
    }

    /// <summary>
    /// Says why a destination doesn't get an event, for the Log.
    /// </summary>
    /// <param name="eventKey">The event key.</param>
    /// <param name="libraryIds">For media, the item's libraries; <c>null</c> for other events.</param>
    /// <param name="destination">The destination.</param>
    /// <returns>The reason, or <c>null</c> when the destination gets it.</returns>
    public static SkipReason? Explain(string eventKey, IReadOnlyCollection<Guid>? libraryIds, Destination destination)
    {
        ArgumentNullException.ThrowIfNull(eventKey);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.Enabled)
        {
            return SkipReason.Paused;
        }

        if (!destination.Events.Contains(eventKey, StringComparer.Ordinal))
        {
            return SkipReason.EventOff;
        }

        var media = EventCatalog.GroupOf(eventKey) == "media";
        return media && libraryIds is not null && !LibraryFilter.Matches(destination, libraryIds) ? SkipReason.LibraryExcluded : null;
    }
}
