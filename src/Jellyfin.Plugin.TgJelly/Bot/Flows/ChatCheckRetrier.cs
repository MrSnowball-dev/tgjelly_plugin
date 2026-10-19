using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Destinations;

namespace Jellyfin.Plugin.TgJelly.Bot.Flows;

/// <summary>
/// Checks a chat that was just picked, trying again a few times: the bot's <c>my_chat_member</c> may arrive after the
/// <c>chat_shared</c>, so the first <c>getChatMember</c> can still say the bot isn't there (spec §6.2 step 2).
/// </summary>
public sealed class ChatCheckRetrier
{
    /// <summary>When the tries happen, as offsets from the first one.</summary>
    public static readonly TimeSpan[] Schedule = [TimeSpan.Zero, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5)];

    private readonly DestinationChecker _checker;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChatCheckRetrier"/> class.
    /// </summary>
    /// <param name="checker">The destination checker.</param>
    /// <param name="time">The clock the waits run on.</param>
    public ChatCheckRetrier(DestinationChecker checker, TimeProvider time)
    {
        _checker = checker;
        Delay = (delay, ct) => Task.Delay(delay, time, ct);
    }

    /// <summary>Gets or sets the wait between tries; tests replace it.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; }

    /// <summary>
    /// Checks a chat, stopping at the first pass.
    /// </summary>
    /// <param name="chatId">The chat.</param>
    /// <param name="retry">Whether to follow the whole <see cref="Schedule"/>; otherwise one try.</param>
    /// <param name="ct">Cancels the check.</param>
    /// <returns>The first passing result, or the last failing one.</returns>
    /// <exception cref="BotNotConnectedException">No bot is connected.</exception>
    public async Task<ChatCheckResult> CheckAsync(long chatId, bool retry, CancellationToken ct)
    {
        var tries = retry ? Schedule.Length : 1;
        var result = await _checker.CheckAsync(chatId, 0, ct).ConfigureAwait(false);
        for (var i = 1; i < tries && !result.Passed; i++)
        {
            await Delay(Schedule[i] - Schedule[i - 1], ct).ConfigureAwait(false);
            result = await _checker.CheckAsync(chatId, 0, ct).ConfigureAwait(false);
        }

        return result;
    }
}
