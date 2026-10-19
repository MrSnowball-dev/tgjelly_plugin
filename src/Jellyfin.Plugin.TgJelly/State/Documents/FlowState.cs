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
    /// Gets or sets the screen's parameters. Keys used: <c>kind</c> (channel, group, here, topic), <c>chatId</c>,
    /// <c>title</c>, <c>threadId</c>, <c>destinationId</c> and <c>input</c> (publicUrl, topicLink).
    /// </summary>
    public Dictionary<string, string> Params { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets the message ID of the wizard message being edited.</summary>
    public int? WizardMessageId { get; set; }

    /// <summary>Gets or sets the message ID of the prompt that carries the chat picker.</summary>
    public int? PromptMessageId { get; set; }

    /// <summary>Gets or sets the message ID of the "checking" message.</summary>
    public int? CheckingMessageId { get; set; }

    /// <summary>Gets or sets when the prompt was sent.</summary>
    public DateTimeOffset? PromptSentAt { get; set; }

    /// <summary>Gets or sets the request IDs of the pickers on the prompt.</summary>
    public List<int> PickerRequestIds { get; set; } = [];

    /// <summary>Gets or sets when the flow last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
