using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Compat;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Bot;

public sealed class BotWorkerTests : IDisposable
{
    private static readonly CompatResult Compatible = new(true, "12", new Version(12, 2, 0, 0), null);
    private readonly BotTestHarness _h = new();
    private readonly ListLogger<BotWorkQueue> _queueLog = new();
    private readonly ListLogger<BotWorker> _log = new();
    private readonly RecordingBotFlows _flows = new();
    private readonly ConcurrentQueue<string> _ran = new();
    private readonly BotWorkQueue _queue;

    public BotWorkerTests() => _queue = new BotWorkQueue(_queueLog);

    [Fact]
    public async Task RunsInOrder()
    {
        using var worker = await StartedAsync(Compatible);
        Enqueue("a");
        Enqueue("b");
        Enqueue("c");

        await Eventually.TrueAsync(() => _ran.Count == 3, "the items ran");
        Assert.Equal(["a", "b", "c"], _ran.ToArray());
        await worker.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ThrowingItem_IsLoggedAndTheNextRuns()
    {
        using var worker = await StartedAsync(Compatible);
        Assert.True(_queue.Enqueue("bad", _ => throw new InvalidOperationException("kaput")));
        Enqueue("next");

        await Eventually.TrueAsync(() => _ran.Count == 1, "the next item ran");
        Assert.Equal(1, _log.Count(LogLevel.Error));
        Assert.Contains(_h.State.LastErrorsSnapshot(), e => e.Source == "bot" && e.Message.Contains("kaput", StringComparison.Ordinal));
        await worker.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Full_DropsNew()
    {
        for (var i = 0; i < BotWorkQueue.Capacity; i++)
        {
            Assert.True(Enqueue("i" + i));
        }

        Assert.False(Enqueue("overflow"));
        Assert.Equal(1, _queueLog.Count(LogLevel.Warning));

        await _queue.RunPendingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(BotWorkQueue.Capacity, _ran.Count);
        Assert.DoesNotContain("overflow", _ran);
        Assert.True(Enqueue("after"));
    }

    [Fact]
    public async Task Stop_RunsWhatIsQueued_WithinTwoSeconds()
    {
        using var worker = await StartedAsync(Compatible);
        var release = new TaskCompletionSource();
        _queue.Enqueue("blocker", async ct => await release.Task.WaitAsync(ct));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Enqueue("queued1");
        Enqueue("queued2");

        var watch = Stopwatch.StartNew();
        var stopping = worker.StopAsync(TestContext.Current.CancellationToken);
        await stopping.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3.5), "stop took " + watch.Elapsed);
        Assert.Equal(["queued1", "queued2"], _ran.ToArray());
    }

    [Fact]
    public async Task Ticks_EveryThirtySeconds()
    {
        using var worker = await StartedAsync(Compatible);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(0, _flows.Ticks);

        _h.Time.Advance(TimeSpan.FromSeconds(31));
        await Eventually.TrueAsync(() => _flows.Ticks == 1, "a tick");
        await worker.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Incompatible_NeverRuns()
    {
        using var worker = await StartedAsync(new CompatResult(false, "test", new Version(11, 0), "no"));
        Enqueue("a");

        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Empty(_ran);
        await worker.StopAsync(TestContext.Current.CancellationToken);
        Assert.Empty(_ran);
    }

    public void Dispose() => _h.Dispose();

    private bool Enqueue(string name) => _queue.Enqueue(name, _ =>
    {
        _ran.Enqueue(name);
        return Task.CompletedTask;
    });

    private async Task<BotWorker> StartedAsync(CompatResult compat)
    {
        var gate = await _h.StartOwnedGateAsync();
        var worker = new BotWorker(gate, compat, _queue, _flows, _h.State, _h.Time, _log);
        await worker.StartAsync(TestContext.Current.CancellationToken);
        return worker;
    }
}
