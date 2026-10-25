using System;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;

namespace Jellyfin.Plugin.TgJelly.Pipeline;

/// <summary>
/// Switches on the events a later version added for every destination that follows new events.
/// </summary>
public static class NewEventsUpgrader
{
    /// <summary>
    /// Upgrades the destinations in place.
    /// </summary>
    /// <param name="configuration">The settings copy to change.</param>
    /// <returns>Whether anything changed.</returns>
    public static bool Apply(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var changed = false;
        foreach (var d in configuration.Destinations)
        {
            if (!d.FollowsNewEvents || EventCatalog.HasAll(d.Events))
            {
                continue;
            }

            // Catalog order first, then anything else the destination already had.
            d.Events = [.. EventCatalog.AllKeys, .. d.Events.Where(k => !EventCatalog.IsKnown(k))];
            changed = true;
        }

        return changed;
    }
}
