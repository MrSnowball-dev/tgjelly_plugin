using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.TgJelly.Hosting;

/// <summary>
/// A wake-up signal for background loops: <see cref="Set"/> wakes the current or next <see cref="WaitAsync"/>,
/// which otherwise returns after a timeout measured on the injected <see cref="TimeProvider"/>.
/// </summary>
public sealed class AsyncSignal
{
    private readonly Lock _gate = new();
    private TaskCompletionSource _signal = NewSource();

    /// <summary>
    /// Wakes the waiter. Signals coalesce: several calls before a wait wake it once.
    /// </summary>
    public void Set()
    {
        lock (_gate)
        {
            _signal.TrySetResult();
        }
    }

    /// <summary>
    /// Waits for <see cref="Set"/> or the timeout, whichever comes first, and resets the signal.
    /// </summary>
    /// <param name="timeout">The longest wait.</param>
    /// <param name="time">The clock the timeout runs on.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns><c>true</c> when woken by <see cref="Set"/>.</returns>
    public async Task<bool> WaitAsync(TimeSpan timeout, TimeProvider time, CancellationToken ct)
    {
        Task signal;
        lock (_gate)
        {
            signal = _signal.Task;
        }

        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var delay = Task.Delay(timeout, time, cts.Token);
            await Task.WhenAny(signal, delay).ConfigureAwait(false);
            await cts.CancelAsync().ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_signal.Task.IsCompleted)
            {
                return false;
            }

            _signal = NewSource();
            return true;
        }
    }

    private static TaskCompletionSource NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
