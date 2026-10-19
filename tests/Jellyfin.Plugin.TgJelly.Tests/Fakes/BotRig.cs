using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Bot.Screens;
using Jellyfin.Plugin.TgJelly.Delivery;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.Pipeline;
using Jellyfin.Plugin.TgJelly.Intake;
using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.State;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot.Types;

namespace Jellyfin.Plugin.TgJelly.Tests.Fakes;

/// <summary>
/// A connected <see cref="BotTestHarness"/> with a real <see cref="FlowEngine"/> behind a real <see cref="UpdateRouter"/>
/// and work queue. <see cref="Deliver"/> runs an update the way the poll loop and worker would.
/// </summary>
public sealed class BotRig
{
    private readonly UpdateRouter _router;

    private BotRig(BotTestHarness h, InstanceGate gate)
    {
        ArgumentNullException.ThrowIfNull(h);
        Harness = h;
        h.Connect();
        Health = new HealthService(gate, h.Connection, new DestinationChecker(h.Connection, h.Strings), h.Settings, h.State, h.Time, NullLogger<HealthService>.Instance);
        h.Track(Health);
        Outbox = new Outbox(h.State, h.Strings, h.Time);
        Readiness = new MediaReadiness(h.State, h.Settings, new FakeLibrary(), new PendingIdSet(), h.Time, NullLogger<MediaReadiness>.Instance);
        Memory = new BotMemory(h.State, h.Time);
        var formatter = new Formatter(h.Strings);
        var menus = new MenuScreens(h.Strings, formatter);
        Status = new StatusScreen(h.Connection, PipelineRig.Compatible, gate, h.Settings, h.State, Health, Readiness, Outbox, h.Strings, formatter, h.Time);
        Library = new FakeLibrary();
        var tester = new DestinationTester(h.Connection, Health, h.Strings, NullLogger<DestinationTester>.Instance);
        var destinations = new DestinationScreens(h.Settings, Health, tester, Library, Outbox, h.Connection, h.Strings, formatter, NullLogger<DestinationScreens>.Instance);
        Server = new FakeServerInfo();
        var settingsScreens = new SettingsScreens(h.Settings, new LinkBuilder(Server), Memory, h.Connection, h.Strings, formatter);
        Flow = new FlowEngine(h.Connection, Memory, new ScreenSender(h.Connection), h.Settings, h.Strings, menus, Status, destinations, settingsScreens, NullLogger<FlowEngine>.Instance);
        Queue = new BotWorkQueue(NullLogger<BotWorkQueue>.Instance);
        _router = new UpdateRouter(Queue, Flow, h.Connection, Health, new DestinationMigration(h.Settings, Outbox, Health), NullLogger<UpdateRouter>.Instance);
    }

    /// <summary>Connects the harness and builds the rig over an owned instance gate.</summary>
    public static async Task<BotRig> CreateAsync(BotTestHarness h)
    {
        ArgumentNullException.ThrowIfNull(h);
        return new BotRig(h, await h.StartOwnedGateAsync().ConfigureAwait(false));
    }

    public BotTestHarness Harness { get; }

    public FakeLibrary Library { get; }

    public FakeServerInfo Server { get; }

    public StatusScreen Status { get; }

    public FlowEngine Flow { get; }

    public BotMemory Memory { get; }

    public HealthService Health { get; }

    public Outbox Outbox { get; }

    public MediaReadiness Readiness { get; }

    public BotWorkQueue Queue { get; }

    /// <summary>Routes an update and runs the work it queued.</summary>
    public async Task Deliver(Update update)
    {
        await _router.HandleAsync(update, CancellationToken.None).ConfigureAwait(false);
        await Queue.RunPendingAsync(CancellationToken.None, (name, ex) => throw new InvalidOperationException("Work item " + name + " failed", ex)).ConfigureAwait(false);
    }
}
