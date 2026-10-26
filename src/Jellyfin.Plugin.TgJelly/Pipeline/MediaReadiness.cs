using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Intake;
using Jellyfin.Plugin.TgJelly.Library;
using Jellyfin.Plugin.TgJelly.Log;
using Jellyfin.Plugin.TgJelly.State;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.Pipeline;

/// <summary>
/// Decides when a new item is ready to announce: pending items wait for their metadata
/// (a forwarded ItemUpdated) or the timeout, items that disappeared are dropped quietly, and upgrades are suppressed
/// unless <c>AnnounceUpgrades</c> is on. Its state lives in queue.json, so it survives restarts.
/// </summary>
public sealed class MediaReadiness
{
    /// <summary>Pending items kept; beyond this the oldest is treated as ready (the timeout path).</summary>
    public const int PendingCap = 20_000;

    /// <summary>Removed identities kept.</summary>
    public const int RemovedCap = 5_000;

    /// <summary>New series kept.</summary>
    public const int NewSeriesCap = 1_000;

    /// <summary>How long a removed identity marks a new item as an upgrade.</summary>
    public static readonly TimeSpan RemovedTtl = TimeSpan.FromHours(48);

    /// <summary>How long a new series labels its episodes "New series".</summary>
    public static readonly TimeSpan NewSeriesTtl = TimeSpan.FromHours(24);

    private readonly StateStore _state;
    private readonly SettingsStore _settings;
    private readonly ILibraryAccess _library;
    private readonly PendingIdSet _pending;
    private readonly TimeProvider _time;
    private readonly ILogger<MediaReadiness> _logger;
    private readonly LogRecorder? _log;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaReadiness"/> class.
    /// </summary>
    /// <param name="state">The state store holding queue.json.</param>
    /// <param name="settings">The settings, for the timeout and <c>AnnounceUpgrades</c>.</param>
    /// <param name="library">The library.</param>
    /// <param name="pending">The pending-ID set the ItemUpdated callback reads.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="log">Where dropped items are recorded for the Log, if anywhere.</param>
    public MediaReadiness(StateStore state, SettingsStore settings, ILibraryAccess library, PendingIdSet pending, TimeProvider time, ILogger<MediaReadiness> logger, LogRecorder? log = null)
    {
        _log = log;
        _state = state;
        _settings = settings;
        _library = library;
        _pending = pending;
        _time = time;
        _logger = logger;
    }

    /// <summary>Gets the number of items waiting for readiness.</summary>
    public int PendingCount => _state.ReadQueue(q => q.PendingMedia.Count);

    /// <summary>Gets or sets a lower cap for all three sections (tests only).</summary>
    internal int? CapOverride { get; set; }

    /// <summary>
    /// Puts the pending IDs from queue.json back into the pending-ID set. Call once the state is loaded.
    /// </summary>
    public void Load()
        => _pending.Reset(_state.ReadQueue(q => q.PendingMedia.Keys.Select(k => Guid.TryParseExact(k, "N", out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).ToList()));

    /// <summary>
    /// Processes one media intake event.
    /// </summary>
    /// <param name="intake">The event.</param>
    /// <returns>The items that became ready.</returns>
    public IReadOnlyList<MediaReady> Process(MediaIntake intake)
    {
        ArgumentNullException.ThrowIfNull(intake);
        if (intake.Item is not { } item)
        {
            return [];
        }

        var now = _time.GetUtcNow();
        switch (intake.Kind)
        {
            case MediaIntakeKind.Added:
                return Add(item.PendingId, now);
            case MediaIntakeKind.Updated:
                var key = Key(item.PendingId);
                if (_state.ReadQueue(q => q.PendingMedia.ContainsKey(key)))
                {
                    return Ready(item.PendingId, now);
                }

                _pending.Remove(item.PendingId);
                return [];
            case MediaIntakeKind.Removed:
                var evicted = 0;
                _state.UpdateQueue(q =>
                {
                    foreach (var identity in item.Identities)
                    {
                        q.RemovedIdentities[identity] = now;
                    }

                    evicted = EvictOldest(q.RemovedIdentities, Cap(RemovedCap));
                });
                CountEvictions(evicted);
                return [];
            case MediaIntakeKind.SeriesAdded:
                var evictedSeries = 0;
                _state.UpdateQueue(q =>
                {
                    q.NewSeries[Key(item.Id)] = now;
                    evictedSeries = EvictOldest(q.NewSeries, Cap(NewSeriesCap));
                });
                CountEvictions(evictedSeries);
                return [];
            default:
                return [];
        }
    }

    /// <summary>
    /// Announces items whose timeout passed and prunes the expired sections.
    /// </summary>
    /// <returns>The items that became ready.</returns>
    public IReadOnlyList<MediaReady> Tick()
    {
        var now = _time.GetUtcNow();
        var timeout = TimeSpan.FromMinutes(_settings.Current.Batching.ReadyTimeoutMinutes);
        var due = _state.ReadQueue(q => q.PendingMedia.Where(p => p.Value + timeout <= now).OrderBy(p => p.Value).Select(p => p.Key).ToList());
        var ready = new List<MediaReady>();
        foreach (var key in due)
        {
            if (Guid.TryParseExact(key, "N", out var id))
            {
                ready.AddRange(Ready(id, now));
            }
            else
            {
                _state.UpdateQueue(q => q.PendingMedia.Remove(key));
            }
        }

        if (_state.ReadQueue(q => q.RemovedIdentities.Values.Any(at => at + RemovedTtl <= now) || q.NewSeries.Values.Any(at => at + NewSeriesTtl <= now)))
        {
            _state.UpdateQueue(q =>
            {
                RemoveWhere(q.RemovedIdentities, at => at + RemovedTtl <= now);
                RemoveWhere(q.NewSeries, at => at + NewSeriesTtl <= now);
            });
        }

        return ready;
    }

    private static string Key(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

    private static void RemoveWhere(Dictionary<string, DateTimeOffset> items, Func<DateTimeOffset, bool> expired)
    {
        foreach (var key in items.Where(kv => expired(kv.Value)).Select(kv => kv.Key).ToList())
        {
            items.Remove(key);
        }
    }

    private static int EvictOldest(Dictionary<string, DateTimeOffset> items, int cap)
    {
        var over = items.Count - cap;
        if (over <= 0)
        {
            return 0;
        }

        foreach (var key in items.OrderBy(kv => kv.Value).Take(over).Select(kv => kv.Key).ToList())
        {
            items.Remove(key);
        }

        return over;
    }

    private int Cap(int cap) => CapOverride ?? cap;

    private void CountEvictions(int evicted)
    {
        if (evicted > 0)
        {
            _state.Update(s => s.Status.Counters.Evictions += evicted);
        }
    }

    private List<MediaReady> Add(Guid id, DateTimeOffset now)
    {
        var key = Key(id);
        _pending.Add(id);
        if (_state.ReadQueue(q => q.PendingMedia.ContainsKey(key)))
        {
            return [];
        }

        // Over the cap, the oldest pending item takes the timeout path: announced with what it has.
        var ready = new List<MediaReady>();
        var oldest = _state.ReadQueue(q => q.PendingMedia.Count >= Cap(PendingCap) ? q.PendingMedia.MinBy(p => p.Value).Key : null);
        if (oldest is not null && Guid.TryParseExact(oldest, "N", out var oldestId))
        {
            ready.AddRange(Ready(oldestId, now));
            CountEvictions(1);
        }

        var added = false;
        _state.UpdateQueue(q => added = q.PendingMedia.TryAdd(key, now));
        if (added)
        {
            _log?.Pending(id, _settings.Current.Batching.ReadyTimeoutMinutes);
        }

        return ready;
    }

    private List<MediaReady> Ready(Guid id, DateTimeOffset now)
    {
        var key = Key(id);
        _state.UpdateQueue(q => q.PendingMedia.Remove(key));
        _pending.Remove(id);
        if (_library.GetItem(id) is not { } item)
        {
            _log?.ItemGone(id);
            return [];
        }

        var libraries = _library.GetLibraries(id);
        var upgraded = false;
        if (item.Identities.Count > 0)
        {
            var removed = _state.ReadQueue(q => item.Identities.Any(i => q.RemovedIdentities.TryGetValue(i, out var at) && at + RemovedTtl > now));
            if (removed || _library.HasOtherCopy(item))
            {
                if (!_settings.Current.Message.AnnounceUpgrades)
                {
                    _logger.LogDebug("TgJelly skipped {Item}: an upgrade of something already announced", item.Name);
                    _log?.PendingDone(id);
                    _log?.UpgradeSkipped(item);
                    return [];
                }

                upgraded = true;
            }
        }

        var newSeries = item.Kind == MediaKind.Episode
            && _state.ReadQueue(q => q.NewSeries.TryGetValue(Key(item.SeriesId), out var at) && at + NewSeriesTtl > now);
        _log?.PendingDone(id);
        return [new MediaReady(item, libraries, upgraded, newSeries, now)];
    }
}
