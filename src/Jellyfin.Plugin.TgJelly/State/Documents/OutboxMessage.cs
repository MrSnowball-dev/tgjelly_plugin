using System.Collections.Generic;

namespace Jellyfin.Plugin.TgJelly.State.Documents;

/// <summary>
/// The rendered message of an Outbox entry (spec §9.3), plus the fallback flags delivery sets (§9.4).
/// </summary>
public sealed class OutboxMessage
{
    /// <summary>Gets or sets the caption sent with a poster (≤ 1024 characters).</summary>
    public string Caption { get; set; } = string.Empty;

    /// <summary>Gets or sets the text-only layout (≤ 4096 characters).</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Gets or sets the poster file, if any.</summary>
    public string? PosterPath { get; set; }

    /// <summary>Gets or sets the file_id cache key: item ID and image modification time.</summary>
    public string? PosterCacheKey { get; set; }

    /// <summary>Gets or sets the button rows.</summary>
    public List<List<OutboxButton>> Buttons { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether to send without a notification sound.</summary>
    public bool Silent { get; set; }

    /// <summary>Gets or sets the event key whose custom template made this message, or <c>null</c>.</summary>
    public string? TemplateKey { get; set; }

    /// <summary>Gets or sets a value indicating whether to skip the poster (a fallback).</summary>
    public bool ForceTextOnly { get; set; }

    /// <summary>Gets or sets a value indicating whether to leave out the buttons (a fallback).</summary>
    public bool NoButtons { get; set; }

    /// <summary>Gets or sets a value indicating whether to send as plain text (a fallback).</summary>
    public bool PlainText { get; set; }

    /// <summary>Gets or sets a value indicating whether the text was already shortened (a fallback).</summary>
    public bool Truncated { get; set; }
}
