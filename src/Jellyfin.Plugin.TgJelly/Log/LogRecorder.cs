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
/// Writes what the pipeline and Outbox delivery did into the <see cref="EventLog"/>, in plain words:
/// one row per message per destination, moved from Waiting to its final result in place. Recording never throws, so
/// it can't stop a message.
/// </summary>
public sealed class LogRecorder
{
    private const int SkipMergeCap = 1_000;

    /// <summary>
    /// A row still waiting this long after it was recorded was lost track of: the Outbox drops entries after 24 h and
    /// reports it, and batching holds groups for at most a few hours, so only a gap in the recording leaves one.
    /// </summary>
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
    private readonly SettingsStore? _settings;
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
    /// <param name="settings">The settings, to name a destination whose health changed.</param>
    public LogRecorder(EventLog log, ILibraryAccess library, StringTable strings, TimeProvider time, ILogger<LogRecorder> logger, HealthService? health = null, SettingsStore? settings = null)
    {
        _health = health;
        _settings = settings;
        if (health is not null)
        {
            health.ProblemFound += OnProblemFound;
        }

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
    /// Returns the Log row of an Outbox entry: its <c>LogId</c>, or its entry ID when it was queued before the Log existed.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The row ID.</returns>
    public static string RowIdOf(OutboxEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.LogId.Length > 0 ? entry.LogId : entry.EntryId;
    }

    /// <summary>
    /// Records the messages a disconnect dropped from the Outbox: the owner disconnected the bot, so they are Skipped.
    /// A message already sent, or already failed, keeps its row as it was.
    /// </summary>
    /// <param name="logIds">The dropped entries' rows.</param>
    public void Disconnected(IReadOnlyList<string> logIds) => Safe(() =>
    {
        var now = _time.GetUtcNow();
        var why = _strings.Get("web.log.why.disconnected");
        foreach (var id in logIds.Distinct(StringComparer.Ordinal))
        {
            _log.Update(id, existing => existing is null || IsSettled(existing)
                ? null
                : existing with { Result = LogResult.Skipped, UpdatedAt = now, Details = [why], RetryAt = null, HeldUntil = null, Advice = null });
        }
    });

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
        _log.Update(logId, existing => existing is null || IsSettled(existing)
            ? null
            : existing with { Result = LogResult.Skipped, UpdatedAt = now, Details = [why], RetryAt = null, HeldUntil = null, Advice = null });
    });

    /// <summary>
    /// Records Outbox entries dropped because more than <paramref name="cap"/> media messages waited for one destination.
    /// </summary>
    /// <param name="logIds">The dropped entries' rows.</param>
    /// <param name="cap">The cap.</param>
    public void OverCap(IReadOnlyList<string> logIds, int cap)
        => Discard(logIds, _strings.Get("web.log.why.failed.cap", ("count", cap)), _strings.Get("web.log.advice.cap"));

    /// <summary>
    /// Records Outbox entries dropped because they waited longer than the Outbox keeps them.
    /// </summary>
    /// <param name="logIds">The dropped entries' rows.</param>
    public void Expired(IReadOnlyList<string> logIds)
        => Discard(logIds, _strings.Get("web.log.why.failed.expired"), _strings.Get("web.log.advice.expired"), _strings.Get("web.log.advice.expiredPlain"));

    /// <summary>
    /// Records an item that is waiting for Jellyfin to finish reading its details, as Waiting without a destination.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="timeoutMinutes">How long it waits at most.</param>
    public void Pending(Guid itemId, int timeoutMinutes) => Safe(() =>
    {
        if (_library.GetItem(itemId) is not { } item || !GroupKeys.IsGrouped(item.Kind))
        {
            return;
        }

        var now = _time.GetUtcNow();
        _log.Upsert(new LogEntry
        {
            Id = PendingId(itemId),
            At = now,
            UpdatedAt = now,
            EventKey = GroupKeys.For(item).EventKey,
            Title = TitleOfItems([item]),
            Result = LogResult.Waiting,
            Details = [_strings.Get("web.log.why.pending", ("minutes", timeoutMinutes))],
        });
    });

    /// <summary>
    /// Removes an item's waiting-for-details row once it is ready; its message has rows of its own from then on.
    /// </summary>
    /// <param name="itemId">The item.</param>
    public void PendingDone(Guid itemId) => Safe(() => _log.Remove([PendingId(itemId)]));

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
            telegram = NonEmpty(health.LastError);
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
    /// Records a test message, sent or not, as its own row (Test, or Test failed with the reason and Telegram's words).
    /// Tests never go through the Outbox, so this is their only record.
    /// </summary>
    /// <param name="destination">The destination.</param>
    /// <param name="eventKey">The sample's event key, or <c>test</c> for the plain test message.</param>
    /// <param name="sent">Whether it went out.</param>
    /// <param name="error">Why it didn't, when it didn't.</param>
    public void Test(Destination destination, string eventKey, bool sent, string? error) => Safe(() =>
    {
        ArgumentNullException.ThrowIfNull(destination);
        var now = _time.GetUtcNow();
        var reason = NonEmpty(error);
        _log.Upsert(new LogEntry
        {
            Id = "test|" + destination.Id + "|" + now.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            At = now,
            UpdatedAt = now,
            EventKey = eventKey,
            Title = _strings.Get("web.log.title.test"),
            DestinationId = destination.Id,
            DestinationTitle = destination.Title,
            Result = sent ? LogResult.TestSent : LogResult.TestFailed,
            Details = sent ? [] : [_strings.Get("web.log.why.testFailed", ("error", reason ?? _strings.Get("web.log.noDetails")))],
            Telegram = sent ? null : reason,
            Tries = 1,
            Advice = sent ? null : _strings.Get("web.log.advice.testFailed"),
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
        var now = _time.GetUtcNow();
        var why = Why(SkipReason.ItemGone, "media", null);
        var converted = false;
        _log.Update(PendingId(itemId), existing =>
        {
            converted = existing is not null;
            return existing is null ? null : existing with { Id = NewId("gone"), UpdatedAt = now, Result = LogResult.Skipped, Details = [why] };
        });
        if (!converted)
        {
            var title = _strings.Get("web.log.title.removedItem", ("id", itemId.ToString("N", CultureInfo.InvariantCulture)));
            Skip(NewId("gone"), "media", title, null, why);
        }
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
        _log.Update(RowIdOf(entry), existing =>
        {
            if (entry.Op == OutboxOp.Edit && existing is { } sent && IsSent(sent.Result))
            {
                // The update went through: an earlier failed update of it no longer matters.
                List<string> kept = [.. sent.Details.Where(d => !IsEditProblem(d))];
                var advice = kept.Count == 0 && sent.Result == LogResult.Sent ? null : sent.Advice;
                return sent with { Title = title, UpdatedAt = now, DestinationId = destination.Id, DestinationTitle = destination.Title, Details = kept, Advice = advice };
            }

            // The entry counts the retries it was scheduled for, even when this row missed them.
            var tries = Math.Max((existing?.Tries ?? 0) + 1, entry.Attempts + 1);
            var details = new List<(LogResult Result, string Why, string Advice)>();
            if (entry.Message.TemplateFallback is { } error)
            {
                details.Add((LogResult.SentWithBuiltIn, _strings.Get("web.log.why.builtIn", ("error", error)), _strings.Get("web.log.advice.builtIn")));
            }

            if (entry.Message.TopicFallback is { } slot)
            {
                details.Add((LogResult.Sent, _strings.Get("web.log.why.topicFallback", ("topic", SlotLabel(slot))), _strings.Get("web.log.advice.topicFallback", ("topic", SlotLabel(slot)), ("destination", destination.Title))));
            }

            if (noPhoto is not null)
            {
                details.Add((LogResult.SentWithoutPhoto, _strings.Get("web.log.why.noPhoto." + noPhoto), _strings.Get("web.log.advice.noPhoto." + noPhoto, ("destination", destination.Title))));
            }

            var delay = now - entry.QueuedAt;
            // late means a try failed or Telegram made it wait; time in the queue (pacing) doesn't count.
            var lastError = NonEmpty(existing?.Telegram);
            if (existing is not null && (existing.Result == LogResult.Failed || IsHeld(existing)))
            {
                var why = lastError is null
                    ? _strings.Get("web.log.why.lateRecoveredPlain", ("delay", Duration(delay)))
                    : _strings.Get("web.log.why.lateRecovered", ("delay", Duration(delay)), ("error", lastError));
                details.Add((LogResult.SentLate, why, _strings.Get("web.log.advice.late")));
            }
            else if (lastError is not null && IsRateLimited(existing!))
            {
                details.Add((LogResult.SentLate, _strings.Get("web.log.why.lateRateLimited", ("delay", Duration(delay)), ("error", lastError)), _strings.Get("web.log.advice.RateLimited")));
            }
            else if (lastError is not null)
            {
                details.Add((LogResult.SentLate, _strings.Get("web.log.why.lateRetried", ("delay", Duration(delay)), ("error", lastError), ("tries", tries)), _strings.Get("web.log.advice.late")));
            }
            else if (entry.Attempts > 0)
            {
                details.Add((LogResult.SentLate, _strings.Get("web.log.why.late", ("delay", Duration(delay)), ("tries", tries)), _strings.Get("web.log.advice.late")));
            }

            return new LogEntry
            {
                Id = RowIdOf(entry),
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
        _log.Update(RowIdOf(entry), existing => entry.Op == OutboxOp.Edit && existing is { } sent && IsSent(sent.Result)
            ? EditProblem(sent, _strings.Get("web.log.why.edit.retry", ("error", Err(telegram))), now)
            : (existing ?? NewRow(entry, destination, now)) with
        {
            UpdatedAt = now,
            DestinationId = destination.Id,
            DestinationTitle = destination.Title,
            Result = LogResult.Waiting,
            Details = [_strings.Get("web.log.why.retry." + kind)],
            Telegram = NonEmpty(telegram),

            // Telegram asking to slow down is a wait, not a try (the Outbox doesn't count it either).
            Tries = (existing?.Tries ?? 0) + (kind == DeliveryErrorKind.RateLimited ? 0 : 1),
            RetryAt = retryAt,
            HeldUntil = null,
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
    /// <param name="fromTelegram">Whether Telegram answered with the error; <c>false</c> when TgJelly or the server failed.</param>
    /// <param name="heldUntil">When the message stays queued, held until its destination works again: when the Outbox drops it.</param>
    public void Failed(OutboxEntry entry, Destination destination, DeliveryError error, string telegram, bool afterFallbacks = false, bool fromTelegram = true, DateTimeOffset? heldUntil = null) => Safe(() =>
    {
        var now = _time.GetUtcNow();
        var (why, advice) = error.Kind switch
        {
            DeliveryErrorKind.Lost or DeliveryErrorKind.NeedsPermissions or DeliveryErrorKind.TopicMissing or DeliveryErrorKind.Migrated when !afterFallbacks
                => ("web.log.why.failed." + error.Kind, "web.log.advice." + error.Kind),
            _ when !fromTelegram => ("web.log.why.failed.ours", "web.log.advice.ours"),
            _ => (afterFallbacks ? "web.log.why.failed.fallbacks" : "web.log.why.failed.Other", "web.log.advice.Other"),
        };
        var args = Args(destination, Err(error.Description));

        // A refused update leaves the message in the chat as it was: the row keeps its result and says so.
        _log.Update(RowIdOf(entry), existing => entry.Op == OutboxOp.Edit && existing is { } sent && IsSent(sent.Result)
            ? EditProblem(sent, _strings.Get("web.log.why.edit.failed", ("error", Err(error.Description))), now)
            : (existing ?? NewRow(entry, destination, now)) with
        {
            UpdatedAt = now,
            DestinationId = destination.Id,
            DestinationTitle = destination.Title,
            Result = LogResult.Failed,
            Details = [_strings.Get(why, args)],
            Telegram = NonEmpty(telegram),
            Tries = (existing?.Tries ?? 0) + 1,
            RetryAt = null,
            HeldUntil = heldUntil,
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
        var why = _strings.Get("web.log.why.failed.unaccounted");
        var advice = _strings.Get("web.log.advice.unaccounted");
        _log.UpdateWhere(
            r => r.Result == LogResult.Waiting && now - r.At > StaleAfter,
            r => r with { Result = LogResult.Failed, UpdatedAt = now, Details = [why], RetryAt = null, Advice = advice });
    });

    private static bool IsSent(LogResult result)
        => result is LogResult.Sent or LogResult.SentLate or LogResult.SentWithBuiltIn or LogResult.SentWithoutPhoto;

    private static bool IsFinal(LogResult result) => IsSent(result) || result is LogResult.Failed or LogResult.Skipped;

    // Final and no longer queued: a Failed row held until its destination works again is still queued.
    private static bool IsSettled(LogEntry row) => IsFinal(row.Result) && row.HeldUntil is null;

    private static string PendingId(Guid itemId) => "pending|" + itemId.ToString("N", CultureInfo.InvariantCulture);

    private static string? NonEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

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

    // The text before the first placeholder of each detail kind, to find those details again.
    private bool StartsLike(string detail, params string[] keys)
        => keys
            .Select(key => _strings.Get(key))
            .Select(text => text.IndexOf('{', StringComparison.Ordinal) is var at and > 0 ? text[..at] : text)
            .Any(prefix => detail.StartsWith(prefix, StringComparison.Ordinal));

    private bool IsEditProblem(string detail) => StartsLike(detail, "web.log.why.edit.retry", "web.log.why.edit.failed");

    // A row waiting because Telegram asked TgJelly to slow down (429).
    private bool IsRateLimited(LogEntry row)
        => row.Result == LogResult.Waiting && row.Details.Count == 1 && StartsLike(row.Details[0], "web.log.why.retry.RateLimited");

    // A row waiting because its destination had a problem (Lost and the like).
    private bool IsHeld(LogEntry row)
        => row.Result == LogResult.Waiting && row.Details.Count == 1
            && StartsLike(row.Details[0], "web.log.why.held.Lost", "web.log.why.held.NeedsPermissions", "web.log.why.held.TopicMissing");

    // Telegram's or the network's words for an {error} placeholder; never empty.
    private string Err(string? text) => NonEmpty(text) ?? _strings.Get("web.log.noDetails");

    private LogEntry EditProblem(LogEntry sent, string detail, DateTimeOffset now) => sent with
    {
        UpdatedAt = now,
        Details = [detail, .. sent.Details.Where(d => !IsEditProblem(d))],
        Advice = sent.Advice ?? _strings.Get("web.log.advice.edit"),
    };

    // plainAdvice, when given, is for a row without Telegram's answer (advice may point at that answer).
    private void Discard(IReadOnlyList<string> logIds, string why, string advice, string? plainAdvice = null) => Safe(() =>
    {
        var now = _time.GetUtcNow();
        string AdviceFor(LogEntry row) => NonEmpty(row.Telegram) is null ? plainAdvice ?? advice : advice;
        foreach (var id in logIds.Distinct(StringComparer.Ordinal))
        {
            _log.Update(id, existing => existing switch
            {
                null or { Result: LogResult.Skipped } => null,
                { } sent when IsSent(sent.Result) => EditProblem(sent, _strings.Get("web.log.why.edit.failed", ("error", why)), now),

                // A held row was still queued: it is dropped now, so its advice (it goes out once fixed) no longer holds.
                { Result: LogResult.Failed } failed => failed with { UpdatedAt = now, Details = [why, .. failed.Details.Where(d => d != why)], HeldUntil = null, Advice = AdviceFor(failed) },
                { } waiting => waiting with { Result = LogResult.Failed, UpdatedAt = now, Details = [why], RetryAt = null, HeldUntil = null, Advice = AdviceFor(waiting) },
            });
        }
    });

    // Messages already queued for a destination that just turned Lost (or similar) are held: say so on their rows.
    private void OnProblemFound(object? sender, string destinationId) => Safe(() =>
    {
        if (_health is null || _settings?.Current.Destinations.FirstOrDefault(d => d.Id == destinationId) is not { } destination)
        {
            return;
        }

        var health = _health.Get(destinationId);
        if (health.Status is not (HealthStatus.Lost or HealthStatus.NeedsPermissions or HealthStatus.TopicMissing))
        {
            return;
        }

        var now = _time.GetUtcNow();
        var queued = _strings.Get("web.log.why.queued");
        var args = Args(destination);
        var why = _strings.Get("web.log.why.held." + health.Status, args);
        var advice = _strings.Get("web.log.advice." + health.Status, args);
        _log.UpdateWhere(
            r => r.DestinationId == destinationId && r.Result == LogResult.Waiting && r.Details.Count == 1 && r.Details[0] == queued,
            r => r with { UpdatedAt = now, Details = [why], Advice = advice, Telegram = NonEmpty(health.LastError) });
    });

    private (string Name, object? Value)[] Args(Destination destination, string? error = null)
    {
        var right = _strings.Get(destination.Kind == ChatKind.Channel ? "web.log.right.post" : "web.log.right.send");
        return [("destination", destination.Title), ("right", right), ("error", error)];
    }

    private string EventLabel(string eventKey) => _strings.Get("web.event." + eventKey);

    private string SlotLabel(string slot) => _strings.Get(slot.Contains('.', StringComparison.Ordinal) ? "web.event." + slot : "web.event.group." + slot);

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
        Id = RowIdOf(entry),
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
