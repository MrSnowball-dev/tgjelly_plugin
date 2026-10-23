using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Delivery;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.Log;
using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.Templates;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Fakes;

/// <summary>
/// The pipeline and Outbox delivery over one <see cref="BotTestHarness"/>, both recording into an
/// <see cref="EventLog"/>, so a test can follow a message from intake to Telegram.
/// </summary>
public sealed class LogRig
{
    private readonly BotTestHarness _h;

    public LogRig(BotTestHarness h, EventLog log, FakeLibrary? library = null)
    {
        ArgumentNullException.ThrowIfNull(h);
        _h = h;
        h.Connect();
        Log = log;
        Library = library ?? new FakeLibrary();
        var gate = new InstanceGate(h.Paths, h.Time, NullLogger<InstanceGate>.Instance);
        h.Track(gate);
        Health = new HealthService(gate, h.Connection, new DestinationChecker(h.Connection, h.Strings), h.Settings, h.State, h.Time, NullLogger<HealthService>.Instance);
        h.Track(Health);
        Recorder = new LogRecorder(log, Library, h.Strings, h.Time, NullLogger<LogRecorder>.Instance, Health);
        Pipeline = new PipelineRig(h, Library, new FakeScanState(), recorder: Recorder);
        Pipeline.Pipeline.Start();
        Delivery = new OutboxDelivery(
            h.Connection,
            Pipeline.Outbox,
            Health,
            h.Settings,
            h.State,
            new PacingGate(h.Time),
            new Formatter(h.Strings),
            h.Time,
            new DestinationMigration(h.Settings, Pipeline.Outbox, Health),
            new TemplateFlags(h.State, h.Settings, h.Time),
            NullLogger<OutboxDelivery>.Instance,
            Recorder)
        {
            Random = () => 0.5,
        };
    }

    public EventLog Log { get; }

    public FakeLibrary Library { get; }

    public LogRecorder Recorder { get; }

    public PipelineRig Pipeline { get; }

    public HealthService Health { get; }

    public OutboxDelivery Delivery { get; }

    /// <summary>Delivers, once each, every queued entry that isn't waiting for a retry time.</summary>
    public async Task DeliverAllAsync()
    {
        var now = _h.Time.GetUtcNow();
        foreach (var entry in Pipeline.Outbox.Snapshot().Where(e => e.NotBefore is null || e.NotBefore <= now))
        {
            var destination = _h.Settings.Current.Destinations.First(d => d.Id == entry.DestinationId);
            await Delivery.DeliverAsync(entry, destination, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Runs the pipeline once a second for <paramref name="seconds"/> seconds.</summary>
    public void RunFor(int seconds)
    {
        for (var i = 0; i < seconds; i++)
        {
            Pipeline.Pipeline.RunOnce();
            _h.Time.Advance(TimeSpan.FromSeconds(1));
        }

        Pipeline.Pipeline.RunOnce();
    }
}
