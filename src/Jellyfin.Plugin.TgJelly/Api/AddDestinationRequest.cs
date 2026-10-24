namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// <c>POST TgJelly/Destinations</c>: add a chat by ID or by topic link (spec §6.5, Advanced).
/// </summary>
public sealed class AddDestinationRequest
{
    /// <summary>Gets or sets the revision the change is based on.</summary>
    public long ExpectedRevision { get; set; }

    /// <summary>Gets or sets the chat ID; exactly one of this and <see cref="TopicLink"/>.</summary>
    public long? ChatId { get; set; }

    /// <summary>Gets or sets a forum topic's link.</summary>
    public string? TopicLink { get; set; }
}
