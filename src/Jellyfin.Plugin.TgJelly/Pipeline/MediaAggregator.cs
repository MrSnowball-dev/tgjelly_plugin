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
/// Batches ready media per destination: open groups close after a quiet window or a maximum
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

    // An index over queue.json's open groups, so a big scan doesn't scan the list for every item. Only this class
    // changes the list; the index is rebuilt whenever the list it was built from is replaced or changed size.
    private readonly Dictionary<string, MediaGroupState> _openIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _openPerDestination = new(StringComparer.Ordinal);
    private List<MediaGroupState>? _indexed;

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
            EnsureIndex(q.OpenGroups);
            foreach (var destination in destinations)
            {
                var id = destination.Id;
                if (_openIndex.TryGetValue(IndexKey(id, key), out var open))
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

                if (_openPerDestination.GetValueOrDefault(id) >= OpenGroupsCapOverride)
                {
                    var oldest = q.OpenGroups.Where(g => g.DestinationId == id).MinBy(g => g.FirstAt)!;
                    q.OpenGroups.Remove(oldest);
                    Unindex(oldest);
                    Close(q, oldest, now);
                }

                var group = new MediaGroupState
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
                };
                q.OpenGroups.Add(group);
                Index(group);
            }
        });
        return outputs;
    }

    /// <summary>
    /// Finds the group a destination holds under <paramref name="key"/>, open or in its release buffer, for the Log.
    /// </summary>
    /// <param name="destinationId">The destination.</param>
    /// <param name="key">The group key.</param>
    /// <returns>When the group started and a copy of its items, or <c>null</c> when it isn't held.</returns>
    public (DateTimeOffset FirstAt, IReadOnlyList<string> ItemIds)? FindHeld(string destinationId, string key) => _state.ReadQueue(q =>
    {
        EnsureIndex(q.OpenGroups);
        var group = _openIndex.GetValueOrDefault(IndexKey(destinationId, key))
            ?? (q.ReleaseBuffers.TryGetValue(destinationId, out var buffer) ? buffer.Groups.Find(g => g.Key == key) : null);
        return group is null ? ((DateTimeOffset, IReadOnlyList<string>)?)null : (group.FirstAt, group.Model.ItemIds.ToList());
    });

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

    private static string IndexKey(string destinationId, string key) => destinationId + "|" + key;

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

    private void EnsureIndex(List<MediaGroupState> open)
    {
        if (ReferenceEquals(_indexed, open) && _openIndex.Count == open.Count)
        {
            return;
        }

        _openIndex.Clear();
        _openPerDestination.Clear();
        foreach (var group in open)
        {
            Index(group);
        }

        _indexed = open;
    }

    private void Index(MediaGroupState group)
    {
        _openIndex[IndexKey(group.DestinationId, group.Key)] = group;
        _openPerDestination[group.DestinationId] = _openPerDestination.GetValueOrDefault(group.DestinationId) + 1;
    }

    private void Unindex(MediaGroupState group)
    {
        _openIndex.Remove(IndexKey(group.DestinationId, group.Key));
        _openPerDestination[group.DestinationId] = _openPerDestination.GetValueOrDefault(group.DestinationId) - 1;
    }

    private IReadOnlyList<MediaOutput> Run(bool force)
    {
        var now = _time.GetUtcNow();
        var batching = _settings.Current.Batching;
        var scan = _scan.IsScanRunning;
        var quiet = TimeSpan.FromSeconds(batching.QuietWindowSeconds);
        var maxHold = TimeSpan.FromMinutes(batching.MaxHoldMinutes);
        var releaseQuiet = TimeSpan.FromSeconds(batching.ReleaseQuietSeconds);
        bool Closes(MediaGroupState g) => force || now - g.LastAt >= quiet || now - g.FirstAt >= maxHold;
        bool Due(ReleaseBuffer b) => force
            || b.Groups.Count >= BufferCapOverride
            || (scan ? now - b.FirstAt >= ScanHoldLimit : now - b.LastAt >= releaseQuiet || now - b.FirstAt >= maxHold);

        // Most ticks find nothing to do; checking first keeps queue.json from being rewritten while idle.
        if (!_state.ReadQueue(q => q.OpenGroups.Exists(Closes) || q.ReleaseBuffers.Values.Any(Due)))
        {
            return [];
        }

        var outputs = new List<MediaOutput>();
        _state.UpdateQueue(q =>
        {
            var closing = q.OpenGroups.Where(Closes).ToList();
            if (closing.Count > 0)
            {
                var closed = closing.OrderBy(g => g.FirstAt).ToList();
                var set = closed.ToHashSet();
                q.OpenGroups.RemoveAll(set.Contains);
                _indexed = null;
                foreach (var group in closed)
                {
                    Close(q, group, now);
                }
            }

            foreach (var (destinationId, buffer) in q.ReleaseBuffers.OrderBy(b => b.Key, StringComparer.Ordinal).ToList())
            {
                if (!Due(buffer))
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
