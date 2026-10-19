using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Delivery;
using Jellyfin.Plugin.TgJelly.Intake;
using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.Pipeline;

/// <summary>
/// Moves events from intake to the Outbox (spec §7): media through readiness, routing and the aggregator; security
/// and server events through the coalescer and routing; then rendering for each destination. One failing output is
/// logged and skipped; the rest still go.
/// </summary>
public sealed class NotificationPipeline
{
    /// <summary>The most events taken from each intake queue per run.</summary>
    public const int BatchSize = 500;

    private readonly IntakeQueues _queues;
    private readonly MediaReadiness _readiness;
    private readonly MediaAggregator _media;
    private readonly AlertCoalescer _alerts;
    private readonly NotificationRenderer _renderer;
    private readonly Outbox _outbox;
    private readonly SettingsStore _settings;
    private readonly StateStore _state;
    private readonly TimeProvider _time;
    private readonly ILogger<NotificationPipeline> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NotificationPipeline"/> class.
    /// </summary>
    /// <param name="queues">The intake queues.</param>
    /// <param name="readiness">Media readiness.</param>
    /// <param name="media">The media aggregator.</param>
    /// <param name="alerts">The alert coalescer.</param>
    /// <param name="renderer">The renderer.</param>
    /// <param name="outbox">The Outbox.</param>
    /// <param name="settings">The settings (destinations).</param>
    /// <param name="state">The state store (counters and errors).</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public NotificationPipeline(
        IntakeQueues queues,
        MediaReadiness readiness,
        MediaAggregator media,
        AlertCoalescer alerts,
        NotificationRenderer renderer,
        Outbox outbox,
        SettingsStore settings,
        StateStore state,
        TimeProvider time,
        ILogger<NotificationPipeline> logger)
    {
        _queues = queues;
        _readiness = readiness;
        _media = media;
        _alerts = alerts;
        _renderer = renderer;
        _outbox = outbox;
        _settings = settings;
        _state = state;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Restores what survives a restart (the pending items).
    /// </summary>
    public void Start() => _readiness.Load();

    /// <summary>
    /// Processes up to <see cref="BatchSize"/> events from each queue, then runs every timer.
    /// </summary>
    /// <returns>How many intake events were processed.</returns>
    public int RunOnce()
    {
        var destinations = _settings.Current.Destinations;
        var media = _queues.TakeMedia(BatchSize);
        var other = _queues.TakeOther(BatchSize);
        foreach (var intake in media)
        {
            Guard(() =>
            {
                if (intake.Kind == MediaIntakeKind.ScanFinished)
                {
                    EnqueueMedia(_media.ScanFinished(), destinations);
                }
                else
                {
                    AddReady(_readiness.Process(intake), destinations);
                }
            });
        }

        foreach (var e in other)
        {
            Guard(() => EnqueueAlerts(_alerts.Process(e), destinations));
        }

        Guard(() => AddReady(_readiness.Tick(), destinations));
        Guard(() => EnqueueMedia(_media.Tick(), destinations));
        Guard(() => EnqueueAlerts(_alerts.Tick(), destinations));
        var dropped = _queues.TakeDropped();
        if (dropped > 0)
        {
            _state.Update(s => s.Status.Counters.DroppedIntake += dropped);
        }

        return media.Count + other.Count;
    }

    /// <summary>
    /// Empties the intake queues, then sends the held lockouts (graceful stop, spec §12).
    /// </summary>
    /// <param name="budget">Stops taking more batches once this has passed; held lockouts are still sent.</param>
    public void Drain(TimeSpan? budget = null)
    {
        var started = _time.GetTimestamp();
        while ((_queues.MediaCount > 0 || _queues.OtherCount > 0)
            && (budget is not { } limit || _time.GetElapsedTime(started) < limit))
        {
            RunOnce();
        }

        EnqueueAlerts(_alerts.Drain(), _settings.Current.Destinations);
    }

    private static T Copy<T>(T value)
        => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonDefaults.Options), JsonDefaults.Options)!;

    private void AddReady(IReadOnlyList<MediaReady> ready, IReadOnlyList<Destination> destinations)
    {
        foreach (var item in ready)
        {
            Guard(() =>
            {
                if (!GroupKeys.IsGrouped(item.Item.Kind))
                {
                    return;
                }

                var routed = Router.Route(GroupKeys.For(item.Item).EventKey, item.Libraries.Select(l => l.Id).ToList(), destinations);
                EnqueueMedia(_media.Add(item, routed), destinations);
            });
        }
    }

    private void EnqueueMedia(IReadOnlyList<MediaOutput> outputs, IReadOnlyList<Destination> destinations)
    {
        foreach (var output in outputs)
        {
            Guard(() =>
            {
                if (destinations.FirstOrDefault(d => d.Id == output.DestinationId) is not { } destination)
                {
                    return;
                }

                if (output.Kind == MediaOutputKind.Digest)
                {
                    var digest = new RenderModel
                    {
                        Kind = RenderKind.Digest,
                        EventKey = "media.digest",
                        At = _time.GetUtcNow(),
                        Groups = output.Groups.Select(g => g.Model).ToList(),
                    };
                    Enqueue(destination, digest, OutboxOp.Send, "digest", sentKey: null, targetKey: null, replyToKey: null);
                    return;
                }

                var group = output.Groups[0];
                var edit = output.Kind == MediaOutputKind.LateEdit;
                Enqueue(destination, group.Model, edit ? OutboxOp.Edit : OutboxOp.Send, group.Key, group.Key, edit ? group.Key : null, replyToKey: null);
            });
        }
    }

    private void EnqueueAlerts(IReadOnlyList<AlertOutput> outputs, IReadOnlyList<Destination> destinations)
    {
        foreach (var output in outputs)
        {
            foreach (var destination in Router.Route(output.Model.EventKey, null, destinations))
            {
                // Each entry owns its model: the Outbox keeps entries by reference and merges edits into them.
                Guard(() => Enqueue(destination, Copy(output.Model), output.Op, output.Model.EventKey, output.SentKey, output.TargetKey, output.ReplyToKey));
            }
        }
    }

    private void Enqueue(Destination destination, RenderModel model, OutboxOp op, string groupKey, string? sentKey, string? targetKey, string? replyToKey)
    {
        if (_renderer.Render(model, destination) is not { } message)
        {
            return;
        }

        _outbox.Enqueue(new OutboxEntry
        {
            EntryId = Guid.NewGuid().ToString("N"),
            DestinationId = destination.Id,
            EventKey = model.EventKey,
            GroupKey = groupKey,
            Op = op,
            SentKey = sentKey,
            TargetKey = targetKey,
            ReplyToKey = replyToKey,
            Message = message,
            Model = model,
            QueuedAt = _time.GetUtcNow(),
        });
    }

    private void Guard(Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A notification could not be processed and was skipped");
            _state.RecordError("pipeline", ex.Message);
        }
    }
}
