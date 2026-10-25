using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// <c>PUT TgJelly/Destinations/{id}</c>: only the given fields change.
/// </summary>
[SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "A JSON request body.")]
public sealed class UpdateDestinationRequest
{
    /// <summary>Gets or sets the revision the change is based on.</summary>
    public long ExpectedRevision { get; set; }

    /// <summary>
    /// Gets or sets topic routes to change, by slot: a thread id, or <c>null</c> to send that slot to the destination's
    /// own topic again. Slots not listed keep their route.
    /// </summary>
    public IReadOnlyList<TopicRouteRequest>? TopicRoutes { get; set; }

    /// <summary>Gets or sets whether each media event has its own topic choice; <c>null</c> keeps it.</summary>
    public bool? SeparateMediaTopics { get; set; }

    /// <summary>Gets or sets the events; whether every event is on is worked out from them.</summary>
    public string[]? Events { get; set; }

    /// <summary>Gets or sets the library filter (Guids; empty means all).</summary>
    public string[]? LibraryIds { get; set; }

    /// <summary>Gets or sets the silent groups.</summary>
    public string[]? SilentGroups { get; set; }

    /// <summary>Gets or sets whether the destination is enabled (not paused).</summary>
    public bool? Enabled { get; set; }
}
