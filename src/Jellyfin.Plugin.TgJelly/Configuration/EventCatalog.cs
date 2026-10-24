using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// The event keys (spec §7.1, stable strings) and the internal presets (§7.2: Everything while every event is on).
/// </summary>
public static class EventCatalog
{
    private static readonly string[] Keys =
    [
        "media.movie", "media.episodes", "media.album", "media.musicvideo", "media.book",
        "security.login_failed", "security.lockout", "security.login_succeeded", "security.user_created",
        "security.user_deleted", "security.password_changed",
        "server.started", "server.update_available", "server.plugin_installed", "server.plugin_updated",
        "server.plugin_uninstalled", "server.plugin_install_failed", "server.task_failed", "server.restart_pending",
    ];

    private static readonly HashSet<string> KeySet = new(Keys, StringComparer.Ordinal);

    /// <summary>
    /// Gets every event key, in catalog order.
    /// </summary>
    public static IReadOnlyList<string> AllKeys => Keys;

    /// <summary>
    /// Gets the event groups, which are also the silent-group names (§8.4).
    /// </summary>
    public static IReadOnlyList<string> Groups { get; } = ["media", "security", "server"];

    /// <summary>
    /// Returns the group of an event key: the part before the first dot.
    /// </summary>
    /// <param name="eventKey">The event key.</param>
    /// <returns>The group.</returns>
    public static string GroupOf(string eventKey)
    {
        ArgumentNullException.ThrowIfNull(eventKey);
        var dot = eventKey.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? eventKey : eventKey[..dot];
    }

    /// <summary>
    /// Returns whether <paramref name="key"/> is a known event key.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns><c>true</c> when known.</returns>
    public static bool IsKnown(string key) => KeySet.Contains(key);

    /// <summary>
    /// The internal preset for a set of events: <see cref="PresetKind.Everything"/> when it has every event, so an event a
    /// later version adds is switched on too; <see cref="PresetKind.Custom"/> otherwise. Presets aren't shown anywhere.
    /// </summary>
    /// <param name="events">The destination's events.</param>
    /// <returns>The preset.</returns>
    public static PresetKind PresetFor(IReadOnlyCollection<string> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        return Keys.All(k => events.Contains(k, StringComparer.Ordinal)) ? PresetKind.Everything : PresetKind.Custom;
    }

    /// <summary>
    /// Expands a preset to its concrete keys; <see cref="PresetKind.Custom"/> expands to none.
    /// </summary>
    /// <param name="preset">The preset.</param>
    /// <returns>The keys, in catalog order.</returns>
    public static string[] Expand(PresetKind preset) => preset switch
    {
        PresetKind.Everything => [.. Keys],
        PresetKind.NewMedia => [.. Keys.Where(k => GroupOf(k) == "media")],
        PresetKind.AdminAlerts => [.. Keys.Where(k => GroupOf(k) == "server" || (GroupOf(k) == "security" && k != "security.login_succeeded"))],
        _ => [],
    };
}
