using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.Intake;
using Jellyfin.Plugin.TgJelly.Library;
using Jellyfin.Plugin.TgJelly.Pipeline;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Strings;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.Log;

/// <summary>
/// Writes what the pipeline and Outbox delivery did into the <see cref="EventLog"/>, in plain words (design §8.2):
/// one row per message per destination, moved from Waiting to its final result in place. Recording never throws, so
/// it can't stop a message.
/// </summary>
public sealed class LogRecorder
{
    private const int SkipMergeCap = 1_000;

    /// <summary>A message sent later than this after it was queued is "Sent late".</summary>
    public static readonly TimeSpan LateAfter = TimeSpan.FromSeconds(60);

    /// <summary>A row still waiting this long after it was recorded was dropped unseen (the Outbox TTL is 24 h).</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(30);

    // Skipped items of one group (episodes of a season) within this window share one row.
    private static readonly TimeSpan SkipMergeWindow = TimeSpan.FromHours(1);

    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly EventLog _log;
    private readonly ILibraryAccess _library;
    private readonly StringTable _strings;
    private readonly TimeProvider _time;
    private readonly ILogger<LogRecorder> _logger;
    private readonly HealthService? _health;
    private readonly Dictionary<string, SkipGroup> _skips = new(StringComparer.Ordinal);
    private DateTimeOffset _lastSweep = DateTimeOffset.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="LogRecorder"/> class.
    /// </summary>
    /// <param name="log">The event log.</param>
    /// <param name="library">The library, for titles.</param>
    /// <param name="strings">The string table, for the reasons.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="health">The destinations' health, to say why a queued message is held.</param>
    public LogRecorder(EventLog log, ILibraryAccess library, StringTable strings, TimeProvider time, ILogger<LogRecorder> logger, HealthService? health = null)
    {
        _health = health;
        _log = log;
        _library = library;
        _strings = strings;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Returns the Log row ID of a held media group for one destination: the group, the destination and when the
    /// group started, so the same season next week gets a row of its own.
    /// </summary>
    /// <param name="groupKey">The group key.</param>
    /// <param name="destinationId">The destination.</param>
    /// <param name="firstAt">When the group started.</param>
    /// <returns>The row ID.</returns>
    public static string MediaId(string groupKey, string destinationId, DateTimeOffset firstAt)
        => string.Create(CultureInfo.InvariantCulture, $"{groupKey}|{destinationId}|{firstAt.UtcTicks}");

    /// <summary>
    /// Records the destinations a ready item doesn't go to, with the reason for each; or one row without a
    /// destination when none has its event on.
    /// </summary>
    /// <param name="ready">The ready item.</param>
    /// <param name="destinations">All destinations.</param>
    public void MediaRouted(MediaReady ready, IReadOnlyList<Destination> destinations) => Safe(() =>
    {
        var (key, eventKey, _) = GroupKeys.For(ready.Item);
        if (!destinations.Any(d => d.Events.Contains(eventKey, StringComparer.Ordinal)))
        {
            SkipMedia(ready.Item, key, eventKey, null, SkipReason.NoDestinationWants, null);
            return;
        }

        var libraryIds = ready.Libraries.Select(l => l.Id).ToList();
        foreach (var destination in destinations)
        {
            if (Router.Explain(eventKey, libraryIds, destination) is { } reason)
            {
                SkipMedia(ready.Item, key, eventKey, destination, reason, ready.Libraries);
            }
        }
    });

    /// <summary>
    /// Records a media group held for batching as Waiting.
    /// </summary>
    /// <param name="destination">The destination.</param>
    /// <param name="groupKey">The group key.</param>
    /// <param name="eventKey">The event key.</param>
    /// <param name="firstAt">When the group started.</param>
    /// <param name="itemIds">The group's items.</param>
    public void Held(Destination destination, string groupKey, string eventKey, DateTimeOffset firstAt, IReadOnlyList<string> itemIds) => Safe(() =>
    {
        var now = _time.GetUtcNow();
        var why = _strings.Get(eventKey == "media.episodes" ? "web.log.why.collectingEpisodes" : "web.log.why.collecting");
        var title = TitleOfIds(itemIds, eventKey);
        _log.Update(MediaId(groupKey, destination.Id, firstAt), existing => new LogEntry
        {
            Id = MediaId(groupKey, destination.Id, firstAt),
            At = existing?.At ?? now,
            UpdatedAt = now,
            EventKey = eventKey,
            Title = title,
            DestinationId = destination.Id,
            DestinationTitle = destination.Title,
            Result = LogResult.Waiting,
            Details = [why],
        });
    });

    /// <summary>
    /// Records the destinations a security or server message doesn't go to; edits of an earlier message are part of
    /// that message's row.
    /// </summary>
    /// <param name="output">The message.</param>
    /// <param name="destinations">All destinations.</param>
    public void AlertRouted(AlertOutput output, IReadOnlyList<Destination> destinations) => Safe(() =>
    {
        if (output.Op != OutboxOp.Send)
        {
            return;
        }

        var eventKey = output.Model.EventKey;
        var title = TitleOf(output.Model, eventKey, 0);
        if (!destinations.Any(d => d.Events.Contains(eventKey, StringComparer.Ordinal)))
        {
            Skip(NewId("skip"), eventKey, title, null, Why(SkipReason.NoDestinationWants, eventKey, null));
            return;
        }

        foreach (var destination in destinations)
        {
            if (Router.Explain(eventKey, null, destination) is { } reason)
            {
                Skip(NewId("skip"), eventKey, title, destination, Why(reason, eventKey, null));
            }
        }
    });

    /// <summary>
    /// Records a held group that its destination no longer wants when it is released (paused or switched off meanwhile).
    /// </summary>
    /// <param name="logId">The group's row.</param>
    /// <param name="destination">The destination.</param>
    /// <param name="model">The group's model.</param>
    /// <param name="reason">Why.</param>
    public void NotWanted(string logId, Destination destination, RenderModel model, SkipReason reason)
        => Settle(logId, destination, model, Why(reason, model.EventKey, null));

    /// <summary>
    /// Records that a held group's items were all removed from Jellyfin before it went out.
    /// </summary>
    /// <param name="logId">The group's row.</param>
    /// <param name="destination">The destination.</param>
    /// <param name="model">The group's model.</param>
    public void NothingToSend(string logId, Destination destination, RenderModel model)
        => Settle(logId, destination, model, _strings.Get("web.log.why.itemsGone"));

    /// <summary>
    /// Records that a held group's destination was removed. Changes an existing row only: a destination that no
    /// longer exists never gets a new row.
    /// </summary>
    /// <param name="logId">The group's row.</param>
    public void DestinationRemoved(string logId) => Safe(() =>
    {
        var now = _time.GetUtcNow();
        var why = Why(SkipReason.DestinationRemoved, string.Empty, null);
        _log.Update(logId, existing => existing is null
            ? null
            : existing with { Result = LogResult.Skipped, UpdatedAt = now, Details = [why], RetryAt = null, Advice = null });
    });

    /// <summary>
    /// Records a message handed to the Outbox as Waiting; an edit of a message already sent keeps that message's result.
    /// </summary>
    /// <param name="logId">The row of the entry that carries the message.</param>
    /// <param name="destination">The destination.</param>
    /// <param name="model">The model.</param>
    /// <param name="summary">For the rolling summary, how many events it stands for; 0 otherwise.</param>
    public void Queued(string logId, Destination destination, RenderModel model, int summary) => Safe(() =>
    {
        var now = _time.GetUtcNow();
        var title = TitleOf(model, model.EventKey, summary);
        var why = _strings.Get("web.log.why.queued");
        string? advice = null;
        string? telegram = null;

        // Delivery holds every message for a destination with a problem until it recovers (Check again).
        if (_health?.Get(destination.Id) is { Status: HealthStatus.Lost or HealthStatus.NeedsPermissions or HealthStatus.TopicMissing } health)
        {
            var args = Args(destination);
            why = _strings.Get("web.log.why.held." + health.Status, args);
            advice = _strings.Get("web.log.advice." + health.Status, args);
            telegram = health.LastError;
        }

        _log.Update(logId, existing =>
        {
            if (existing is { } kept && (IsSent(kept.Result) || kept.Result == LogResult.Failed))
            {
                return kept with { Title = title, UpdatedAt = now };
            }

            var retrying = existing is { Result: LogResult.Waiting, Telegram: not null };
            return new LogEntry
            {
                Id = logId,
                At = existing?.At ?? now,
                UpdatedAt = now,
                EventKey = summary > 0 ? "server.summary" : model.EventKey,
                Title = title,
                DestinationId = destination.Id,
                DestinationTitle = destination.Title,
                Result = LogResult.Waiting,
                Details = retrying ? existing!.Details : [why],
                Telegram = retrying ? existing!.Telegram : telegram,
                Tries = existing?.Tries ?? 0,
                RetryAt = retrying ? existing!.RetryAt : null,
                Advice = retrying ? existing!.Advice : advice,
            };
        });
    });

    /// <summary>
    /// Removes the rows of held groups that went out inside another message (a digest), which has its own row.
    /// </summary>
    /// <param name="logIds">The rows.</param>
    public void Merged(IReadOnlyList<string> logIds) => Safe(() => _log.Remove(logIds));

    /// <summary>
    /// Records an item removed from Jellyfin before it was ready, as a row without a destination.
    /// </summary>
    /// <param name="itemId">The item.</param>
    public void ItemGone(Guid itemId) => Safe(() =>
    {
        var title = _strings.Get("web.log.title.removedItem", ("id", itemId.ToString("N", CultureInfo.InvariantCulture)));
        Skip(NewId("gone"), "media", title, null, Why(SkipReason.ItemGone, "media", null));
    });

    /// <summary>
    /// Records an upgrade of something already announced, left out because announcing upgrades is off.
    /// </summary>
    /// <param name="item">The item.</param>
    public void UpgradeSkipped(MediaItem item) => Safe(() =>
    {
        if (GroupKeys.IsGrouped(item.Kind))
        {
            var (key, eventKey, _) = GroupKeys.For(item);
            SkipMedia(item, key, eventKey, null, SkipReason.Upgrade, null);
        }
    });

    /// <summary>
    /// Records a delivered message: Sent, or the most serious attention detail with every detail listed.
    /// </summary>
    /// <param name="entry">The delivered entry.</param>
    /// <param name="destination">Where it went.</param>
    /// <param name="noPhoto">Why the poster was left out (<c>blocked</c>, <c>rejected</c>, <c>unreadable</c>, <c>upload</c>), or <c>null</c>.</param>
    public void Delivered(OutboxEntry entry, Destination destination, string? noPhoto) => Safe(() =>
    {
        var now = _time.GetUtcNow();
        var title = TitleOf(entry.Model, entry.EventKey, entry.Summary);
        _log.Update(RowId(entry), existing =>
        {
            if (entry.Op == OutboxOp.Edit && existing is { } sent && IsSent(sent.Result))
            {
                return sent with { Title = title, UpdatedAt = now, DestinationId = destination.Id, DestinationTitle = destination.Title };
            }

            var tries = (existing?.Tries ?? 0) + 1;
            var details = new List<(LogResult Result, string Why, string Advice)>();
            if (entry.Message.TemplateFallback is { } error)
            {
                details.Add((LogResult.SentWithBuiltIn, _strings.Get("web.log.why.builtIn", ("error", error)), _strings.Get("web.log.advice.builtIn")));
            }

            if (noPhoto is not null)
            {
                details.Add((LogResult.SentWithoutPhoto, _strings.Get("web.log.why.noPhoto." + noPhoto), _strings.Get("web.log.advice.noPhoto." + noPhoto, ("destination", destination.Title))));
            }

            var delay = now - entry.QueuedAt;
            var lastError = existing?.Telegram;
            if (lastError is not null)
            {
                details.Add((LogResult.SentLate, _strings.Get("web.log.why.lateRetried", ("delay", Duration(delay)), ("error", lastError), ("tries", tries)), _strings.Get("web.log.advice.late")));
            }
            else if (entry.Attempts > 0 || delay > LateAfter)
            {
                details.Add((LogResult.SentLate, _strings.Get("web.log.why.late", ("delay", Duration(delay))), _strings.Get("web.log.advice.late")));
            }

            return new LogEntry
            {
                Id = RowId(entry),
                At = existing?.At ?? entry.QueuedAt,
                UpdatedAt = now,
                EventKey = entry.EventKey,
                Title = title,
                DestinationId = destination.Id,
                DestinationTitle = destination.Title,
                Result = details.Count == 0 ? LogResult.Sent : details[0].Result,
                Details = [.. details.Select(d => d.Why)],
                Telegram = lastError,
                Tries = tries,
                Advice = details.Count == 0 ? null : details[0].Advice,
            };
        });
    });

    /// <summary>
    /// Records a failed try that will be retried: the row stays Waiting, with Telegram's words and the retry time.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <param name="destination">The destination.</param>
    /// <param name="kind">What failed (Transport, ServerError, Unauthorized or RateLimited).</param>
    /// <param name="telegram">What Telegram or the network said, redacted.</param>
    /// <param name="retryAt">When it is tried again.</param>
    public void Retrying(OutboxEntry entry, Destination destination, DeliveryErrorKind kind, string telegram, DateTimeOffset retryAt) => Safe(() =>
    {
        var now = _time.GetUtcNow();
        _log.Update(RowId(entry), existing => (existing ?? NewRow(entry, destination, now)) with
        {
            UpdatedAt = now,
            DestinationId = destination.Id,
            DestinationTitle = destination.Title,
            Result = LogResult.Waiting,
            Details = [_strings.Get("web.log.why.retry." + kind)],
            Telegram = telegram,
            Tries = (existing?.Tries ?? 0) + 1,
            RetryAt = retryAt,
            Advice = _strings.Get("web.log.advice." + kind, ("destination", destination.Title)),
        });
    });

    /// <summary>
    /// Records a message Telegram refused: Failed, with Telegram's words, the tries and what to do.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <param name="destination">The destination.</param>
    /// <param name="error">The classified error.</param>
    /// <param name="telegram">What Telegram or the network said, with the error code, redacted.</param>
    /// <param name="afterFallbacks">Whether it failed after every fallback was tried.</param>
    public void Failed(OutboxEntry entry, Destination destination, DeliveryError error, string telegram, bool afterFallbacks = false) => Safe(() =>
    {
        var now = _time.GetUtcNow();
        var (why, advice) = error.Kind switch
        {
            DeliveryErrorKind.Lost or DeliveryErrorKind.NeedsPermissions or DeliveryErrorKind.TopicMissing or DeliveryErrorKind.Migrated when !afterFallbacks
                => ("web.log.why.failed." + error.Kind, "web.log.advice." + error.Kind),
            _ => (afterFallbacks ? "web.log.why.failed.fallbacks" : "web.log.why.failed.Other", "web.log.advice.Other"),
        };
        var args = Args(destination, error.Description);
        _log.Update(RowId(entry), existing => (existing ?? NewRow(entry, destination, now)) with
        {
            UpdatedAt = now,
            DestinationId = destination.Id,
            DestinationTitle = destination.Title,
            Result = LogResult.Failed,
            Details = [_strings.Get(why, args)],
            Telegram = telegram,
            Tries = (existing?.Tries ?? 0) + 1,
            RetryAt = null,
            Advice = _strings.Get(advice, args),
        });
    });

    /// <summary>
    /// Marks rows still Waiting long after the Outbox would have dropped them as Failed. Runs at most once a minute.
    /// </summary>
    public void Sweep() => Safe(() =>
    {
        var now = _time.GetUtcNow();
        if (now - _lastSweep < SweepInterval)
        {
            return;
        }

        _lastSweep = now;
        var why = _strings.Get("web.log.why.failed.expired");
        var advice = _strings.Get("web.log.advice.expired");
        _log.UpdateWhere(
            r => r.Result == LogResult.Waiting && now - r.At > StaleAfter,
            r => r with { Result = LogResult.Failed, UpdatedAt = now, Details = [why], RetryAt = null, Advice = advice });
    });

    private static bool IsSent(LogResult result)
        => result is LogResult.Sent or LogResult.SentLate or LogResult.SentWithBuiltIn or LogResult.SentWithoutPhoto;

    private static string RowId(OutboxEntry entry) => entry.LogId.Length > 0 ? entry.LogId : entry.EntryId;

    private static string NewId(string prefix) => prefix + "|" + Guid.NewGuid().ToString("N");

    private static string TitleOfItems(IReadOnlyList<MediaItem> items)
    {
        var first = items[0];
        switch (first.Kind)
        {
            case MediaKind.Episode:
                var series = first.SeriesName ?? first.Name;
                if (first.SeasonNumber is not { } season)
                {
                    return series;
                }

                var numbers = items.Select(i => i.EpisodeNumber).OfType<int>().Distinct().Order().ToList();
                return numbers.Count switch
                {
                    0 => string.Create(CultureInfo.InvariantCulture, $"{series}, S{season:00}"),
                    1 => string.Create(CultureInfo.InvariantCulture, $"{series}, S{season:00}E{numbers[0]:00}"),
                    _ => string.Create(CultureInfo.InvariantCulture, $"{series}, S{season:00}E{numbers[0]:00}–E{numbers[^1]:00}"),
                };
            case MediaKind.AudioBook:
                return first.ParentName ?? first.Name;
            case MediaKind.MusicAlbum:
                return string.IsNullOrWhiteSpace(first.AlbumArtist) ? first.Name : first.AlbumArtist + " – " + first.Name;
            default:
                return first.Name;
        }
    }

    private (string Name, object? Value)[] Args(Destination destination, string? error = null)
    {
        var right = _strings.Get(destination.Kind == ChatKind.Channel ? "web.log.right.post" : "web.log.right.send");
        return [("destination", destination.Title), ("right", right), ("error", error)];
    }

    private string EventLabel(string eventKey) => _strings.Get("web.event." + eventKey);

    private string TitleOfIds(IEnumerable<string> itemIds, string eventKey)
    {
        var items = itemIds
            .Select(id => Guid.TryParseExact(id, "N", out var guid) ? _library.GetItem(guid) : null)
            .OfType<MediaItem>()
            .ToList();
        return items.Count > 0 ? TitleOfItems(items) : EventLabel(eventKey);
    }

    private string TitleOf(RenderModel? model, string eventKey, int summary)
    {
        if (summary > 0)
        {
            return _strings.Plural("web.log.title.summary", summary);
        }

        if (model is null)
        {
            return EventLabel(eventKey);
        }

        switch (model.Kind)
        {
            case RenderKind.Media:
                return TitleOfIds(model.ItemIds, model.EventKey);
            case RenderKind.Digest:
                return _strings.Plural("web.log.title.digest", model.Groups.Sum(g => g.ItemIds.Count));
            default:
                string? Value(string key) => model.Values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
                var plugin = Value(EventFields.Plugin) is { } name ? (Value(EventFields.Version) is { } version ? name + " " + version : name) : null;
                return Value(EventFields.User) ?? plugin ?? Value(EventFields.Task) ?? Value(EventFields.Version) ?? Value(EventFields.Server) ?? EventLabel(model.EventKey);
        }
    }

    private string Duration(TimeSpan span)
    {
        if (span < TimeSpan.FromSeconds(120))
        {
            return _strings.Get("web.log.duration.seconds", ("count", Math.Max(1, (long)Math.Round(span.TotalSeconds))));
        }

        return span < TimeSpan.FromMinutes(120)
            ? _strings.Get("web.log.duration.minutes", ("count", (long)Math.Round(span.TotalMinutes)))
            : _strings.Get("web.log.duration.hours", ("count", (long)Math.Round(span.TotalHours)));
    }

    private string Why(SkipReason reason, string eventKey, IReadOnlyList<LibraryRef>? libraries) => reason switch
    {
        SkipReason.Paused => _strings.Get("web.log.why.paused"),
        SkipReason.EventOff => _strings.Get("web.log.why.eventOff", ("event", EventLabel(eventKey))),
        SkipReason.LibraryExcluded when libraries is { Count: > 0 }
            => _strings.Get("web.log.why.libraryExcluded", ("library", string.Join(", ", libraries.Select(l => l.Name)))),
        SkipReason.LibraryExcluded => _strings.Get("web.log.why.libraryUnknown"),
        SkipReason.NoDestinationWants => _strings.Get("web.log.why.noDestinationWants", ("event", EventLabel(eventKey))),
        SkipReason.ItemGone => _strings.Get("web.log.why.itemGone"),
        SkipReason.Upgrade => _strings.Get("web.log.why.upgrade"),
        _ => _strings.Get("web.log.why.destinationRemoved"),
    };

    private LogEntry NewRow(OutboxEntry entry, Destination destination, DateTimeOffset now) => new()
    {
        Id = RowId(entry),
        At = entry.QueuedAt,
        UpdatedAt = now,
        EventKey = entry.EventKey,
        Title = TitleOf(entry.Model, entry.EventKey, entry.Summary),
        DestinationId = destination.Id,
        DestinationTitle = destination.Title,
        Result = LogResult.Waiting,
    };

    private void Settle(string logId, Destination destination, RenderModel model, string why) => Safe(() =>
    {
        var now = _time.GetUtcNow();
        var title = TitleOf(model, model.EventKey, 0);

        // A late edit with nothing left to send leaves the message it would have edited as it was.
        _log.Update(logId, existing => existing is { } final && (IsSent(final.Result) || final.Result == LogResult.Failed) ? null : new LogEntry
        {
            Id = logId,
            At = existing?.At ?? now,
            UpdatedAt = now,
            EventKey = model.EventKey,
            Title = existing?.Title ?? title,
            DestinationId = destination.Id,
            DestinationTitle = destination.Title,
            Result = LogResult.Skipped,
            Details = [why],
        });
    });

    private void Skip(string id, string eventKey, string title, Destination? destination, string why)
    {
        var now = _time.GetUtcNow();
        _log.Upsert(new LogEntry
        {
            Id = id,
            At = now,
            UpdatedAt = now,
            EventKey = eventKey,
            Title = title,
            DestinationId = destination?.Id,
            DestinationTitle = destination?.Title,
            Result = LogResult.Skipped,
            Details = [why],
        });
    }

    // Items of one group skipped for the same destination and reason within an hour share a row, so a season the
    // destination doesn't get is one row, like the message it would have been.
    private void SkipMedia(MediaItem item, string groupKey, string eventKey, Destination? destination, SkipReason reason, IReadOnlyList<LibraryRef>? libraries)
    {
        var now = _time.GetUtcNow();
        var mergeKey = groupKey + "|" + (destination?.Id ?? "-") + "|" + reason;
        SkipGroup group;
        lock (_skips)
        {
            if (!_skips.TryGetValue(mergeKey, out var found) || now - found.At > SkipMergeWindow)
            {
                if (_skips.Count >= SkipMergeCap)
                {
                    foreach (var old in _skips.Where(s => now - s.Value.At > SkipMergeWindow).Select(s => s.Key).ToList())
                    {
                        _skips.Remove(old);
                    }

                    if (_skips.Count >= SkipMergeCap)
                    {
                        _skips.Clear();
                    }
                }

                found = new SkipGroup(NewId("skip"), now, []);
                _skips[mergeKey] = found;
            }

            if (!found.Items.Contains(item.Id))
            {
                found.Items.Add(item.Id);
            }

            group = found with { Items = [.. found.Items] };
        }

        var items = group.Items.Select(id => id == item.Id ? item : _library.GetItem(id)).OfType<MediaItem>().ToList();
        var why = Why(reason, eventKey, libraries);
        _log.Upsert(new LogEntry
        {
            Id = group.Id,
            At = group.At,
            UpdatedAt = now,
            EventKey = eventKey,
            Title = TitleOfItems(items),
            DestinationId = destination?.Id,
            DestinationTitle = destination?.Title,
            Result = LogResult.Skipped,
            Details = [why],
        });
    }

    private void Safe(Action record)
    {
        try
        {
            record();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "TgJelly could not record a Log row; sending is not affected");
        }
    }

    private sealed record SkipGroup(string Id, DateTimeOffset At, List<Guid> Items);
}
