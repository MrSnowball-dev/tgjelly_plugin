using System;
using System.Collections.Generic;
using System.Threading;

namespace Jellyfin.Plugin.TgJelly.Delivery;

/// <summary>
/// Telegram's rate guidance (spec §9.3, F36): at least 1 s between sends to one chat, at most 20 per minute per group
/// or channel, at most 2 per second overall (owner, 2026-10-25, to stay clear of flood waits), and a chat paused by a 429's <c>retry_after</c>. In memory only.
/// </summary>
public sealed class PacingGate
{
    private const int PerMinuteLimit = 20;
    // At most 2 messages a second overall (owner, 2026-10-25), well under Telegram's 30, so a busy library scan
    // never runs into flood waits.
    private const int PerSecondLimit = 2;

    private static readonly TimeSpan PerChat = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    private readonly Lock _gate = new();
    private readonly Dictionary<long, DateTimeOffset> _lastSend = [];
    private readonly Dictionary<long, DateTimeOffset> _pausedUntil = [];
    private readonly Dictionary<long, Queue<DateTimeOffset>> _recentPerChat = [];
    private readonly Queue<DateTimeOffset> _recent = new();

    /// <summary>
    /// Returns the earliest time a message may be sent to a chat.
    /// </summary>
    /// <param name="chatId">The chat.</param>
    /// <param name="groupOrChannel">Whether the per-minute group limit applies.</param>
    /// <param name="now">The current time.</param>
    /// <returns><paramref name="now"/> or later.</returns>
    public DateTimeOffset NextAllowed(long chatId, bool groupOrChannel, DateTimeOffset now)
    {
        lock (_gate)
        {
            var at = now;
            if (_lastSend.TryGetValue(chatId, out var last))
            {
                at = Max(at, last + PerChat);
            }

            if (_pausedUntil.TryGetValue(chatId, out var paused))
            {
                at = Max(at, paused);
            }

            if (groupOrChannel && _recentPerChat.TryGetValue(chatId, out var perChat))
            {
                Prune(perChat, now - Minute);
                if (perChat.Count >= PerMinuteLimit)
                {
                    at = Max(at, perChat.Peek() + Minute);
                }
            }

            Prune(_recent, now - Second);
            if (_recent.Count >= PerSecondLimit)
            {
                at = Max(at, _recent.Peek() + Second);
            }

            return at;
        }
    }

    /// <summary>
    /// Records a send (or an edit; both count toward pacing).
    /// </summary>
    /// <param name="chatId">The chat.</param>
    /// <param name="now">When.</param>
    public void RecordSend(long chatId, DateTimeOffset now)
    {
        lock (_gate)
        {
            _lastSend[chatId] = now;
            if (!_recentPerChat.TryGetValue(chatId, out var perChat))
            {
                perChat = new Queue<DateTimeOffset>();
                _recentPerChat[chatId] = perChat;
            }

            perChat.Enqueue(now);
            Prune(perChat, now - Minute);
            _recent.Enqueue(now);
            Prune(_recent, now - Second);
        }
    }

    /// <summary>
    /// Pauses a chat, after a 429.
    /// </summary>
    /// <param name="chatId">The chat.</param>
    /// <param name="until">Until when.</param>
    public void Pause(long chatId, DateTimeOffset until)
    {
        lock (_gate)
        {
            _pausedUntil[chatId] = until;
        }
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static void Prune(Queue<DateTimeOffset> times, DateTimeOffset olderThanOrAt)
    {
        while (times.Count > 0 && times.Peek() <= olderThanOrAt)
        {
            times.Dequeue();
        }
    }
}
