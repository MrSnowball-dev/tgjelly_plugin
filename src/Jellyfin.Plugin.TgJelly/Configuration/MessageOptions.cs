using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json.Serialization;
using System.Xml.Serialization;

namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// What messages show (design §6.2): one set of switches per message, keyed by event key, and whether quality upgrades
/// are announced at all.
/// </summary>
public class MessageOptions
{
    /// <summary>The key of the media digest, the one message that is not an event.</summary>
    public const string DigestKey = "media.digest";

    private const string LegacyNote = "v0.4's one set of switches, read once by PluginConfiguration.MigrateMessageOptions. Use For and Change.";

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

    /// <summary>Gets or sets v0.4's poster switch (migration source only).</summary>
    [Obsolete(LegacyNote)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [XmlIgnore]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ShowPoster { get; set; }

    /// <summary>Gets or sets v0.4's overview switch (migration source only).</summary>
    [Obsolete(LegacyNote)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [XmlIgnore]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ShowOverview { get; set; }

    /// <summary>Gets or sets v0.4's rating and genres switch (migration source only).</summary>
    [Obsolete(LegacyNote)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [XmlIgnore]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ShowRatingGenres { get; set; }

    /// <summary>Gets or sets v0.4's "Open in Jellyfin" switch (migration source only).</summary>
    [Obsolete(LegacyNote)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [XmlIgnore]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ShowOpenInJellyfin { get; set; }

    /// <summary>Gets or sets v0.4's IMDb/TMDb switch (migration source only).</summary>
    [Obsolete(LegacyNote)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [XmlIgnore]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ShowProviderLinks { get; set; }

    // XML: .NET 9's XmlSerializer skips [Obsolete] members, so each v0.4 switch is read through a twin under its old
    // element name, and written back only while it is set (the ShouldSerialize methods), i.e. never once the
    // migration has run.
#pragma warning disable CS0618 // migration source
    /// <summary>Gets or sets <see cref="ShowPoster"/> as XML.</summary>
    [XmlElement(nameof(ShowPoster))]
    [JsonIgnore]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool? ShowPosterXml
    {
        get => ShowPoster;
        set => ShowPoster = value;
    }

    /// <summary>Gets or sets <see cref="ShowOverview"/> as XML.</summary>
    [XmlElement(nameof(ShowOverview))]
    [JsonIgnore]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool? ShowOverviewXml
    {
        get => ShowOverview;
        set => ShowOverview = value;
    }

    /// <summary>Gets or sets <see cref="ShowRatingGenres"/> as XML.</summary>
    [XmlElement(nameof(ShowRatingGenres))]
    [JsonIgnore]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool? ShowRatingGenresXml
    {
        get => ShowRatingGenres;
        set => ShowRatingGenres = value;
    }

    /// <summary>Gets or sets <see cref="ShowOpenInJellyfin"/> as XML.</summary>
    [XmlElement(nameof(ShowOpenInJellyfin))]
    [JsonIgnore]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool? ShowOpenInJellyfinXml
    {
        get => ShowOpenInJellyfin;
        set => ShowOpenInJellyfin = value;
    }

    /// <summary>Gets or sets <see cref="ShowProviderLinks"/> as XML.</summary>
    [XmlElement(nameof(ShowProviderLinks))]
    [JsonIgnore]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool? ShowProviderLinksXml
    {
        get => ShowProviderLinks;
        set => ShowProviderLinks = value;
    }
#pragma warning restore CS0618

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

#pragma warning disable CS0618 // migration source
    /// <summary>Tells the XML serializer whether to write <see cref="ShowPosterXml"/>.</summary>
    /// <returns><c>true</c> while it is set.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool ShouldSerializeShowPosterXml() => ShowPoster.HasValue;

    /// <summary>Tells the XML serializer whether to write <see cref="ShowOverviewXml"/>.</summary>
    /// <returns><c>true</c> while it is set.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool ShouldSerializeShowOverviewXml() => ShowOverview.HasValue;

    /// <summary>Tells the XML serializer whether to write <see cref="ShowRatingGenresXml"/>.</summary>
    /// <returns><c>true</c> while it is set.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool ShouldSerializeShowRatingGenresXml() => ShowRatingGenres.HasValue;

    /// <summary>Tells the XML serializer whether to write <see cref="ShowOpenInJellyfinXml"/>.</summary>
    /// <returns><c>true</c> while it is set.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool ShouldSerializeShowOpenInJellyfinXml() => ShowOpenInJellyfin.HasValue;

    /// <summary>Tells the XML serializer whether to write <see cref="ShowProviderLinksXml"/>.</summary>
    /// <returns><c>true</c> while it is set.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool ShouldSerializeShowProviderLinksXml() => ShowProviderLinks.HasValue;
#pragma warning restore CS0618

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
