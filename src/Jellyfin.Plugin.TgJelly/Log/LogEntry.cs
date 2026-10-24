using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TgJelly.Log;

/// <summary>
/// One row of the event log: one message for one destination, or an event no destination got (design §8.1).
/// </summary>
public sealed record LogEntry
{
    /// <summary>Gets the row ID: a media group's key, destination and start, or the Outbox entry's ID.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets when the row was first recorded; rows are listed and kept by this time.</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>Gets when the row last changed.</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Gets the message's event key, e.g. <c>media.movie</c>.</summary>
    public string EventKey { get; init; } = string.Empty;

    /// <summary>Gets what the message is about, e.g. "The Bear, S04E01–E04".</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Gets the destination's ID, or <c>null</c> when no destination was involved.</summary>
    public string? DestinationId { get; init; }

    /// <summary>Gets the destination's name when the row was written.</summary>
    public string? DestinationTitle { get; init; }

    /// <summary>Gets the outcome.</summary>
    public LogResult Result { get; init; }

    /// <summary>Gets the plain reasons, most serious first; empty for a plain <see cref="LogResult.Sent"/>.</summary>
    public IReadOnlyList<string> Details { get; init; } = [];

    /// <summary>Gets what Telegram or the network said last, verbatim and redacted.</summary>
    public string? Telegram { get; init; }

    /// <summary>Gets how many times delivery was tried.</summary>
    public int Tries { get; init; }

    /// <summary>Gets when the next try is due, while it waits to retry.</summary>
    public DateTimeOffset? RetryAt { get; init; }

    /// <summary>
    /// Gets, for a Failed row whose message is still queued and held until its destination works again (Lost and the
    /// like), when the Outbox drops it; <c>null</c> otherwise.
    /// </summary>
    public DateTimeOffset? HeldUntil { get; init; }

    /// <summary>Gets what to do about it, for failed and attention rows.</summary>
    public string? Advice { get; init; }
}
