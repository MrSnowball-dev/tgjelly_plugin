using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Delivery;
using Jellyfin.Plugin.TgJelly.Intake;
using Jellyfin.Plugin.TgJelly.Log;
using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.Pipeline;

/// <summary>
/// Moves events from intake to the Outbox: media through readiness, routing and the aggregator; security
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
    private readonly LogRecorder? _log;

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
    /// <param name="log">Where each message's outcome is recorded for the Log, if anywhere.</param>
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
        ILogger<NotificationPipeline> logger,
        LogRecorder? log = null)
    {
        _log = log;
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
        _log?.Sweep();
        var dropped = _queues.TakeDropped();
        if (dropped > 0)
        {
            _state.Update(s => s.Status.Counters.DroppedIntake += dropped);
        }

        return media.Count + other.Count;
    }

    /// <summary>
    /// Empties the intake queues, then sends the held lockouts (graceful stop).
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

                var (key, eventKey, _) = GroupKeys.For(item.Item);
                var routed = Router.Route(eventKey, item.Libraries.Select(l => l.Id).ToList(), destinations);
                EnqueueMedia(_media.Add(item, routed), destinations);

                // Only after the message is on its way: recording must never keep it from the Outbox.
                Record(log =>
                {
                    log.MediaRouted(item, destinations);
                    foreach (var destination in routed)
                    {
                        if (_media.FindHeld(destination.Id, key) is { } held)
                        {
                            log.Held(destination, key, eventKey, held.FirstAt, held.ItemIds);
                        }
                    }
                });
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
                    // Removed while its groups were held: their rows say so, and no row is made for it.
                    foreach (var gone in output.Groups)
                    {
                        _log?.DestinationRemoved(LogRecorder.MediaId(gone.Key, output.DestinationId, gone.FirstAt));
                    }

                    return;
                }

                // Groups wait up to hours in a release buffer; a destination disabled or unsubscribed meanwhile gets
                // nothing from them.
                var wanted = new List<MediaGroupState>();
                foreach (var group in output.Groups)
                {
                    if (Router.Explain(group.Model.EventKey, null, destination) is { } reason)
                    {
                        _log?.NotWanted(LogRecorder.MediaId(group.Key, destination.Id, group.FirstAt), destination, group.Model, reason);
                    }
                    else
                    {
                        wanted.Add(group);
                    }
                }

                if (wanted.Count == 0)
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
                        Groups = wanted.Select(g => g.Model).ToList(),
                    };
                    var groupRows = wanted.Select(g => (Id: LogRecorder.MediaId(g.Key, destination.Id, g.FirstAt), g.Model)).ToList();
                    if (Enqueue(destination, digest, OutboxOp.Send, "digest", sentKey: null, targetKey: null, replyToKey: null, logId: null) is not null)
                    {
                        _log?.Merged([.. groupRows.Select(r => r.Id)]);
                    }
                    else
                    {
                        foreach (var (id, model) in groupRows)
                        {
                            _log?.NothingToSend(id, destination, model);
                        }
                    }

                    return;
                }

                var first = output.Groups[0];
                var edit = output.Kind == MediaOutputKind.LateEdit;

                // A late arrival updates the row of the message it edits; a released group moves its Waiting row on.
                var logId = edit ? LogIdOf(destination.Id, first.Key) : LogRecorder.MediaId(first.Key, destination.Id, first.FirstAt);
                Enqueue(destination, first.Model, edit ? OutboxOp.Edit : OutboxOp.Send, first.Key, first.Key, edit ? first.Key : null, replyToKey: null, logId);
            });
        }
    }

    private void EnqueueAlerts(IReadOnlyList<AlertOutput> outputs, IReadOnlyList<Destination> destinations)
    {
        foreach (var output in outputs)
        {
            _log?.AlertRouted(output, destinations);
            foreach (var destination in Router.Route(output.Model.EventKey, null, destinations))
            {
                // Each entry owns its model: the Outbox keeps entries by reference and merges edits into them. An edit
                // of an earlier alert updates that alert's row.
                Guard(() =>
                {
                    var logId = output.Op == OutboxOp.Edit && output.TargetKey is { } target ? LogIdOf(destination.Id, target) : null;
                    Enqueue(destination, Copy(output.Model), output.Op, output.Model.EventKey, output.SentKey, output.TargetKey, output.ReplyToKey, logId);
                });
            }
        }
    }

    // Returns the Log row that carries the message, or null when there was nothing to send.
    private string? Enqueue(Destination destination, RenderModel model, OutboxOp op, string groupKey, string? sentKey, string? targetKey, string? replyToKey, string? logId)
    {
        var entryId = Guid.NewGuid().ToString("N");
        logId ??= entryId;
        if (_renderer.Render(model, destination) is not { } message)
        {
            // A digest's caller settles the rows of the groups it held.
            if (model.Kind != RenderKind.Digest)
            {
                _log?.NothingToSend(logId, destination, model);
            }

            return null;
        }

        var (carrier, summary) = _outbox.Enqueue(new OutboxEntry
        {
            EntryId = entryId,
            LogId = logId,
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
        _log?.Queued(carrier, destination, model, summary);
        return carrier;
    }

    // Log-only reads and writes: a failure is logged and the message goes on as if there were no Log.
    private void Record(Action<LogRecorder> record)
    {
        if (_log is null)
        {
            return;
        }

        try
        {
            record(_log);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "TgJelly could not record a Log row; sending is not affected");
        }
    }

    // The Log row of the message an edit changes; null (a row of its own) when it can't be found.
    private string? LogIdOf(string destinationId, string key)
    {
        if (_log is null)
        {
            return null;
        }

        try
        {
            return _outbox.LogIdOf(destinationId, key);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "TgJelly could not find the Log row of an edited message; sending is not affected");
            return null;
        }
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
