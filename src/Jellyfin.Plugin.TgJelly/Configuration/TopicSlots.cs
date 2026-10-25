using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// The slots a forum destination can route to their own topics (owner, 2026-10-25): each media event on its own,
/// Security and Server as groups. The digest and the alert summary mix kinds, so they follow the destination's topic.
/// </summary>
public static class TopicSlots
{
    /// <summary>Gets every slot, in the editor's order.</summary>
    public static IReadOnlyList<string> All { get; } =
        [.. EventCatalog.AllKeys.Where(k => EventCatalog.GroupOf(k) == "media"), "security", "server"];

    /// <summary>
    /// The slot an event key belongs to, or <c>null</c> when it always follows the destination's own topic.
    /// </summary>
    /// <param name="eventKey">The event key (or template key such as <c>media.digest</c>).</param>
    /// <returns>The slot, or <c>null</c>.</returns>
    public static string? For(string eventKey)
    {
        ArgumentNullException.ThrowIfNull(eventKey);
        if (All.Contains(eventKey, StringComparer.Ordinal))
        {
            return eventKey;
        }

        // The rolling alert summary (server.summary) mixes security and server events, so it isn't a slot.
        var group = EventCatalog.GroupOf(eventKey);
        return group is "security" or "server" && EventCatalog.IsKnown(eventKey) ? group : null;
    }
}
