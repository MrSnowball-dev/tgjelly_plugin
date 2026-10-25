using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.Hosting;

/// <summary>
/// Runs a background loop so that an exception can never escape to the host.
/// A failed body is restarted with backoff 1 s → 60 s; the backoff resets after a run of at least 60 s.
/// </summary>
public static class Supervisor
{
    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Runs <paramref name="body"/> until it completes normally or <paramref name="stoppingToken"/> is cancelled.
    /// Never throws.
    /// </summary>
    /// <param name="name">A short name for log messages.</param>
    /// <param name="body">The loop body.</param>
    /// <param name="time">The clock used for backoff delays.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="stoppingToken">Cancelled when the host stops.</param>
    /// <returns>A task that completes when the loop ends.</returns>
    public static async Task RunAsync(
        string name,
        Func<CancellationToken, Task> body,
        TimeProvider time,
        ILogger logger,
        CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        // Return to the caller at once, so StartAsync never runs loop work inline (.NET 9 BackgroundService
        // runs ExecuteAsync synchronously up to its first await; .NET 10 does not).
        await Task.Yield();

        var delay = FirstDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            var startedAt = time.GetUtcNow();
            try
            {
                await body(stoppingToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (time.GetUtcNow() - startedAt >= MaxDelay)
                {
                    delay = FirstDelay;
                }

                logger.LogError(ex, "TgJelly {Name} failed; restarting in {Delay}", name, delay);
            }

            try
            {
                await Task.Delay(delay, time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = delay * 2 > MaxDelay ? MaxDelay : delay * 2;
        }
    }
}
