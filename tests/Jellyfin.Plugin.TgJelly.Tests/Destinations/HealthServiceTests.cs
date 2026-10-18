using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Destinations;

public sealed class HealthServiceTests : IDisposable
{
    private readonly BotTestHarness _h = new();

    [Fact]
    public void MarkProblem_NeverTouchesEnabled()
    {
        var d = _h.AddDestination(-1001);
        using var health = NewService();

        health.MarkProblem(d.Id, HealthStatus.Lost, "kicked");

        Assert.True(_h.Settings.Current.Destinations.Single().Enabled);
        Assert.False(health.IsDeliverable(d.Id));
        Assert.Equal(("kicked", _h.Time.GetUtcNow() + HealthService.RecheckInterval), (health.Get(d.Id).LastError, health.Get(d.Id).NextCheckAt!.Value));
    }

    [Fact]
    public async Task CheckNow_Recovers_AndRaisesRecovered()
    {
        _h.Connect();
        var d = _h.AddDestination(-1001);
        using var health = NewService();
        var recovered = 0;
        health.Recovered += (_, _) => recovered++;
        health.MarkProblem(d.Id, HealthStatus.Lost, "kicked");

        var result = await health.CheckNowAsync(d.Id, TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Ok, result.Status);
        Assert.Null(result.LastError);
        Assert.Null(result.NextCheckAt);
        Assert.Equal(1, recovered);
    }

    [Fact]
    public async Task Unhealthy_IsRecheckedEvery30Minutes_ByTheLoop()
    {
        _h.Connect();
        var d = _h.AddDestination(-1001);
        var gate = await _h.StartOwnedGateAsync();
        using var health = NewService(gate);
        health.MarkProblem(d.Id, HealthStatus.Lost, "kicked");
        await health.StartAsync(CancellationToken.None);
        await Eventually.SettleAsync();

        _h.Time.Advance(TimeSpan.FromMinutes(29));
        await Eventually.SettleAsync();
        Assert.Empty(_h.Api.CallsTo("getChat"));

        await Eventually.AdvanceUntilAsync(_h.Time, TimeSpan.FromMinutes(1), () => health.Get(d.Id).Status == HealthStatus.Ok, "re-checked after 30 min");
        Assert.Single(_h.Api.CallsTo("getChat"));
        await health.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RequestCheckForChat_ChecksOnlyThatChatsDestinations()
    {
        _h.Connect();
        _h.AddDestination(-1001);
        var second = _h.AddDestination(-1002);
        var gate = await _h.StartOwnedGateAsync();
        using var health = NewService(gate);
        await health.StartAsync(CancellationToken.None);

        health.RequestCheckForChat(-1002);

        await Eventually.TrueAsync(() => _h.Api.CallsTo("getChat").Any(), "a check ran");
        await Eventually.SettleAsync();
        Assert.Equal([-1002L], _h.Api.CallsTo("getChat").Select(c => (long)c.Body["chat_id"]!).ToArray());
        Assert.Equal(HealthStatus.Ok, health.Get(second.Id).Status);
        await health.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RequestCheckAll_ChecksEveryDestination()
    {
        _h.Connect();
        _h.AddDestination(-1001);
        _h.AddDestination(-1002);
        var gate = await _h.StartOwnedGateAsync();
        using var health = NewService(gate);
        await health.StartAsync(CancellationToken.None);

        health.RequestCheckAll();

        await Eventually.TrueAsync(() => _h.Api.CallsTo("getChat").Count() == 2, "both destinations checked");
        await health.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void ANewProblem_ResetsNotified()
    {
        var d = _h.AddDestination(-1001);
        using var health = NewService();

        health.MarkProblem(d.Id, HealthStatus.Lost, "kicked");
        _h.State.Update(s => s.Health[d.Id].Notified = true);
        health.MarkProblem(d.Id, HealthStatus.NeedsPermissions, "rights");
        Assert.False(health.Get(d.Id).Notified);

        _h.State.Update(s => s.Health[d.Id].Notified = true);
        health.MarkProblem(d.Id, HealthStatus.NeedsPermissions, "rights again");
        Assert.True(health.Get(d.Id).Notified);
    }

    [Fact]
    public async Task Loop_DoesNothingWhileDisconnected()
    {
        _h.AddDestination(-1001);
        var gate = await _h.StartOwnedGateAsync();
        using var health = NewService(gate);
        await health.StartAsync(CancellationToken.None);

        health.RequestCheckAll();
        _h.Time.Advance(TimeSpan.FromMinutes(1));
        await Eventually.SettleAsync();

        Assert.Empty(_h.Api.Calls);
        await health.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ChecksAskedForWhileDisconnected_RunOnceConnected()
    {
        _h.AddDestination(-1001);
        var gate = await _h.StartOwnedGateAsync();
        using var health = NewService(gate);
        await health.StartAsync(CancellationToken.None);
        health.RequestCheckAll();
        await Eventually.SettleAsync();

        _h.Connect();

        await Eventually.TrueAsync(() => _h.Api.CallsTo("getChat").Any(), "the pending check ran after connecting");
        await health.StopAsync(CancellationToken.None);
    }

    public void Dispose() => _h.Dispose();

    private HealthService NewService(Jellyfin.Plugin.TgJelly.State.InstanceGate? gate = null)
        => new(
            gate ?? new Jellyfin.Plugin.TgJelly.State.InstanceGate(_h.Paths, _h.Time, NullLogger<Jellyfin.Plugin.TgJelly.State.InstanceGate>.Instance),
            _h.Connection,
            new DestinationChecker(_h.Connection, _h.Strings),
            _h.Settings,
            _h.State,
            _h.Time,
            NullLogger<HealthService>.Instance);
}
