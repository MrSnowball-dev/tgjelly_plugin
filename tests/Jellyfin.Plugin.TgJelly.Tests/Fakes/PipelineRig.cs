using System;
using Jellyfin.Plugin.TgJelly.Compat;
using Jellyfin.Plugin.TgJelly.Delivery;
using Jellyfin.Plugin.TgJelly.Intake;
using Jellyfin.Plugin.TgJelly.Library;
using Jellyfin.Plugin.TgJelly.Pipeline;
using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.TgJelly.Tests.Fakes;

/// <summary>
/// The notification pipeline built from real units over a <see cref="BotTestHarness"/>, a fake library and a fake
/// scan state. A second rig over a reloaded <see cref="StateStore"/> simulates a restart.
/// </summary>
public sealed class PipelineRig
{
    public static readonly CompatResult Compatible = new(true, "test", new Version(12, 0), null);

    public PipelineRig(BotTestHarness h, FakeLibrary library, FakeScanState scan, StateStore? state = null)
    {
        ArgumentNullException.ThrowIfNull(h);
        State = state ?? h.State;
        Queues = new IntakeQueues(Compatible, h.Time);
        Readiness = new MediaReadiness(State, h.Settings, library, new PendingIdSet(), h.Time, NullLogger<MediaReadiness>.Instance);
        Outbox = new Outbox(State, h.Strings, h.Time);
        Aggregator = new MediaAggregator(State, h.Settings, Outbox, scan, h.Time);
        Alerts = new AlertCoalescer(State, h.Strings, h.Time);
        var formatter = new Formatter(h.Strings);
        var engine = new Jellyfin.Plugin.TgJelly.Templates.TemplateEngine(TimeProvider.System);
        var models = new Jellyfin.Plugin.TgJelly.Templates.TemplateModelBuilder(library, new LinkBuilder(new FakeServerInfo()), h.Settings, formatter, h.Time);
        var flags = new Jellyfin.Plugin.TgJelly.Templates.TemplateFlags(State, h.Settings, h.Time);
        Renderer = new NotificationRenderer(
            new MediaLayouts(formatter, new LinkBuilder(new FakeServerInfo()), library, h.Settings),
            new AlertLayouts(formatter, h.Time),
            new Jellyfin.Plugin.TgJelly.Templates.TemplateRenderer(engine, models, flags, h.Settings, h.Strings, NullLogger<Jellyfin.Plugin.TgJelly.Templates.TemplateRenderer>.Instance),
            engine,
            models,
            flags,
            h.Settings,
            h.Strings);
        Logger = new ListLogger<NotificationPipeline>();
        Pipeline = new NotificationPipeline(Queues, Readiness, Aggregator, Alerts, Renderer, Outbox, h.Settings, State, h.Time, Logger);
    }

    public StateStore State { get; }

    public IntakeQueues Queues { get; }

    public MediaReadiness Readiness { get; }

    public Outbox Outbox { get; }

    public MediaAggregator Aggregator { get; }

    public AlertCoalescer Alerts { get; }

    public NotificationRenderer Renderer { get; }

    public ListLogger<NotificationPipeline> Logger { get; }

    public NotificationPipeline Pipeline { get; }

    public static ItemFacts Facts(MediaItem item, bool refreshed)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new ItemFacts(item.Id, item.Kind, item.Id, false, false, false, false, false, refreshed ? new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc) : DateTime.MinValue, item.Identities);
    }

    public void Added(MediaItem item) => Queues.PublishMedia(MediaIntakeKind.Added, Facts(item, refreshed: false));

    public void Updated(MediaItem item) => Queues.PublishMedia(MediaIntakeKind.Updated, Facts(item, refreshed: true));
}
