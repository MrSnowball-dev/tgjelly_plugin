namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// Where one kind of message goes in a forum (owner, 2026-10-25): a slot and the topic it posts to. A slot without a
/// route posts to the destination's own topic.
/// </summary>
public class TopicRoute
{
    /// <summary>Gets or sets the slot: a media event key (<c>media.movie</c>), or <c>security</c> or <c>server</c>.</summary>
    public string Slot { get; set; } = string.Empty;

    /// <summary>Gets or sets the topic's thread id; 0 is General.</summary>
    public int ThreadId { get; set; }
}
