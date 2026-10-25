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
    /// Returns whether a set of events has every event in the catalog.
    /// </summary>
    /// <param name="events">The events.</param>
    /// <returns><c>true</c> when none is missing.</returns>
    public static bool HasAll(IReadOnlyCollection<string> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        return Keys.All(k => events.Contains(k, StringComparer.Ordinal));
    }
}
