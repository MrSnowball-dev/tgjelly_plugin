using System;
using System.Collections.Generic;
using Jellyfin.Plugin.TgJelly.State.Documents;

namespace Jellyfin.Plugin.TgJelly.Bot;

/// <summary>
/// What a bot switch or a disconnect clears and keeps (spec §10.4). Plan 4 adds its bot-scoped sections here.
/// </summary>
public static class BotStateReset
{
    /// <summary>
    /// A different bot: clears what belonged to the old bot and marks every destination Lost until the new bot is
    /// added to its chat. Settings, the Outbox (held while Lost) and the bot identity fields are left to the caller.
    /// </summary>
    /// <param name="state">The state document.</param>
    /// <param name="queue">The queue document.</param>
    /// <param name="destinationIds">Every destination.</param>
    /// <param name="lostMessage">The health message, e.g. "bot changed: add @new to this chat".</param>
    /// <param name="now">The current time.</param>
    public static void ForDifferentBot(StateDocument state, QueueDocument queue, IEnumerable<string> destinationIds, string lostMessage, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(destinationIds);
        queue.SentMessages.Clear();
        queue.FileIdCache.Clear();
        state.Telegram.Profile = new ProfileState();
        foreach (var id in destinationIds)
        {
            state.Health[id] = new DestinationHealth { Status = HealthStatus.Lost, LastError = lostMessage, At = now };
        }
    }

    /// <summary>
    /// A disconnect: clears the bot-scoped sections and the Outbox, and keeps the bot identity so reconnecting the
    /// same bot isn't treated as a switch.
    /// </summary>
    /// <param name="state">The state document.</param>
    /// <param name="queue">The queue document.</param>
    public static void ForDisconnect(StateDocument state, QueueDocument queue)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(queue);
        state.Health.Clear();
        queue.SentMessages.Clear();
        queue.Outbox.Clear();
        queue.FileIdCache.Clear();
        state.Telegram.Profile = new ProfileState();
    }
}
