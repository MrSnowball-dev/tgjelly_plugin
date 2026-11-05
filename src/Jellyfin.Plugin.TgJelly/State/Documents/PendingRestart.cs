using System;

namespace Jellyfin.Plugin.TgJelly.State.Documents;

/// <summary>
/// A restart asked for with a message's Restart button: after the restart, that message is edited in place to
/// "Jellyfin is up" instead of a new message going to its chat.
/// </summary>
public sealed class PendingRestart
{
    /// <summary>Gets or sets the destination the message went to.</summary>
    public string DestinationId { get; set; } = string.Empty;

    /// <summary>Gets or sets the chat.</summary>
    public long ChatId { get; set; }

    /// <summary>Gets or sets the message whose button was pressed.</summary>
    public int MessageId { get; set; }

    /// <summary>Gets or sets a value indicating whether the message is a photo (its caption is edited).</summary>
    public bool Photo { get; set; }

    /// <summary>Gets or sets the Log row of the restart.</summary>
    public string LogId { get; set; } = string.Empty;

    /// <summary>Gets or sets when the button was pressed.</summary>
    public DateTimeOffset At { get; set; }
}
