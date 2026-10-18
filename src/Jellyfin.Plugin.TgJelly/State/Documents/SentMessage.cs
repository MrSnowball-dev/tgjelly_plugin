using System;

namespace Jellyfin.Plugin.TgJelly.State.Documents;

/// <summary>
/// A sent message that may be edited or replied to later (spec §10.3 <c>sentMessages</c>).
/// </summary>
public sealed class SentMessage
{
    /// <summary>Gets or sets the Telegram message ID.</summary>
    public int MessageId { get; set; }

    /// <summary>Gets or sets whether it's a photo or a text message.</summary>
    public SentKind Kind { get; set; }

    /// <summary>Gets or sets when it was sent.</summary>
    public DateTimeOffset SentAt { get; set; }

    /// <summary>Gets or sets when it was last edited.</summary>
    public DateTimeOffset? LastEditAt { get; set; }

    /// <summary>Gets or sets the event key it was sent for.</summary>
    public string EventKey { get; set; } = string.Empty;
}
