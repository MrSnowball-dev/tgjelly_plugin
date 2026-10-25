using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Strings;

namespace Jellyfin.Plugin.TgJelly.Templates;

/// <summary>
/// Every key a template can be written for: the event keys and the digest.
/// </summary>
public static class TemplateKeys
{
    /// <summary>The key of the media digest.</summary>
    public const string Digest = "media.digest";

    /// <summary>
    /// Gets every template key: <see cref="EventCatalog.AllKeys"/> followed by <see cref="Digest"/>.
    /// </summary>
    public static IReadOnlyList<string> All { get; } = [.. EventCatalog.AllKeys, Digest];

    /// <summary>
    /// Names a template key as the dashboard, the notices and the last-resort text show it.
    /// </summary>
    /// <param name="strings">The string table.</param>
    /// <param name="key">The template key.</param>
    /// <returns>The event's name, or "Digest" for <see cref="Digest"/>.</returns>
    public static string Label(StringTable strings, string key)
    {
        ArgumentNullException.ThrowIfNull(strings);
        ArgumentNullException.ThrowIfNull(key);
        return strings.Get(key == Digest ? "templates.digestLabel" : "dest.event." + key);
    }

    /// <summary>
    /// Names a template key with its group, for text read away from its group: the last-resort message posted into a
    /// chat and the notices ("Server: Started").
    /// </summary>
    /// <param name="strings">The string table.</param>
    /// <param name="key">The template key.</param>
    /// <returns>"Group: Event", or "Digest" for <see cref="Digest"/>.</returns>
    public static string FullLabel(StringTable strings, string key)
    {
        ArgumentNullException.ThrowIfNull(strings);
        ArgumentNullException.ThrowIfNull(key);
        return key == Digest
            ? strings.Get("templates.digestLabel")
            : strings.Get("web.event.group." + Configuration.EventCatalog.GroupOf(key)) + ": " + strings.Get("dest.event." + key);
    }
}
