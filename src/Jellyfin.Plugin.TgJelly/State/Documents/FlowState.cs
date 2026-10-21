using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TgJelly.State.Documents;

/// <summary>
/// Where a private chat is in the bot's menus and setup flows. Survives restarts so a tap on an old message still works.
/// </summary>
public sealed class FlowState
{
    /// <summary>Gets or sets the screen the chat is on.</summary>
    public string Screen { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the screen's parameters. Keys used: <c>await</c> (chat), <c>chatId</c>, <c>title</c>, <c>kind</c>
    /// (the chat's <c>ChatKind</c>), <c>isForum</c>, <c>photosBlocked</c>, <c>threadId</c>, <c>topic</c>,
    /// <c>promotion</c>, <c>anyChat</c>, <c>creating</c>, <c>failed</c>, <c>events</c> and <c>input</c> (publicUrl,
    /// topicLink).
    /// </summary>
    public Dictionary<string, string> Params { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets the message ID of the wizard message being edited.</summary>
    public int? WizardMessageId { get; set; }

    /// <summary>Gets or sets when the chat prompt was sent; it expires after <c>BotMemory.PromptTtl</c>.</summary>
    public DateTimeOffset? PromptSentAt { get; set; }

    /// <summary>Gets or sets when the flow last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
