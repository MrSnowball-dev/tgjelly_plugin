using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TgJelly.Log;

/// <summary>
/// A page of the event log.
/// </summary>
/// <param name="Items">The rows, newest first.</param>
/// <param name="LastSentAt">When a message last reached Telegram, as far as the log knows.</param>
/// <param name="LastEventAt">When the newest row was recorded: the last event TgJelly acted on.</param>
/// <param name="StartedEmpty">Whether the log file was missing or unreadable when this run started.</param>
/// <param name="Next">The cursor for the next (older) page, or <c>null</c> when there are no older rows.</param>
/// <param name="StartedUnreadable">Whether the log file was unreadable or written by a newer version when this run started; a missing file is not.</param>
public sealed record LogPage(IReadOnlyList<LogEntry> Items, DateTimeOffset? LastSentAt, DateTimeOffset? LastEventAt, bool StartedEmpty, string? Next = null, bool StartedUnreadable = false);
