using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Hosting;

public class SupervisorTests
{
    [Fact]
    public async Task FailingBody_IsRestarted_WithDoublingBackoff()
    {
        var time = new FakeTimeProvider();
        var attempts = 0;
        using var cts = new CancellationTokenSource();

        var run = Supervisor.RunAsync(
            "test",
            _ =>
            {
                attempts++;
                return attempts < 4 ? throw new InvalidOperationException("boom") : Task.CompletedTask;
            },
            time,
            NullLogger.Instance,
            cts.Token);

        // Each retry is checked by settling first (so the backoff timer exists), then advancing exactly its delay.
        await Eventually.TrueAsync(() => attempts == 1, "first attempt");
        await Eventually.SettleAsync();
        time.Advance(TimeSpan.FromMilliseconds(999));
        await Eventually.SettleAsync();
        Assert.Equal(1, attempts);

        time.Advance(TimeSpan.FromMilliseconds(1));
        await Eventually.TrueAsync(() => attempts == 2, "retry after 1 s");
        await Eventually.SettleAsync();
        time.Advance(TimeSpan.FromSeconds(2));
        await Eventually.TrueAsync(() => attempts == 3, "retry after 2 s");
        await Eventually.SettleAsync();
        time.Advance(TimeSpan.FromSeconds(4));
        await Eventually.TrueAsync(() => attempts == 4, "retry after 4 s");

        await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RunAsync_ReturnsToTheCaller_BeforeRunningTheBody()
    {
        // A blocking body must not block the caller (BackgroundService.StartAsync on .NET 9 calls ExecuteAsync inline).
        using var release = new ManualResetEventSlim();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var run = Supervisor.RunAsync(
            "test",
            _ =>
            {
                release.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
                return Task.CompletedTask;
            },
            TimeProvider.System,
            NullLogger.Instance,
            CancellationToken.None);
        var returnedAfter = watch.Elapsed;
        release.Set();

        Assert.True(returnedAfter < TimeSpan.FromSeconds(1), $"RunAsync blocked the caller for {returnedAfter}");
        await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Cancellation_EndsQuietly_EvenDuringBackoff()
    {
        var time = new FakeTimeProvider();
        using var cts = new CancellationTokenSource();

        var run = Supervisor.RunAsync("test", _ => throw new InvalidOperationException("boom"), time, NullLogger.Instance, cts.Token);
        await Eventually.SettleAsync();
        await cts.CancelAsync();

        await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(run.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task OperationCanceledFromBody_WhenStopping_IsNotAnError()
    {
        using var cts = new CancellationTokenSource();
        var run = Supervisor.RunAsync(
            "test",
            async token => await Task.Delay(Timeout.Infinite, token),
            TimeProvider.System,
            NullLogger.Instance,
            cts.Token);

        await cts.CancelAsync();

        await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(run.IsCompletedSuccessfully);
    }
}
