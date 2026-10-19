using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.Bot;

/// <summary>
/// The bounded queue between the poll loop and the <see cref="BotWorker"/>: the poll loop only enqueues, so a slow
/// screen never delays confirming updates.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "It is a queue; the name is part of the plan 4 contract.")]
public sealed class BotWorkQueue
{
    /// <summary>The most items that wait; further ones are dropped.</summary>
    public const int Capacity = 500;

    private readonly Lock _gate = new();
    private readonly Queue<(string Name, Func<CancellationToken, Task> Work)> _items = new();
    private readonly AsyncSignal _signal = new();
    private readonly ILogger<BotWorkQueue> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BotWorkQueue"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public BotWorkQueue(ILogger<BotWorkQueue> logger) => _logger = logger;

    /// <summary>
    /// Adds an item. When the queue is full the new item is dropped and a warning is logged.
    /// </summary>
    /// <param name="name">A short name for log messages.</param>
    /// <param name="work">The work.</param>
    /// <returns><c>true</c> when queued.</returns>
    public bool Enqueue(string name, Func<CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            if (_items.Count >= Capacity)
            {
                _logger.LogWarning("TgJelly's bot work queue is full; dropped {Item}", name);
                return false;
            }

            _items.Enqueue((name, work));
        }

        _signal.Set();
        return true;
    }

    /// <summary>
    /// Waits until an item is queued or the timeout passes.
    /// </summary>
    /// <param name="timeout">The longest wait.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>A task.</returns>
    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, TimeProvider.System, ct);

    /// <summary>
    /// Runs the queued items in order until none is left, or <paramref name="ct"/> is cancelled. A failing item is
    /// reported through <paramref name="onError"/> (or logged here when none is given) and the next one still runs.
    /// </summary>
    /// <param name="ct">Cancels the run; the running item sees it too.</param>
    /// <param name="onError">Called with the item name and exception when an item fails.</param>
    /// <returns>A task.</returns>
    internal async Task RunPendingAsync(CancellationToken ct, Action<string, Exception>? onError = null)
    {
        while (!ct.IsCancellationRequested)
        {
            (string Name, Func<CancellationToken, Task> Work) item;
            lock (_gate)
            {
                if (!_items.TryDequeue(out item))
                {
                    return;
                }
            }

            try
            {
                await item.Work(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (onError is null)
                {
                    _logger.LogError(ex, "TgJelly's bot work item {Item} failed", item.Name);
                }
                else
                {
                    onError(item.Name, ex);
                }
            }
        }
    }
}
