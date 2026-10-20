using System;
using System.Collections.Immutable;
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
    public static readonly ImmutableArray<TimeSpan> Schedule = [TimeSpan.Zero, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5)];

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
    /// <exception cref="Exception">The last try threw (earlier tries that threw count as failed).</exception>
    /// <exception cref="BotNotConnectedException">No bot is connected.</exception>
    public async Task<ChatCheckResult> CheckAsync(long chatId, bool retry, CancellationToken ct)
    {
        var tries = retry ? Schedule.Length : 1;
        ChatCheckResult? result = null;
        for (var i = 0; i < tries; i++)
        {
            if (i > 0)
            {
                await Delay(Schedule[i] - Schedule[i - 1], ct).ConfigureAwait(false);
            }

            try
            {
                result = await _checker.CheckAsync(chatId, 0, ct).ConfigureAwait(false);
                if (result.Passed)
                {
                    break;
                }
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or BotNotConnectedException) && i < tries - 1)
            {
                // A transient error is one failed try; the last try's error is thrown.
            }
        }

        return result!;
    }

    /// <summary>
    /// Runs an attempt on the same <see cref="Schedule"/>, stopping at the first success: a bot's rights may settle
    /// after the update that changed them.
    /// </summary>
    /// <param name="attempt">The attempt; <c>true</c> when it succeeded.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns><c>true</c> when an attempt succeeded.</returns>
    public async Task<bool> UntilAsync(Func<Task<bool>> attempt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        var done = await attempt().ConfigureAwait(false);
        for (var i = 1; i < Schedule.Length && !done; i++)
        {
            await Delay(Schedule[i] - Schedule[i - 1], ct).ConfigureAwait(false);
            done = await attempt().ConfigureAwait(false);
        }

        return done;
    }
}
