using System;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;

namespace Jellyfin.Plugin.TgJelly.Pipeline;

/// <summary>
/// Adds the keys a later version added to a preset to every destination using that preset (spec §7.2).
/// </summary>
public static class PresetUpgrader
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
            if (d.Preset == PresetKind.Custom)
            {
                continue;
            }

            var expanded = EventCatalog.Expand(d.Preset);
            if (expanded.All(k => d.Events.Contains(k, StringComparer.Ordinal)))
            {
                continue;
            }

            // Catalog order first, then anything else the destination already had.
            d.Events = [.. expanded, .. d.Events.Where(k => !expanded.Contains(k, StringComparer.Ordinal))];
            changed = true;
        }

        return changed;
    }
}
