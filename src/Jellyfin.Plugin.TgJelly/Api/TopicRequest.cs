namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// Gives a slot of a forum destination its own topic: a new one TgJelly creates, or an existing one by its link.
/// </summary>
public sealed class TopicRequest
{
    /// <summary>Gets or sets the slot: a media event key, or <c>security</c> or <c>server</c>.</summary>
    public string Slot { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether TgJelly creates a topic named after the slot.</summary>
    public bool Create { get; set; }

    /// <summary>Gets or sets a link to an existing topic of the chat (<c>https://t.me/c/…/123</c>), when not creating.</summary>
    public string? Link { get; set; }

    /// <summary>
    /// Gets or sets the revision the page knew. Not checked: once Telegram has the topic, it is recorded on the current
    /// settings.
    /// </summary>
    public long? ExpectedRevision { get; set; }
}
