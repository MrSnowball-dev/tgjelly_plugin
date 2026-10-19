using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Strings;

namespace Jellyfin.Plugin.TgJelly.Delivery;

/// <summary>
/// The queue rules of the Outbox (spec §9.3) over <c>queue.json</c>: per-destination caps with the rolling summary
/// for security and server entries, the 24 h TTL, priority, edit merging, retries, and the sent-message and
/// file_id records. Delivery itself is <c>OutboxDelivery</c>.
/// </summary>
public sealed class Outbox
{
    /// <summary>Media entries kept per destination; the oldest is dropped beyond this.</summary>
    public const int MediaCap = 500;

    /// <summary>Security and server entries per destination; beyond this they fold into the rolling summary.</summary>
    public const int AlertCap = 50;

    /// <summary>Sent messages kept for edits and replies.</summary>
    public const int SentMessagesCap = 5000;

    /// <summary>Cached poster file_ids.</summary>
    public const int FileIdCacheCap = 2000;

    /// <summary>How long an entry may wait.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    /// <summary>How long a cached file_id is reused.</summary>
    public static readonly TimeSpan FileIdTtl = TimeSpan.FromHours(24);

    // A sent message stays editable this long after its last edit (the sliding security window, spec §7.5).
    private static readonly TimeSpan SentWindowGrace = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(5);

    private readonly StateStore _state;
    private readonly StringTable _strings;
    private readonly TimeProvider _time;

    // The entry Pick last handed to delivery, read and written under the state lock. A late edit must not merge into
    // it: delivery holds a copy and writes that copy back (Complete, Replace), which would drop the merged content.
    private string? _inFlight;

    /// <summary>
    /// Initializes a new instance of the <see cref="Outbox"/> class.
    /// </summary>
    /// <param name="state">The state store holding <c>queue.json</c>.</param>
    /// <param name="strings">The string table, for the rolling summary.</param>
    /// <param name="time">The clock.</param>
    public Outbox(StateStore state, StringTable strings, TimeProvider time)
    {
        _state = state;
        _strings = strings;
        _time = time;
    }

    /// <summary>
    /// Raised after the queue changed.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Gets the number of queued entries.
    /// </summary>
    public int Count => _state.ReadQueue(q => q.Outbox.Count);

    /// <summary>
    /// The retry delay for an attempt: 2 s doubling up to 5 min, then ±20 % jitter.
    /// </summary>
    /// <param name="attempt">The attempt number, from 1.</param>
    /// <param name="random01">A random number in [0, 1].</param>
    /// <returns>The delay.</returns>
    public static TimeSpan Backoff(int attempt, double random01)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 30);
        var seconds = Math.Min(FirstRetry.TotalSeconds * Math.Pow(2, exponent), MaxRetry.TotalSeconds);
        return TimeSpan.FromSeconds(seconds * (0.8 + (0.4 * random01)));
    }

    /// <summary>
    /// Returns the IDs of the destinations that have queued entries.
    /// </summary>
    /// <returns>The IDs.</returns>
    public IReadOnlyList<string> DestinationIds() => _state.ReadQueue(q => q.Outbox.Select(e => e.DestinationId).Distinct().ToList());

    /// <summary>
    /// Returns copies of the queued entries.
    /// </summary>
    /// <returns>The entries.</returns>
    public IReadOnlyList<OutboxEntry> Snapshot() => _state.ReadQueue(q => q.Outbox.Select(Copy).ToList());

    /// <summary>
    /// Queues an entry, applying edit merging, the media cap and the security/server cap with its rolling summary.
    /// Security and server entries are written to <c>queue.json</c> within 1 s.
    /// </summary>
    /// <param name="entry">The entry.</param>
    public void Enqueue(OutboxEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var now = _time.GetUtcNow();
        var group = EventCatalog.GroupOf(entry.EventKey);
        var dropped = 0;
        _state.UpdateQueue(
            q =>
            {
                var mine = q.Outbox.Where(e => e.DestinationId == entry.DestinationId);
                if (entry.Op == OutboxOp.Edit && entry.TargetKey is not null)
                {
                    // The message isn't sent yet: render it again instead of queuing an edit (spec §7.5 step 3).
                    var unsent = q.Outbox.Find(e => e.Op == OutboxOp.Send && e.DestinationId == entry.DestinationId && e.SentKey == entry.TargetKey);
                    if (unsent is not null && unsent.EntryId != _inFlight)
                    {
                        var message = entry.Message;
                        message.ForceTextOnly |= unsent.Message.ForceTextOnly;
                        message.NoButtons |= unsent.Message.NoButtons;
                        message.PlainText |= unsent.Message.PlainText;
                        unsent.Message = message;
                        unsent.Model = entry.Model ?? unsent.Model;
                        return;
                    }

                    var queued = q.Outbox.FindIndex(e => e.Op == OutboxOp.Edit && e.DestinationId == entry.DestinationId && e.TargetKey == entry.TargetKey);
                    if (queued >= 0)
                    {
                        q.Outbox[queued] = entry;
                        return;
                    }

                    q.Outbox.Add(entry);
                    return;
                }

                if (group == "media")
                {
                    var media = mine.Where(e => e.Op == OutboxOp.Send && EventCatalog.GroupOf(e.EventKey) == "media").OrderBy(e => e.QueuedAt).ToList();
                    for (var i = 0; i <= media.Count - MediaCap; i++)
                    {
                        q.Outbox.Remove(media[i]);
                        dropped++;
                    }
                }
                else if (IsAlert(entry) && mine.Count(IsAlert) >= AlertCap)
                {
                    var summary = q.Outbox.FirstOrDefault(e => e.DestinationId == entry.DestinationId && e.Summary > 0);
                    if (summary is null)
                    {
                        summary = new OutboxEntry
                        {
                            EntryId = Guid.NewGuid().ToString("N"),
                            DestinationId = entry.DestinationId,
                            EventKey = "server.summary",
                            GroupKey = "summary",
                            QueuedAt = now,
                        };
                        q.Outbox.Add(summary);
                    }

                    summary.Summary++;
                    var text = _strings.Plural("outbox.summary", summary.Summary);
                    summary.Message = new OutboxMessage { Text = text, Caption = text, Silent = entry.Message.Silent };
                    return;
                }

                q.Outbox.Add(entry);
            },
            urgent: group != "media");

        AddCounters(droppedOutbox: dropped);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Picks the next entry to deliver: per destination, security and server entries before media, oldest first,
    /// skipping entries waiting for a retry time or for an unsent reply target; then the pacing check; across
    /// destinations, security and server first, then the oldest.
    /// </summary>
    /// <param name="destinations">The destinations by ID; missing or undeliverable ones are held.</param>
    /// <param name="pacing">The pacing gate.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The pick.</returns>
    public OutboxPick Pick(IReadOnlyDictionary<string, DestinationView> destinations, PacingGate pacing, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(destinations);
        ArgumentNullException.ThrowIfNull(pacing);
        return _state.ReadQueue(q =>
        {
            DateTimeOffset? wake = null;
            OutboxEntry? best = null;
            foreach (var group in q.Outbox.GroupBy(e => e.DestinationId))
            {
                if (!destinations.TryGetValue(group.Key, out var view) || !view.Deliverable)
                {
                    continue;
                }

                OutboxEntry? head = null;
                foreach (var candidate in group.OrderBy(e => IsPriority(e) ? 0 : 1).ThenBy(e => e.QueuedAt))
                {
                    if (candidate.NotBefore is { } notBefore && notBefore > now)
                    {
                        wake = Min(wake, notBefore);
                        continue;
                    }

                    if (candidate.ReplyToKey is { } replyTo
                        && !q.SentMessages.ContainsKey(SentKey(group.Key, replyTo))
                        && group.Any(o => o.SentKey == replyTo && o.EntryId != candidate.EntryId))
                    {
                        continue;
                    }

                    // An edit waits while the message it edits is still queued; sent now, it would become a duplicate.
                    if (candidate.Op == OutboxOp.Edit && candidate.TargetKey is { } target
                        && group.Any(o => o.Op == OutboxOp.Send && o.SentKey == target))
                    {
                        continue;
                    }

                    head = candidate;
                    break;
                }

                if (head is null)
                {
                    continue;
                }

                var groupOrChannel = view.Kind is ChatKind.Group or ChatKind.Supergroup or ChatKind.Channel;
                var allowed = pacing.NextAllowed(view.ChatId, groupOrChannel, now);
                if (allowed > now)
                {
                    wake = Min(wake, allowed);
                    continue;
                }

                if (best is null || Better(head, best))
                {
                    best = head;
                }
            }

            _inFlight = best?.EntryId;
            return new OutboxPick(best is null ? null : Copy(best), wake);
        });
    }

    /// <summary>
    /// Removes a delivered entry and records the sent message under its <c>SentKey</c>; for an edit, updates the
    /// target's <c>LastEditAt</c>.
    /// </summary>
    /// <param name="entry">The delivered entry.</param>
    /// <param name="sent">The sent message, or <c>null</c> for an edit.</param>
    public void Complete(OutboxEntry entry, SentMessage? sent)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var now = _time.GetUtcNow();
        var evicted = 0;
        _state.UpdateQueue(q =>
        {
            q.Outbox.RemoveAll(e => e.EntryId == entry.EntryId);
            Landed(entry.EntryId);
            if (sent is not null && entry.SentKey is not null)
            {
                sent.Model = entry.Model;
                q.SentMessages[SentKey(entry.DestinationId, entry.SentKey)] = sent;
                evicted = EvictOldest(q.SentMessages, SentMessagesCap, m => m.SentAt);
            }

            if (sent is null && entry.Op == OutboxOp.Edit && entry.TargetKey is not null
                && q.SentMessages.TryGetValue(SentKey(entry.DestinationId, entry.TargetKey), out var target))
            {
                target.LastEditAt = now;
                target.Model = entry.Model ?? target.Model;
            }
        });

        AddCounters(evictions: evicted);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Schedules a retry.
    /// </summary>
    /// <param name="entryId">The entry.</param>
    /// <param name="notBefore">The earliest retry time.</param>
    /// <param name="countAttempt">Whether this counts as an attempt (a 429 doesn't).</param>
    public void Retry(string entryId, DateTimeOffset notBefore, bool countAttempt)
        => Mutate(entryId, e =>
        {
            e.NotBefore = notBefore;
            e.Attempts += countAttempt ? 1 : 0;
        });

    /// <summary>
    /// Replaces a queued entry (by ID) after a fallback changed it.
    /// </summary>
    /// <param name="entry">The changed entry.</param>
    public void Replace(OutboxEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _state.UpdateQueue(q =>
        {
            var i = q.Outbox.FindIndex(e => e.EntryId == entry.EntryId);
            if (i >= 0)
            {
                q.Outbox[i] = Copy(entry);
            }

            Landed(entry.EntryId);
        });
    }

    /// <summary>
    /// Drops an entry that can't be delivered, counting it.
    /// </summary>
    /// <param name="entryId">The entry.</param>
    public void Drop(string entryId)
    {
        var removed = 0;
        _state.UpdateQueue(q =>
        {
            removed = q.Outbox.RemoveAll(e => e.EntryId == entryId);
            Landed(entryId);
        });
        AddCounters(droppedOutbox: removed);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Drops entries older than the TTL, counting them.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <returns>How many were dropped.</returns>
    public int PurgeExpired(DateTimeOffset now)
    {
        if (!_state.ReadQueue(q => q.Outbox.Any(e => e.QueuedAt + Ttl <= now)))
        {
            return 0;
        }

        var removed = 0;
        _state.UpdateQueue(q => removed = q.Outbox.RemoveAll(e => e.QueuedAt + Ttl <= now));
        AddCounters(droppedOutbox: removed);
        return removed;
    }

    /// <summary>
    /// Forgets a removed destination's entries and sent messages, without counting them as drops.
    /// </summary>
    /// <param name="destinationId">The destination.</param>
    public void RemoveDestination(string destinationId)
    {
        var prefix = destinationId + "|";
        _state.UpdateQueue(q =>
        {
            q.Outbox.RemoveAll(e => e.DestinationId == destinationId);
            foreach (var key in q.SentMessages.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                q.SentMessages.Remove(key);
            }
        });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Moves a destination's entries and sent messages to another destination (a migration merge).
    /// </summary>
    /// <param name="fromDestinationId">The merged-away destination.</param>
    /// <param name="toDestinationId">The destination that remains.</param>
    public void Repoint(string fromDestinationId, string toDestinationId)
    {
        var prefix = fromDestinationId + "|";
        _state.UpdateQueue(q =>
        {
            foreach (var e in q.Outbox.Where(e => e.DestinationId == fromDestinationId))
            {
                e.DestinationId = toDestinationId;
            }

            foreach (var key in q.SentMessages.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                q.SentMessages.TryAdd(SentKey(toDestinationId, key[prefix.Length..]), q.SentMessages[key]);
                q.SentMessages.Remove(key);
            }
        });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Looks up a sent message.
    /// </summary>
    /// <param name="destinationId">The destination.</param>
    /// <param name="key">The key it was sent under.</param>
    /// <param name="sent">A copy of the record.</param>
    /// <returns><c>true</c> when found.</returns>
    public bool TryGetSent(string destinationId, string key, [NotNullWhen(true)] out SentMessage? sent)
    {
        var found = _state.ReadQueue(q => q.SentMessages.TryGetValue(SentKey(destinationId, key), out var m) ? Copy(m) : null);
        sent = found;
        return found is not null;
    }

    /// <summary>
    /// Finds the latest model of the message under <paramref name="key"/>: a queued edit's, else a queued send's, else
    /// the sent message's.
    /// </summary>
    /// <param name="destinationId">The destination.</param>
    /// <param name="key">The <c>sentMessages</c> key.</param>
    /// <returns>The record, or <c>null</c> when there is none with a model.</returns>
    public SentRecord? FindModel(string destinationId, string key) => _state.ReadQueue(q =>
    {
        var send = q.Outbox.Find(e => e.Op == OutboxOp.Send && e.DestinationId == destinationId && e.SentKey == key);
        var edit = q.Outbox.Find(e => e.Op == OutboxOp.Edit && e.DestinationId == destinationId && e.TargetKey == key);
        q.SentMessages.TryGetValue(SentKey(destinationId, key), out var sent);
        if (edit?.Model is { } editModel)
        {
            return new SentRecord(Copy(editModel), sent?.SentAt ?? edit.QueuedAt, sent is null);
        }

        if (send?.Model is { } sendModel)
        {
            return new SentRecord(Copy(sendModel), send.QueuedAt, true);
        }

        return sent?.Model is { } sentModel ? new SentRecord(Copy(sentModel), sent.SentAt, false) : null;
    });

    /// <summary>
    /// Forgets sent messages nothing will edit any more: kept until max(<c>sentAt</c> + LateEditHours,
    /// <c>lastEditAt</c> + 10 min) (spec §10.3). Not counted as evictions.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <param name="lateEditHours">The late-edit window in hours.</param>
    /// <returns>How many were removed.</returns>
    public int PruneSent(DateTimeOffset now, int lateEditHours)
    {
        var lateEdit = TimeSpan.FromHours(lateEditHours);
        bool Expired(SentMessage m) => now >= Max(m.SentAt + lateEdit, (m.LastEditAt ?? m.SentAt) + SentWindowGrace);
        if (!_state.ReadQueue(q => q.SentMessages.Values.Any(Expired)))
        {
            return 0;
        }

        var removed = 0;
        _state.UpdateQueue(q =>
        {
            foreach (var key in q.SentMessages.Where(kv => Expired(kv.Value)).Select(kv => kv.Key).ToList())
            {
                q.SentMessages.Remove(key);
                removed++;
            }
        });
        return removed;
    }

    /// <summary>
    /// Looks up a cached poster file_id younger than 24 h.
    /// </summary>
    /// <param name="cacheKey">The poster's cache key.</param>
    /// <param name="now">The current time.</param>
    /// <param name="fileId">The file_id.</param>
    /// <returns><c>true</c> when found.</returns>
    public bool TryGetFileId(string cacheKey, DateTimeOffset now, [NotNullWhen(true)] out string? fileId)
    {
        fileId = _state.ReadQueue(q => q.FileIdCache.TryGetValue(cacheKey, out var c) && now - c.CachedAt < FileIdTtl ? c.FileId : null);
        return fileId is not null;
    }

    /// <summary>
    /// Caches a poster's file_id after its first upload.
    /// </summary>
    /// <param name="cacheKey">The poster's cache key.</param>
    /// <param name="fileId">The file_id Telegram returned.</param>
    /// <param name="now">The current time.</param>
    public void CacheFileId(string cacheKey, string fileId, DateTimeOffset now)
    {
        var evicted = 0;
        _state.UpdateQueue(q =>
        {
            q.FileIdCache[cacheKey] = new CachedFileId { FileId = fileId, CachedAt = now };
            evicted = EvictOldest(q.FileIdCache, FileIdCacheCap, c => c.CachedAt);
        });
        AddCounters(evictions: evicted);
    }

    private static string SentKey(string destinationId, string key) => destinationId + "|" + key;

    private static bool IsAlert(OutboxEntry e)
        => e.Op == OutboxOp.Send && e.Summary == 0 && EventCatalog.GroupOf(e.EventKey) is "security" or "server";

    private static bool IsPriority(OutboxEntry e) => EventCatalog.GroupOf(e.EventKey) != "media";

    private static bool Better(OutboxEntry a, OutboxEntry b)
        => IsPriority(a) != IsPriority(b) ? IsPriority(a) : a.QueuedAt < b.QueuedAt;

    private static DateTimeOffset? Min(DateTimeOffset? a, DateTimeOffset b) => a is { } x && x <= b ? x : b;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;

    private static int EvictOldest<T>(Dictionary<string, T> items, int cap, Func<T, DateTimeOffset> age)
    {
        var over = items.Count - cap;
        if (over <= 0)
        {
            return 0;
        }

        foreach (var key in items.OrderBy(kv => age(kv.Value)).Take(over).Select(kv => kv.Key).ToList())
        {
            items.Remove(key);
        }

        return over;
    }

    private static T Copy<T>(T value)
        => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonDefaults.Options), JsonDefaults.Options)!;

    private void Mutate(string entryId, Action<OutboxEntry> change)
    {
        _state.UpdateQueue(q =>
        {
            var entry = q.Outbox.Find(e => e.EntryId == entryId);
            if (entry is not null)
            {
                change(entry);
            }

            Landed(entryId);
        });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Landed(string entryId)
    {
        if (_inFlight == entryId)
        {
            _inFlight = null;
        }
    }

    private void AddCounters(int droppedOutbox = 0, int evictions = 0)
    {
        if (droppedOutbox == 0 && evictions == 0)
        {
            return;
        }

        _state.Update(s =>
        {
            s.Status.Counters.DroppedOutbox += droppedOutbox;
            s.Status.Counters.Evictions += evictions;
        });
    }
}
