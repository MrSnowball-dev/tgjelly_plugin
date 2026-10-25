using System;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Serialization;
using System.Xml.Serialization;

namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// A chat (or forum topic) that receives notifications (spec §10.1).
/// </summary>
public class Destination
{
    /// <summary>Gets or sets the 8-character id ([a-z0-9]).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the Telegram chat id.</summary>
    public long ChatId { get; set; }

    /// <summary>Gets or sets the forum topic id; 0 means none or the General topic.</summary>
    public int ThreadId { get; set; }

    /// <summary>Gets or sets the chat title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the chat type.</summary>
    [XmlIgnore]
    public ChatKind Kind { get; set; }

    /// <summary>Gets or sets <see cref="Kind"/> as XML text; an unknown name (from a newer version) reads as <see cref="EnumText.Fallback{T}"/>.</summary>
    [XmlElement(nameof(Kind))]
    [JsonIgnore]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public string KindXml
    {
        get => Kind.ToString();
        set => Kind = EnumText.Parse<ChatKind>(value);
    }

    /// <summary>Gets or sets a value indicating whether the destination is enabled (the user's Pause/Resume).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the chat had topics turned on when it was added; such a supergroup is
    /// shown as a Forum.
    /// </summary>
    public bool IsForum { get; set; }

    /// <summary>Gets the strings key for the chat's kind: <c>dest.kind.Forum</c> for a supergroup with topics.</summary>
    [XmlIgnore]
    [JsonIgnore]
    public string KindKey => Kind == ChatKind.Supergroup && (IsForum || ThreadId != 0) ? "dest.kind.Forum" : "dest.kind." + Kind;

    /// <summary>
    /// Gets or sets a value indicating whether events a later version adds are switched on here too: true while
    /// every event is on.
    /// </summary>
    [XmlIgnore]
    [JsonIgnore]
    public bool FollowsNewEvents { get; set; } = true;

    /// <summary>Gets or sets <see cref="FollowsNewEvents"/> as stored: "Everything" or "Custom".</summary>
    [XmlElement("Preset")]
    [JsonPropertyName("preset")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public string PresetText
    {
        get => FollowsNewEvents ? "Everything" : "Custom";
        set => FollowsNewEvents = string.Equals(value, "Everything", StringComparison.Ordinal);
    }

    /// <summary>Gets or sets, for a forum, the slots that post to their own topic (<see cref="TopicSlots"/>).</summary>
    public TopicRoute[] TopicRoutes { get; set; } = Array.Empty<TopicRoute>();

    /// <summary>
    /// Gets or sets a value indicating whether each media event has its own topic choice; otherwise all media follows
    /// the Media group's topic.
    /// </summary>
    public bool SeparateMediaTopics { get; set; }

    /// <summary>Gets or sets the forum topics TgJelly knows for this chat, with their names when it knows them.</summary>
    public KnownTopic[] KnownTopics { get; set; } = Array.Empty<KnownTopic>();

    /// <summary>Gets or sets the concrete event keys.</summary>
    public string[] Events { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the library ids (Guid "N" format); empty means all.</summary>
    public string[] LibraryIds { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the groups sent without sound (media, security, server).</summary>
    public string[] SilentGroups { get; set; } = Array.Empty<string>();

    /// <summary>
    /// The topic a message for this event key goes to: its slot's route, or the destination's own topic.
    /// </summary>
    /// <param name="eventKey">The event key.</param>
    /// <returns>The thread id; 0 is General.</returns>
    public int ThreadFor(string eventKey)
    {
        var slot = RouteSlotFor(eventKey);
        var route = slot is null ? null : Array.Find(TopicRoutes, r => string.Equals(r.Slot, slot, StringComparison.Ordinal));
        return route?.ThreadId ?? ThreadId;
    }

    /// <summary>
    /// The slot whose route an event key follows: the media event itself with separate media topics, Media
    /// otherwise, or Security or Server; <c>null</c> for the digest and the alert summary, which follow the
    /// destination's own topic.
    /// </summary>
    /// <param name="eventKey">The event key.</param>
    /// <returns>The slot, or <c>null</c>.</returns>
    public string? RouteSlotFor(string eventKey)
    {
        if (!EventCatalog.IsKnown(eventKey))
        {
            return null;
        }

        var group = EventCatalog.GroupOf(eventKey);
        return group switch
        {
            "media" => SeparateMediaTopics ? eventKey : "media",
            "security" or "server" => group,
            _ => null,
        };
    }

    /// <summary>
    /// Adds a topic TgJelly knows, or renames it; an empty name keeps the one already known.
    /// </summary>
    /// <param name="thread">The topic's thread ID.</param>
    /// <param name="name">Its name, or empty when unknown.</param>
    public void RememberTopic(int thread, string name)
    {
        var known = Array.Find(KnownTopics, t => t.ThreadId == thread);
        var topic = new KnownTopic { ThreadId = thread, Name = name.Length > 0 ? name : known?.Name ?? string.Empty };
        KnownTopics = known is null ? [.. KnownTopics, topic] : [.. KnownTopics.Select(t => t == known ? topic : t)];
    }
}
