using System;
using System.ComponentModel;
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
    /// Gets or sets the internal preset: Everything while every event is on (so later versions' events are added),
    /// Custom otherwise (spec §7.2). Not shown anywhere.
    /// </summary>
    [XmlIgnore]
    public PresetKind Preset { get; set; } = PresetKind.Everything;

    /// <summary>Gets or sets <see cref="Preset"/> as XML text; an unknown name (from a newer version) reads as <see cref="EnumText.Fallback{T}"/>.</summary>
    [XmlElement(nameof(Preset))]
    [JsonIgnore]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public string PresetXml
    {
        get => Preset.ToString();
        set => Preset = EnumText.Parse<PresetKind>(value);
    }

    /// <summary>Gets or sets the concrete event keys.</summary>
    public string[] Events { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the library ids (Guid "N" format); empty means all.</summary>
    public string[] LibraryIds { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the groups sent without sound (media, security, server).</summary>
    public string[] SilentGroups { get; set; } = Array.Empty<string>();
}
