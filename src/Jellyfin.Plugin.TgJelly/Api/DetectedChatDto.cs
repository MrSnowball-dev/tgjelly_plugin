using System;

namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// A chat the bot was added to outside setup (spec §6.5).
/// </summary>
public sealed class DetectedChatDto
{
    /// <summary>Gets the chat ID.</summary>
    public long ChatId { get; init; }

    /// <summary>Gets the chat title.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Gets the chat kind: Group, Supergroup or Channel.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>Gets when the bot was first seen in the chat.</summary>
    public DateTimeOffset DetectedAt { get; init; }
}
