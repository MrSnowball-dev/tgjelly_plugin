using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Delivery;
using Jellyfin.Plugin.TgJelly.Intake;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.State.Documents;

namespace Jellyfin.Plugin.TgJelly.Pipeline;

/// <summary>
/// Batches ready media per destination (spec §7.4 steps 4–7): open groups close after a quiet window or a maximum
/// hold, closed groups wait in a release buffer while a scan runs, a big release becomes one digest, and a late item
/// of a group already sent becomes an edit. All state lives in queue.json, so it survives a restart.
/// </summary>
public sealed class MediaAggregator
{
    /// <summary>The most open groups a destination keeps; the oldest is closed early beyond it.</summary>
    public const int OpenGroupsCap = 5_000;

    /// <summary>The most groups a release buffer holds before it is released, even during a scan.</summary>
    public const int BufferCap = 2_000;

    /// <summary>How long a running scan holds a release buffer at most.</summary>
    public static readonly TimeSpan ScanHoldLimit = TimeSpan.FromMinutes(120);

    private readonly StateStore _state;
    private readonly SettingsStore _settings;
    private readonly Outbox _outbox;
    private readonly IScanState _scan;
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaAggregator"/> class.
    /// </summary>
    /// <param name="state">The state store (open groups and release buffers).</param>
    /// <param name="settings">The settings (batching).</param>
    /// <param name="outbox">The Outbox, for the models of groups already sent or queued.</param>
    /// <param name="scan">Whether a library scan is running.</param>
    /// <param name="time">The clock.</param>
    public MediaAggregator(StateStore state, SettingsStore settings, Outbox outbox, IScanState scan, TimeProvider time)
    {
        _state = state;
        _settings = settings;
        _outbox = outbox;
        _scan = scan;
        _time = time;
    }

    /// <summary>Gets or sets the open-groups cap (tests lower it).</summary>
    internal int OpenGroupsCapOverride { get; set; } = OpenGroupsCap;

    /// <summary>Gets or sets the buffer cap (tests lower it).</summary>
    internal int BufferCapOverride { get; set; } = BufferCap;

    /// <summary>
    /// Adds a ready item to its group in each destination it was routed to.
    /// </summary>
    /// <param name="ready">The item.</param>
    /// <param name="destinations">The destinations it was routed to.</param>
    /// <returns>Late-arrival edits; new and growing groups give nothing until they are released.</returns>
    public IReadOnlyList<MediaOutput> Add(MediaReady ready, IReadOnlyList<Destination> destinations)
    {
        ArgumentNullException.ThrowIfNull(ready);
        ArgumentNullException.ThrowIfNull(destinations);
        if (destinations.Count == 0 || !GroupKeys.IsGrouped(ready.Item.Kind))
        {
            return [];
        }

        var (key, eventKey, containerId) = GroupKeys.For(ready.Item);
        var itemId = ready.Item.Id.ToString("N");
        var now = _time.GetUtcNow();
        var lateEdit = TimeSpan.FromHours(_settings.Current.Batching.LateEditHours);
        var outputs = new List<MediaOutput>();
        _state.UpdateQueue(q =>
        {
            foreach (var destination in destinations)
            {
                var id = destination.Id;
                if (q.OpenGroups.Find(g => g.Key == key && g.DestinationId == id) is { } open)
                {
                    Merge(open.Model, itemId, ready);
                    open.LastAt = now;
                    continue;
                }

                if (q.ReleaseBuffers.TryGetValue(id, out var buffer) && buffer.Groups.Find(g => g.Key == key) is { } buffered)
                {
                    Merge(buffered.Model, itemId, ready);
                    continue;
                }

                if (lateEdit > TimeSpan.Zero && _outbox.FindModel(id, key) is { } sent && (sent.Queued || now - sent.SentAt < lateEdit))
                {
                    Merge(sent.Model, itemId, ready);
                    var edited = new MediaGroupState { DestinationId = id, Key = key, FirstAt = sent.SentAt, LastAt = now, Model = sent.Model };
                    outputs.Add(new MediaOutput(id, MediaOutputKind.LateEdit, [edited]));
                    continue;
                }

                if (q.OpenGroups.Count(g => g.DestinationId == id) >= OpenGroupsCapOverride)
                {
                    var oldest = q.OpenGroups.Where(g => g.DestinationId == id).MinBy(g => g.FirstAt)!;
                    q.OpenGroups.Remove(oldest);
                    Close(q, oldest, now);
                }

                q.OpenGroups.Add(new MediaGroupState
                {
                    DestinationId = id,
                    Key = key,
                    FirstAt = now,
                    LastAt = now,
                    Model = new RenderModel
                    {
                        Kind = RenderKind.Media,
                        EventKey = eventKey,
                        At = now,
                        ItemIds = [itemId],
                        ContainerId = containerId,
                        LibraryName = ready.Libraries.Count > 0 ? ready.Libraries[0].Name : string.Empty,
                        NewSeries = ready.NewSeries,
                        Upgraded = ready.Upgraded,
                    },
                });
            }
        });
        return outputs;
    }

    /// <summary>
    /// Closes quiet or long-held groups and releases the buffers that are due.
    /// </summary>
    /// <returns>The released groups and digests.</returns>
    public IReadOnlyList<MediaOutput> Tick() => Run(force: false);

    /// <summary>
    /// Closes every open group and releases every buffer, ignoring the timers (a scan just finished).
    /// </summary>
    /// <returns>The released groups and digests.</returns>
    public IReadOnlyList<MediaOutput> ScanFinished() => Run(force: true);

    private static void Merge(RenderModel model, string itemId, MediaReady ready)
    {
        if (!model.ItemIds.Contains(itemId, StringComparer.Ordinal))
        {
            model.ItemIds.Add(itemId);
        }

        model.NewSeries |= ready.NewSeries;
        model.Upgraded &= ready.Upgraded;
    }

    private static void Close(QueueDocument q, MediaGroupState group, DateTimeOffset now)
    {
        if (!q.ReleaseBuffers.TryGetValue(group.DestinationId, out var buffer))
        {
            buffer = new ReleaseBuffer { FirstAt = now };
            q.ReleaseBuffers[group.DestinationId] = buffer;
        }

        buffer.LastAt = now;
        buffer.Groups.Add(group);
    }

    private IReadOnlyList<MediaOutput> Run(bool force)
    {
        var now = _time.GetUtcNow();
        var batching = _settings.Current.Batching;
        var scan = _scan.IsScanRunning;
        var quiet = TimeSpan.FromSeconds(batching.QuietWindowSeconds);
        var maxHold = TimeSpan.FromMinutes(batching.MaxHoldMinutes);
        var releaseQuiet = TimeSpan.FromSeconds(batching.ReleaseQuietSeconds);
        var outputs = new List<MediaOutput>();
        _state.UpdateQueue(q =>
        {
            var closing = force ? q.OpenGroups : q.OpenGroups.Where(g => now - g.LastAt >= quiet || now - g.FirstAt >= maxHold).ToList();
            if (closing.Count > 0)
            {
                var closed = closing.OrderBy(g => g.FirstAt).ToList();
                var set = closed.ToHashSet();
                q.OpenGroups.RemoveAll(set.Contains);
                foreach (var group in closed)
                {
                    Close(q, group, now);
                }
            }

            foreach (var (destinationId, buffer) in q.ReleaseBuffers.OrderBy(b => b.Key, StringComparer.Ordinal).ToList())
            {
                var due = force
                    || buffer.Groups.Count >= BufferCapOverride
                    || (scan
                        ? now - buffer.FirstAt >= ScanHoldLimit
                        : now - buffer.LastAt >= releaseQuiet || now - buffer.FirstAt >= maxHold);
                if (!due)
                {
                    continue;
                }

                // Released groups leave the document, so the outputs can hand them on without copying.
                q.ReleaseBuffers.Remove(destinationId);
                var groups = buffer.Groups.OrderBy(g => g.FirstAt).ToList();
                if (groups.Count > batching.DigestThreshold)
                {
                    outputs.Add(new MediaOutput(destinationId, MediaOutputKind.Digest, groups));
                }
                else
                {
                    outputs.AddRange(groups.Select(g => new MediaOutput(destinationId, MediaOutputKind.Group, [g])));
                }
            }
        });
        return outputs;
    }
}
