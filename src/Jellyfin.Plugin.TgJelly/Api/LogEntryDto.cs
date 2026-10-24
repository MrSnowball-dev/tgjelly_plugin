using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// One Log row: one message for one destination, or an event no destination got.
/// </summary>
public sealed class LogEntryDto
{
    /// <summary>Gets the row ID; stable while the row moves from Waiting to its result.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets when the row was first recorded.</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>Gets when the row last changed.</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Gets the message's event key, e.g. <c>media.movie</c>; <c>server.summary</c> for the rolling summary and <c>media</c> for an item removed before it was ready.</summary>
    public string EventKey { get; init; } = string.Empty;

    /// <summary>Gets what the message is about.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Gets the destination's ID, or <c>null</c> for a row without a destination.</summary>
    public string? DestinationId { get; init; }

    /// <summary>Gets the destination's name.</summary>
    public string? DestinationTitle { get; init; }

    /// <summary>Gets the result: Waiting, Skipped, Sent, SentLate, SentWithBuiltIn, SentWithoutPhoto or Failed.</summary>
    public string Result { get; init; } = string.Empty;

    /// <summary>Gets the plain reasons, most serious first; empty for Sent.</summary>
    public IReadOnlyList<string> Details { get; init; } = [];

    /// <summary>Gets what Telegram or the network said, verbatim.</summary>
    public string? Telegram { get; init; }

    /// <summary>Gets how many times delivery was tried.</summary>
    public int Tries { get; init; }

    /// <summary>Gets when the next try is due, while it waits to retry.</summary>
    public DateTimeOffset? RetryAt { get; init; }

    /// <summary>Gets, for a Failed message still held until its destination works again, when it is dropped if it hasn't gone out.</summary>
    public DateTimeOffset? HeldUntil { get; init; }

    /// <summary>Gets what to do about it.</summary>
    public string? Advice { get; init; }
}
