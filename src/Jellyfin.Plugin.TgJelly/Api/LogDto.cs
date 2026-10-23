using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// A page of the Log for the dashboard (design §8.1).
/// </summary>
public sealed class LogDto
{
    /// <summary>Gets the rows, newest first.</summary>
    public IReadOnlyList<LogEntryDto> Items { get; init; } = [];

    /// <summary>Gets when a message last reached Telegram, as far as the Log knows.</summary>
    public DateTimeOffset? LastSentAt { get; init; }

    /// <summary>Gets when TgJelly last acted on an event from Jellyfin, as far as the Log knows.</summary>
    public DateTimeOffset? LastEventAt { get; init; }

    /// <summary>Gets the value to pass as <c>before</c> for the next, older page; <c>null</c> when there are no older rows.</summary>
    public string? Next { get; init; }

    /// <summary>Gets a value indicating whether the Log file was missing or unreadable when this run started.</summary>
    public bool StartedEmpty { get; init; }
}
