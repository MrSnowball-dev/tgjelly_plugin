using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Compat;
using Jellyfin.Plugin.TgJelly.Hosting;
using Jellyfin.Plugin.TgJelly.Library;

namespace Jellyfin.Plugin.TgJelly.Intake;

/// <summary>
/// The two bounded in-memory intake queues. Callbacks publish here in O(1) without I/O; the pipeline
/// drains them. An incompatible build accepts nothing (intake stays off).
/// </summary>
public sealed class IntakeQueues
{
    /// <summary>The media queue's capacity; when full, queued <see cref="MediaIntakeKind.Updated"/> events go first.</summary>
    public const int MediaCapacity = 10_000;

    /// <summary>The other queue's capacity; when full, the newest event is dropped.</summary>
    public const int OtherCapacity = 1_000;

    private readonly CompatResult _compat;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly LinkedList<MediaIntake> _media = new();
    private readonly LinkedList<NotificationEvent> _other = new();
    private readonly AsyncSignal _signal = new();
    private long _dropped;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntakeQueues"/> class.
    /// </summary>
    /// <param name="compat">The compatibility result.</param>
    /// <param name="time">The clock that stamps events.</param>
    public IntakeQueues(CompatResult compat, TimeProvider time)
    {
        _compat = compat;
        _time = time;
    }

    /// <summary>Gets the number of queued media events.</summary>
    public int MediaCount
    {
        get
        {
            lock (_gate)
            {
                return _media.Count;
            }
        }
    }

    /// <summary>Gets the number of queued security and server events.</summary>
    public int OtherCount
    {
        get
        {
            lock (_gate)
            {
                return _other.Count;
            }
        }
    }

    /// <summary>
    /// Queues a media event.
    /// </summary>
    /// <param name="kind">What happened.</param>
    /// <param name="item">The item, if any.</param>
    /// <returns><c>false</c> when the event was dropped or intake is off.</returns>
    public bool PublishMedia(MediaIntakeKind kind, ItemFacts? item)
    {
        if (!_compat.IsCompatible)
        {
            return false;
        }

        var intake = new MediaIntake(kind, item, _time.GetUtcNow());
        lock (_gate)
        {
            if (_media.Count >= MediaCapacity && !MakeRoom(kind))
            {
                Interlocked.Increment(ref _dropped);
                return false;
            }

            _media.AddLast(intake);
        }

        _signal.Set();
        return true;
    }

    /// <summary>
    /// Queues a security or server event.
    /// </summary>
    /// <param name="eventKey">The event key.</param>
    /// <param name="values">The payload.</param>
    /// <returns><c>false</c> when the event was dropped or intake is off.</returns>
    public bool Publish(string eventKey, IReadOnlyDictionary<string, string> values)
    {
        if (!_compat.IsCompatible)
        {
            return false;
        }

        var e = new NotificationEvent(eventKey, _time.GetUtcNow(), values);
        lock (_gate)
        {
            if (_other.Count >= OtherCapacity)
            {
                Interlocked.Increment(ref _dropped);
                return false;
            }

            _other.AddLast(e);
        }

        _signal.Set();
        return true;
    }

    /// <summary>
    /// Removes up to <paramref name="max"/> media events, oldest first.
    /// </summary>
    /// <param name="max">The maximum.</param>
    /// <returns>The events.</returns>
    public IReadOnlyList<MediaIntake> TakeMedia(int max) => Take(_media, max);

    /// <summary>
    /// Removes up to <paramref name="max"/> security and server events, oldest first.
    /// </summary>
    /// <param name="max">The maximum.</param>
    /// <returns>The events.</returns>
    public IReadOnlyList<NotificationEvent> TakeOther(int max) => Take(_other, max);

    /// <summary>
    /// Returns how many events were dropped since the last call, and resets the count.
    /// </summary>
    /// <returns>The number dropped.</returns>
    public long TakeDropped() => Interlocked.Exchange(ref _dropped, 0);

    /// <summary>
    /// Waits until an event is published or the timeout passes.
    /// </summary>
    /// <param name="timeout">The longest wait.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>A task.</returns>
    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, _time, ct);

    private List<T> Take<T>(LinkedList<T> queue, int max)
    {
        var taken = new List<T>();
        lock (_gate)
        {
            while (taken.Count < max && queue.First is { } first)
            {
                taken.Add(first.Value);
                queue.RemoveFirst();
            }
        }

        return taken;
    }

    // queued Updated events are evicted before Added ones. An incoming Updated is dropped rather than
    // pushing out an Added; a ScanFinished is never evicted. Each eviction counts as a drop.
    private bool MakeRoom(MediaIntakeKind incoming)
    {
        var victim = Find(n => n.Kind == MediaIntakeKind.Updated);
        if (victim is null && incoming == MediaIntakeKind.Updated)
        {
            return false;
        }

        victim ??= Find(n => n.Kind != MediaIntakeKind.ScanFinished);
        if (victim is null)
        {
            return false;
        }

        _media.Remove(victim);
        Interlocked.Increment(ref _dropped);
        return true;
    }

    private LinkedListNode<MediaIntake>? Find(Func<MediaIntake, bool> match)
    {
        for (var node = _media.First; node is not null; node = node.Next)
        {
            if (match(node.Value))
            {
                return node;
            }
        }

        return null;
    }
}
