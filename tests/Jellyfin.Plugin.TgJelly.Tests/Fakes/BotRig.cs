using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Bot.Flows;
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
        var checker = new DestinationChecker(h.Connection, h.Strings);
        Retrier = new ChatCheckRetrier(checker, h.Time)
        {
            Delay = (delay, _) =>
            {
                Delays.Add(delay);
                return Task.CompletedTask;
            },
        };
        var creator = new DestinationCreator(h.Settings, Health, tester, NullLogger<DestinationCreator>.Instance);
        var screens = new ScreenSender(h.Connection);
        var topics = new TopicFlow(h.Connection, Memory, screens, Retrier, checker, h.Strings, formatter, h.Time, NullLogger<TopicFlow>.Instance);
        Onboarding = new OnboardingFlow(h.Connection, Memory, screens, h.Settings, Retrier, topics, checker, creator, Health, destinations, menus, h.Strings, formatter, h.Time, NullLogger<OnboardingFlow>.Instance);
        Flow = new FlowEngine(h.Connection, Memory, screens, h.Settings, h.Strings, menus, Status, destinations, settingsScreens, Onboarding, NullLogger<FlowEngine>.Instance);
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

    public OnboardingFlow Onboarding { get; }

    public ChatCheckRetrier Retrier { get; }

    /// <summary>The waits the retrier asked for; it returns at once instead of waiting.</summary>
    public List<TimeSpan> Delays { get; } = [];

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
