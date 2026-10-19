using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TgJelly.State.Documents;

/// <summary>
/// The data a message is rendered from (spec §9.3): item IDs for media, a payload snapshot for security and server
/// events. Kept on Outbox entries and sent messages, so an update renders the whole message again from the merged
/// model. Plain data only (plan 5 builds template models from it).
/// </summary>
public sealed class RenderModel
{
    /// <summary>Gets or sets what this describes.</summary>
    public RenderKind Kind { get; set; }

    /// <summary>Gets or sets the event key.</summary>
    public string EventKey { get; set; } = string.Empty;

    /// <summary>Gets or sets when it happened (alerts) or when the group started (media).</summary>
    public DateTimeOffset At { get; set; }

    /// <summary>Gets or sets, for media, the items of the group (Guid <c>N</c>), in arrival order.</summary>
    public List<string> ItemIds { get; set; } = [];

    /// <summary>Gets or sets, for media, the series, season, album or folder the group belongs to (Guid <c>N</c>).</summary>
    public string ContainerId { get; set; } = string.Empty;

    /// <summary>Gets or sets, for media, the library name shown in the message.</summary>
    public string LibraryName { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the group's series is new (media).</summary>
    public bool NewSeries { get; set; }

    /// <summary>Gets or sets a value indicating whether every item is an announced upgrade (media).</summary>
    public bool Upgraded { get; set; }

    /// <summary>Gets or sets, for digests, the groups.</summary>
    public List<RenderModel> Groups { get; set; } = [];

    /// <summary>Gets or sets, for alerts, the payload snapshot, keyed by <c>EventFields</c>.</summary>
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets, for alerts, extra lines (restart reasons), already rendered from the string table.</summary>
    public List<string> Lines { get; set; } = [];
}
