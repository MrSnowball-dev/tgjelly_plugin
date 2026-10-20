using System;
using Jellyfin.Plugin.TgJelly.Configuration;

namespace Jellyfin.Plugin.TgJelly.State.Documents;

/// <summary>
/// A chat the bot was added to that is not a destination yet.
/// </summary>
public sealed class DetectedChat
{
    /// <summary>Gets or sets the chat ID.</summary>
    public long ChatId { get; set; }

    /// <summary>Gets or sets the chat title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the chat type.</summary>
    public ChatKind Kind { get; set; }

    /// <summary>Gets or sets when the chat was first detected.</summary>
    public DateTimeOffset DetectedAt { get; set; }
}
