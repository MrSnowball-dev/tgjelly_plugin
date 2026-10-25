using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// What messages show: one set of switches per message, keyed by event key, and whether quality upgrades
/// are announced at all.
/// </summary>
public class MessageOptions
{
    /// <summary>The key of the media digest, the one message that is not an event.</summary>
    public const string DigestKey = "media.digest";

    /// <summary>
    /// Gets every message key: <see cref="EventCatalog.AllKeys"/> followed by <see cref="DigestKey"/>.
    /// </summary>
    public static IReadOnlyList<string> Keys { get; } = [.. EventCatalog.AllKeys, DigestKey];

    /// <summary>
    /// Gets or sets the switches of each message that has been changed or migrated. A message with no entry uses
    /// <see cref="MessageSwitches.DefaultFor"/>. An array, not a dictionary, so that Jellyfin can store it as XML.
    /// </summary>
    public MessageSwitchesEntry[] PerMessage { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether quality upgrades are announced. One value for every media message:
    /// MediaReadiness decides whether an upgrade is an event before any message is picked.
    /// </summary>
    public bool AnnounceUpgrades { get; set; }

    /// <summary>
    /// Returns whether a key names a message TgJelly sends.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns><c>true</c> for an event key or <see cref="DigestKey"/>.</returns>
    public static bool IsKey(string? key) => key is not null && (key == DigestKey || EventCatalog.IsKnown(key));

    /// <summary>
    /// Returns a copy of a message's switches: its entry, or its defaults when it has none.
    /// </summary>
    /// <param name="eventKey">The event key, or <see cref="DigestKey"/>.</param>
    /// <returns>A copy; changing it changes nothing saved (use <see cref="Change"/>).</returns>
    public MessageSwitches For(string eventKey)
    {
        ArgumentNullException.ThrowIfNull(eventKey);
        return Find(eventKey)?.Switches?.Copy() ?? MessageSwitches.DefaultFor(eventKey);
    }

    /// <summary>
    /// Changes a message's switches, adding its entry (from its defaults) when it has none.
    /// </summary>
    /// <param name="eventKey">The event key, or <see cref="DigestKey"/>.</param>
    /// <param name="change">The change.</param>
    public void Change(string eventKey, Action<MessageSwitches> change)
    {
        ArgumentNullException.ThrowIfNull(eventKey);
        ArgumentNullException.ThrowIfNull(change);
        var switches = For(eventKey);
        change(switches);
        if (Find(eventKey) is { } entry)
        {
            entry.Switches = switches;
        }
        else
        {
            PerMessage = [.. PerMessage ?? [], new MessageSwitchesEntry { EventKey = eventKey, Switches = switches }];
        }
    }

    private MessageSwitchesEntry? Find(string eventKey)
    {
        foreach (var entry in PerMessage ?? [])
        {
            if (entry is not null && string.Equals(entry.EventKey, eventKey, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return null;
    }
}
