using System;

namespace Jellyfin.Plugin.TgJelly.State.Documents;

/// <summary>
/// A media group of one destination: the items announced together in one message.
/// </summary>
public sealed class MediaGroupState
{
    /// <summary>Gets or sets the destination.</summary>
    public string DestinationId { get; set; } = string.Empty;

    /// <summary>Gets or sets the group key (<c>GroupKeys</c>).</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets when the group's first item arrived.</summary>
    public DateTimeOffset FirstAt { get; set; }

    /// <summary>Gets or sets when the group's latest item arrived.</summary>
    public DateTimeOffset LastAt { get; set; }

    /// <summary>Gets or sets the model the message is rendered from.</summary>
    public RenderModel Model { get; set; } = new();
}
