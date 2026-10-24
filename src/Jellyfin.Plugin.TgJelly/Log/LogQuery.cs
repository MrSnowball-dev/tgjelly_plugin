using System;

namespace Jellyfin.Plugin.TgJelly.Log;

/// <summary>
/// Which rows of the event log to return, newest first.
/// </summary>
public sealed class LogQuery
{
    /// <summary>Gets a value indicating whether to return only failed and attention rows, and waiting rows that failed a try.</summary>
    public bool ProblemsOnly { get; init; }

    /// <summary>Gets the destination to show, or <c>null</c> for all.</summary>
    public string? DestinationId { get; init; }

    /// <summary>Gets the event key to show, or <c>null</c> for all.</summary>
    public string? EventKey { get; init; }

    /// <summary>Gets the paging cursor: the <see cref="LogPage.Next"/> of the previous page, or <c>null</c> for the newest rows.</summary>
    public string? Before { get; init; }

    /// <summary>Gets the most rows to return.</summary>
    public int Limit { get; init; } = 100;
}
