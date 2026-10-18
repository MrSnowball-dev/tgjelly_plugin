using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Compat;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Delivery;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Delivery;

public sealed class OutboxServiceTests : IDisposable
{
    private readonly BotTestHarness _h = new();
    private readonly Outbox _outbox;
    private readonly PacingGate _pacing;
    private HealthService? _health;

    public OutboxServiceTests()
    {
        _outbox = new Outbox(_h.State, _h.Strings, _h.Time);
        _pacing = new PacingGate(_h.Time);
    }

    private DateTimeOffset Now => _h.Time.GetUtcNow();

    [Fact]
    public async Task QueuedEntries_AreDelivered_InPickOrder()
    {
        _h.Connect();
        var d = _h.AddDestination(-1001);
        _outbox.Enqueue(Entry(d, "media.movie", "movie"));
        _outbox.Enqueue(Entry(d, "server.task_failed", "task"));
        using var service = await StartAsync();

        await Eventually.AdvanceUntilAsync(_h.Time, TimeSpan.FromSeconds(1), () => _h.Api.CallsTo("sendMessage").Count() == 2, "both delivered");

        Assert.Equal(["task", "movie"], _h.Api.CallsTo("sendMessage").Select(c => (string)c.Body["text"]!).ToArray());
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HeldEntries_WaitUntilTheDestinationRecovers()
    {
        _h.Connect();
        var d = _h.AddDestination(-1001);
        using var service = await StartAsync();
        _health!.MarkProblem(d.Id, HealthStatus.Lost, "kicked");
        _outbox.Enqueue(Entry(d, "media.movie", "movie"));
        await Eventually.SettleAsync();
        Assert.Empty(_h.Api.CallsTo("sendMessage"));

        await _health.CheckNowAsync(d.Id, TestContext.Current.CancellationToken);

        await Eventually.TrueAsync(() => _h.Api.CallsTo("sendMessage").Any(), "sent after recovery");
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PausedDestination_IsHeld()
    {
        _h.Connect();
        var d = _h.AddDestination(-1001);
        _h.Settings.Update(null, c => c.Destinations[0].Enabled = false);
        using var service = await StartAsync();
        _outbox.Enqueue(Entry(d, "media.movie", "movie"));
        await Eventually.SettleAsync();
        Assert.Empty(_h.Api.CallsTo("sendMessage"));

        _h.Settings.Update(null, c => c.Destinations[0].Enabled = true);

        await Eventually.TrueAsync(() => _h.Api.CallsTo("sendMessage").Any(), "sent after resuming");
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Incompatible_NeverSends()
    {
        _h.Connect();
        var d = _h.AddDestination(-1001);
        _outbox.Enqueue(Entry(d, "media.movie", "movie"));
        using var service = await StartAsync(compatible: false);

        _h.Time.Advance(TimeSpan.FromMinutes(1));
        await Eventually.SettleAsync();

        Assert.Empty(_h.Api.Calls);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Disconnected_SendsNothing_UntilConnected()
    {
        var d = _h.AddDestination(-1001);
        _outbox.Enqueue(Entry(d, "media.movie", "movie"));
        using var service = await StartAsync();
        await Eventually.SettleAsync();
        Assert.Empty(_h.Api.Calls);

        _h.Connect();

        await Eventually.TrueAsync(() => _h.Api.CallsTo("sendMessage").Any(), "sent once connected");
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task EntriesOfARemovedDestination_AreDropped()
    {
        _h.Connect();
        var d = _h.AddDestination(-1001);
        _outbox.Enqueue(Entry(d, "media.movie", "movie"));
        _h.Settings.Update(null, c => c.Destinations = []);
        using var service = await StartAsync();

        await Eventually.TrueAsync(() => _outbox.Count == 0, "orphaned entry dropped");
        Assert.Empty(_h.Api.Calls);
        await service.StopAsync(CancellationToken.None);
    }

    public void Dispose() => _h.Dispose();

    private async Task<OutboxService> StartAsync(bool compatible = true)
    {
        var gate = await _h.StartOwnedGateAsync();
        _health = new HealthService(gate, _h.Connection, new DestinationChecker(_h.Connection, _h.Strings), _h.Settings, _h.State, _h.Time, NullLogger<HealthService>.Instance);
        _h.Track(_health);
        var delivery = new OutboxDelivery(_h.Connection, _outbox, _health, _h.Settings, _h.State, _pacing, _h.Time, NullLogger<OutboxDelivery>.Instance);
        var compat = compatible ? CompatGuard.Evaluate(new Version(BuildInfo.TargetJellyfinMajor, 0, 0, 0)) : CompatGuard.Evaluate(new Version(BuildInfo.TargetJellyfinMajor + 1, 0, 0, 0));
        var service = new OutboxService(gate, compat, _h.Connection, _outbox, delivery, _health, _h.Settings, _pacing, _h.Time, NullLogger<OutboxService>.Instance);
        await service.StartAsync(CancellationToken.None);
        return service;
    }

    private OutboxEntry Entry(Destination d, string eventKey, string text) => new()
    {
        EntryId = Guid.NewGuid().ToString("N"),
        DestinationId = d.Id,
        EventKey = eventKey,
        QueuedAt = Now,
        Message = new OutboxMessage { Text = text, Caption = text },
    };

    [Fact]
    public async Task Sends_DuringAWebhookConflict()
    {
        _h.Connect();
        _h.Connection.SetState(Jellyfin.Plugin.TgJelly.Bot.ConnectionState.WebhookConflict);
        var d = _h.AddDestination(-1001);
        _outbox.Enqueue(Entry(d, "server.task_failed", "task"));
        using var service = await StartAsync();

        await Eventually.TrueAsync(() => _h.Api.CallsTo("sendMessage").Any(), "sending works while another app owns the webhook");
        await service.StopAsync(CancellationToken.None);
    }
}
