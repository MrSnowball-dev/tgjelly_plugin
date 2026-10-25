namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// One topic route to change in a destination PUT.
/// </summary>
public sealed class TopicRouteRequest
{
    /// <summary>Gets or sets the slot: a media event key, or <c>security</c> or <c>server</c>.</summary>
    public string Slot { get; set; } = string.Empty;

    /// <summary>Gets or sets the topic, or <c>null</c> for the destination's own topic.</summary>
    public int? ThreadId { get; set; }
}
