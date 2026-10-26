using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Intake;
using Jellyfin.Plugin.TgJelly.Library;
using Jellyfin.Plugin.TgJelly.Pipeline;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Pipeline;

public sealed class PipelineServiceTests : IDisposable
{
    private readonly BotTestHarness _h = new();
    private readonly PipelineRig _rig;

    public PipelineServiceTests() => _rig = new PipelineRig(_h, new FakeLibrary(), new FakeScanState());

    [Fact]
    public async Task Stop_DrainsIntake_AndSendsHeldLockouts_Within5s()
    {
        _h.AddDestination(-1001);
        using var service = await StartedAsync();

        for (var i = 0; i < 1000; i++)
        {
            _rig.Added(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Movie });
        }

        _rig.Queues.Publish("security.lockout", new Dictionary<string, string> { [EventFields.User] = "bob", [EventFields.UserKey] = "BOB", [EventFields.Attempts] = "3" });
        var watch = Stopwatch.StartNew();
        await service.StopAsync(CancellationToken.None);
        watch.Stop();

        Assert.Equal((0, 0), (_rig.Queues.MediaCount, _rig.Queues.OtherCount));
        Assert.Equal(1000, _rig.Readiness.PendingCount);
        Assert.Equal("security.lockout", Assert.Single(_rig.Outbox.Snapshot()).EventKey);
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, PipelineService.DrainBudget + TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Stop_DoesNotThrow_WhenPipelineThrows()
    {
        using var service = await StartedAsync();
        _rig.Queues.Publish("server.started", new Dictionary<string, string>());
        _h.Host.Fault = new InvalidOperationException("settings broke");

        await service.StopAsync(CancellationToken.None);

        _h.Host.Fault = null;
        Assert.Empty(_rig.Outbox.Snapshot());
    }


    public void Dispose() => _h.Dispose();

    private async Task<PipelineService> StartedAsync()
    {
        var gate = await _h.StartOwnedGateAsync();
        var service = new PipelineService(gate, _h.State, _rig.Queues, _rig.Pipeline, _h.Time, NullLogger<PipelineService>.Instance);
        await service.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!service.IsStarted && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(service.IsStarted);
        return service;
    }
}
