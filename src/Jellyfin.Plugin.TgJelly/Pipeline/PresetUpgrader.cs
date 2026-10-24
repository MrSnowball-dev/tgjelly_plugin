using System;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;

namespace Jellyfin.Plugin.TgJelly.Pipeline;

/// <summary>
/// Switches on the events a later version added for every destination that has every event on (internally the
/// Everything preset). Presets are no longer offered (2026-10-25): a destination that used New media or Admin alerts
/// keeps its events and becomes Custom, so nothing new appears in a chat the admin trimmed.
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
            if (d.Preset is PresetKind.NewMedia or PresetKind.AdminAlerts)
            {
                d.Preset = PresetKind.Custom;
                changed = true;
            }

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
