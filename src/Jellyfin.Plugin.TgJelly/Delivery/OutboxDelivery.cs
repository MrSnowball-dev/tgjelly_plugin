using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.Log;
using Jellyfin.Plugin.TgJelly.Logging;
using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Templates;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Extensions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Jellyfin.Plugin.TgJelly.Delivery;

/// <summary>
/// Delivers one Outbox entry, applying every fallback for a failed send and recording the outcome in the Outbox, the
/// destination's health and <c>status</c>. Never throws except <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class OutboxDelivery
{
    /// <summary>A send that leaves the Outbox later than this after it was queued gets the ⏱ line.</summary>
    public static readonly TimeSpan DelayedAfter = TimeSpan.FromMinutes(5);

    private const int MaxResends = 4;
    private const int CaptionLimit = 1024;
    private const int TextLimit = 4096;

    private readonly BotConnection _connection;
    private readonly Outbox _outbox;
    private readonly HealthService _health;
    private readonly SettingsStore _settings;
    private readonly StateStore _state;
    private readonly PacingGate _pacing;
    private readonly Formatter _formatter;
    private readonly TimeProvider _time;
    private readonly DestinationMigration _migration;
    private readonly TemplateFlags _templateFlags;
    private readonly ILogger<OutboxDelivery> _logger;
    private readonly LogRecorder? _log;

    /// <summary>
    /// Initializes a new instance of the <see cref="OutboxDelivery"/> class.
    /// </summary>
    /// <param name="connection">The bot connection.</param>
    /// <param name="outbox">The Outbox.</param>
    /// <param name="health">The health service.</param>
    /// <param name="settings">The settings, for migrations and the public URL.</param>
    /// <param name="state">The state store, for errors and flags.</param>
    /// <param name="pacing">The pacing gate.</param>
    /// <param name="formatter">The formatter, for the ⏱ line.</param>
    /// <param name="time">The clock.</param>
    /// <param name="migration">The chat migration.</param>
    /// <param name="templateFlags">The template flags.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="log">Where each delivery's outcome is recorded for the Log, if anywhere.</param>
    public OutboxDelivery(BotConnection connection, Outbox outbox, HealthService health, SettingsStore settings, StateStore state, PacingGate pacing, Formatter formatter, TimeProvider time, DestinationMigration migration, TemplateFlags templateFlags, ILogger<OutboxDelivery> logger, LogRecorder? log = null)
    {
        _log = log;
        _templateFlags = templateFlags;
        _migration = migration;
        _connection = connection;
        _outbox = outbox;
        _health = health;
        _settings = settings;
        _state = state;
        _pacing = pacing;
        _formatter = formatter;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Gets or sets the random source for retry jitter (replaced in tests).
    /// </summary>
    internal Func<double> Random { get; set; } = System.Random.Shared.NextDouble;

    /// <summary>
    /// Delivers an entry once, with immediate re-sends after fallbacks.
    /// </summary>
    /// <param name="entry">A copy of the queued entry.</param>
    /// <param name="destination">Its destination.</param>
    /// <param name="ct">Cancels the delivery.</param>
    /// <returns>A task.</returns>
    public async Task DeliverAsync(OutboxEntry entry, Destination destination, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(destination);
        if (_connection.Sender is not { } sender)
        {
            return;
        }

        DeliveryError? last = null;
        var lastTelegram = string.Empty;
        var lastFromTelegram = false;
        for (var hop = 0; hop <= MaxResends; hop++)
        {
            var attempt = Prepare(entry, destination);
            try
            {
                var sent = await SendAsync(sender, entry, destination, attempt, ct).ConfigureAwait(false);
                OnSuccess(entry, destination, attempt, sent);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastTelegram = TelegramText(ex);
                lastFromTelegram = ex is ApiRequestException;
                last = TelegramErrorClassifier.ClassifyDelivery(ex);
                var resend = OnFailure(entry, destination, attempt, ex, last, lastTelegram);
                if (resend is null)
                {
                    return;
                }

                (entry, destination) = resend.Value;
                _outbox.Replace(entry);
            }
        }

        Drop(entry, destination, "too many fallbacks", last ?? new DeliveryError(DeliveryErrorKind.Other, "too many fallbacks"), lastTelegram, afterFallbacks: true, fromTelegram: lastFromTelegram);
    }

    /// <summary>
    /// Returns what Telegram or the network said, for the Log: Telegram's error code and description, or every
    /// message of a network failure (the outer one alone is often just "Exception during making request"). Redacted.
    /// </summary>
    /// <param name="exception">The failure.</param>
    /// <returns>The text.</returns>
    internal static string TelegramText(Exception exception)
    {
        if (exception is ApiRequestException api)
        {
            return SecretRedactor.Redact(api.ErrorCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + api.Message);
        }

        var messages = new System.Collections.Generic.List<string>();
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(e.Message) && !messages.Contains(e.Message, StringComparer.Ordinal))
            {
                messages.Add(e.Message);
            }
        }

        return SecretRedactor.Redact(string.Join(": ", messages));
    }

    private static InlineKeyboardMarkup? Markup(OutboxMessage message)
        => message.NoButtons || message.Buttons.Count == 0
            ? null
            : new InlineKeyboardMarkup(message.Buttons.Select(row => row.Select(b => InlineKeyboardButton.WithUrl(b.Text, b.Url))));

    private static string Body(OutboxMessage message, string html) => message.PlainText ? HtmlText.ToPlain(html) : html;

    private Attempt Prepare(OutboxEntry entry, Destination destination)
    {
        // Every message whose slot's topic is known to be gone says so in the Log, not only the first one.
        if (destination.ThreadFor(entry.EventKey) != ThreadOf(entry, destination))
        {
            entry.Message.TopicFallback = destination.RouteSlotFor(entry.EventKey);
        }

        SentMessage? target = null;
        if (entry.Op == OutboxOp.Edit && (entry.TargetKey is null || !_outbox.TryGetSent(destination.Id, entry.TargetKey, out target)))
        {
            // The message to edit isn't known (a different bot, an evicted record): send it as a new one.
            entry.Op = OutboxOp.Send;
            entry.SentKey ??= entry.TargetKey;
        }

        int? replyTo = entry.ReplyToKey is { } key && _outbox.TryGetSent(destination.Id, key, out var replied) ? replied.MessageId : null;
        if (target is not null)
        {
            return new Attempt(target, replyTo, target.Kind == SentKind.Photo, null, null);
        }

        var message = entry.Message;
        if (message.ForceTextOnly || message.PosterPath is null || _health.Get(destination.Id).PhotosBlocked)
        {
            return new Attempt(null, replyTo, false, null, null);
        }

        if (message.PosterCacheKey is { } cacheKey && _outbox.TryGetFileId(cacheKey, _time.GetUtcNow(), out var fileId))
        {
            return new Attempt(null, replyTo, true, fileId, null);
        }

        return File.Exists(message.PosterPath)
            ? new Attempt(null, replyTo, true, null, message.PosterPath)
            : new Attempt(null, replyTo, false, null, null);
    }

    private async Task<Message?> SendAsync(ITelegramSender sender, OutboxEntry entry, Destination destination, Attempt attempt, CancellationToken ct)
    {
        var message = entry.Message;
        var parseMode = message.PlainText ? ParseMode.None : ParseMode.Html;
        var buttons = Markup(message);
        var now = _time.GetUtcNow();
        var delayed = attempt.EditTarget is null && now - entry.QueuedAt > DelayedAfter ? "\n\n" + _formatter.DelayedLine(entry.QueuedAt, now) : string.Empty;
        if (attempt.EditTarget is { } target)
        {
            var edit = new MessageEdit(destination.ChatId, target.MessageId, Body(message, attempt.Photo ? message.Caption : message.Text), parseMode, buttons);
            if (attempt.Photo)
            {
                await sender.EditCaptionAsync(edit, ct).ConfigureAwait(false);
            }
            else
            {
                await sender.EditTextAsync(edit, ct).ConfigureAwait(false);
            }

            return null;
        }

        if (!attempt.Photo)
        {
            return await sender.SendTextAsync(
                new OutgoingText(destination.ChatId, ThreadOf(entry, destination), Body(message, message.Text + delayed), parseMode, buttons, attempt.ReplyTo, message.Silent),
                ct).ConfigureAwait(false);
        }

        if (attempt.FileId is { } fileId)
        {
            return await sender.SendPhotoAsync(
                new OutgoingPhoto(destination.ChatId, ThreadOf(entry, destination), InputFile.FromFileId(fileId), Body(message, message.Caption + delayed), parseMode, buttons, attempt.ReplyTo, message.Silent),
                ct).ConfigureAwait(false);
        }

        var stream = File.OpenRead(attempt.UploadPath!);
        await using (stream.ConfigureAwait(false))
        {
            var photo = InputFile.FromStream(stream, "poster" + Path.GetExtension(attempt.UploadPath));
            return await sender.SendPhotoAsync(
                new OutgoingPhoto(destination.ChatId, ThreadOf(entry, destination), photo, Body(message, message.Caption + delayed), parseMode, buttons, attempt.ReplyTo, message.Silent),
                ct).ConfigureAwait(false);
        }
    }

    private void OnSuccess(OutboxEntry entry, Destination destination, Attempt attempt, Message? sent)
    {
        var now = _time.GetUtcNow();
        _pacing.RecordSend(destination.ChatId, now);
        try
        {
            TopicNames.LearnFrom(_settings, destination.Id, sent);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A name is a nicety: never let it disturb a delivered message.
            _logger.LogWarning(ex, "TgJelly could not remember a topic's name");
        }

        var logId = entry.LogId.Length > 0 ? entry.LogId : entry.EntryId;
        if (sent is null)
        {
            _outbox.Complete(entry, null);
        }
        else
        {
            _outbox.Complete(entry, new SentMessage { MessageId = sent.Id, Kind = attempt.Photo ? SentKind.Photo : SentKind.Text, SentAt = now, EventKey = entry.EventKey, Model = entry.Model, LogId = logId });
            if (attempt.UploadPath is not null && entry.Message.PosterCacheKey is { } cacheKey && sent.Photo is { Length: > 0 } sizes)
            {
                _outbox.CacheFileId(cacheKey, sizes[^1].FileId, now);
            }
        }

        if (Markup(entry.Message) is not null && _state.Read(s => s.Status.PublicUrlFlag is not null))
        {
            _state.Update(s => s.Status.PublicUrlFlag = null);
        }

        if (_log is not null)
        {
            try
            {
                // A poster left out because this chat blocks photos may have been decided by an earlier message (Prepare).
                var noPhoto = entry.Message.NoPhoto
                    ?? (attempt.EditTarget is null && !attempt.Photo && entry.Message.PosterPath is not null && _health.Get(destination.Id).PhotosBlocked ? "blocked" : null);
                _log.Delivered(entry, destination, noPhoto);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "TgJelly could not record a Log row; sending is not affected");
            }
        }
    }

    // Returns the changed entry (and destination) to re-send now, or null when the outcome is recorded.
    private (OutboxEntry Entry, Destination Destination)? OnFailure(OutboxEntry entry, Destination destination, Attempt attempt, Exception exception, DeliveryError error, string telegram)
    {
        var message = entry.Message;
        if (exception is IOException or UnauthorizedAccessException && attempt.UploadPath is not null)
        {
            // The poster file couldn't be opened or read; Telegram's own transport errors arrive as RequestException.
            message.ForceTextOnly = true;
            message.NoPhoto = "unreadable";
            return (entry, destination);
        }

        var now = _time.GetUtcNow();
        switch (error.Kind)
        {
            case DeliveryErrorKind.PhotosBlocked when attempt.Photo && attempt.EditTarget is null:
                _health.SetPhotosBlocked(destination.Id);
                message.ForceTextOnly = true;
                message.NoPhoto = "blocked";
                return (entry, destination);
            case DeliveryErrorKind.Lost:
                // The entry stays queued and goes out if the destination recovers (Check again) before the Outbox drops it;
                // until then it failed, held.
                _health.MarkProblem(destination.Id, HealthStatus.Lost, error.Description);
                _log?.Failed(entry, destination, error, telegram, heldUntil: entry.QueuedAt + Outbox.Ttl);
                return null;
            case DeliveryErrorKind.NeedsPermissions:
                _health.MarkProblem(destination.Id, HealthStatus.NeedsPermissions, error.Description);
                _log?.Failed(entry, destination, error, telegram, heldUntil: entry.QueuedAt + Outbox.Ttl);
                return null;
            case DeliveryErrorKind.TopicMissing when ThreadOf(entry, destination) != destination.ThreadId:
                // A routed topic is gone: remember it, and send this one (and its slot's next ones) to the destination's
                // own topic instead of holding everything.
                _health.SetTopicMissing(destination.Id, ThreadOf(entry, destination));
                message.TopicFallback = destination.RouteSlotFor(entry.EventKey);
                return (entry, destination);
            case DeliveryErrorKind.TopicMissing:
                _health.MarkProblem(destination.Id, HealthStatus.TopicMissing, error.Description);
                _log?.Failed(entry, destination, error, telegram, heldUntil: entry.QueuedAt + Outbox.Ttl);
                return null;
            case DeliveryErrorKind.Migrated when !entry.Migrated && error.MigrateToChatId is { } newChatId:
                var map = _migration.MigrateAll(destination.ChatId, newChatId);
                var migrated = map.TryGetValue(destination.Id, out var nowId) ? _settings.Current.Destinations.FirstOrDefault(x => x.Id == nowId) : null;
                if (migrated is null)
                {
                    break;
                }

                entry.Migrated = true;
                entry.DestinationId = migrated.Id;
                return (entry, migrated);
            case DeliveryErrorKind.ParseError when !message.PlainText:
                if (message.TemplateKey is { } templateKey
                    && _settings.Current.Templates.FirstOrDefault(t => t.EventKey == templateKey) is { } template)
                {
                    _templateFlags.Flag(templateKey, "error", error.Description, TemplateText.Hash(template.Text));
                }

                message.PlainText = true;
                return (entry, destination);
            case DeliveryErrorKind.BadButton when !message.NoButtons:
                message.NoButtons = true;
                var url = _settings.Current.PublicUrl;
                _state.Update(s => s.Status.PublicUrlFlag = new PublicUrlFlag { Url = url, At = now });
                return (entry, destination);
            case DeliveryErrorKind.BadPhoto when attempt.Photo && attempt.EditTarget is null:
                message.ForceTextOnly = true;
                message.NoPhoto = "rejected";
                return (entry, destination);
            case DeliveryErrorKind.TooLong when !message.Truncated:
                message.Truncated = true;
                if (attempt.Photo)
                {
                    message.Caption = HtmlText.Truncate(message.Caption, CaptionLimit * 9 / 10);
                }
                else
                {
                    message.Text = HtmlText.Truncate(message.Text, TextLimit * 9 / 10);
                }

                return (entry, destination);
            case DeliveryErrorKind.EditTargetGone when attempt.EditTarget is not null:
                entry.Op = OutboxOp.Send;
                entry.SentKey ??= entry.TargetKey;
                if (entry.TargetKey is not null)
                {
                    // Prepare must not find the dead message again.
                    entry.TargetKey = null;
                }

                return (entry, destination);
            case DeliveryErrorKind.NotModified:
                _pacing.RecordSend(destination.ChatId, now);
                _outbox.Complete(entry, null);
                _log?.Delivered(entry, destination, message.NoPhoto);
                return null;
            case DeliveryErrorKind.RateLimited:
                var until = now + (error.RetryAfter ?? TimeSpan.FromSeconds(5));
                _pacing.Pause(destination.ChatId, until);
                _outbox.Retry(entry.EntryId, until, countAttempt: false);
                _log?.Retrying(entry, destination, error.Kind, telegram, until);
                return null;
            case DeliveryErrorKind.Transport:
            case DeliveryErrorKind.ServerError:
            case DeliveryErrorKind.Unauthorized:
                if (error.Kind == DeliveryErrorKind.Transport && attempt.UploadPath is not null && ++entry.PhotoFailures >= 2)
                {
                    message.ForceTextOnly = true;
                    message.NoPhoto = "upload";
                }

                _outbox.Replace(entry);
                var retryAt = now + Outbox.Backoff(entry.Attempts + 1, Random());
                _outbox.Retry(entry.EntryId, retryAt, countAttempt: true);
                _logger.LogInformation("TgJelly will retry a message to {Destination}: {Error}", destination.Title, error.Description);
                _log?.Retrying(entry, destination, error.Kind, telegram, retryAt);
                return null;
        }

        Drop(entry, destination, error.Description, error, telegram, fromTelegram: exception is ApiRequestException);
        return null;
    }

    private void Drop(OutboxEntry entry, Destination destination, string reason, DeliveryError error, string telegram, bool afterFallbacks = false, bool fromTelegram = true)
    {
        _logger.LogWarning("TgJelly dropped a message to {Destination}: {Reason}", destination.Title, reason);
        _outbox.Drop(entry.EntryId);
        // The ID, not the title: last errors outlive the destination, and diagnostics must never show its title.
        _state.RecordError("outbox", DestinationRef.Of(destination) + ": " + reason);
        _log?.Failed(entry, destination, error, telegram, afterFallbacks, fromTelegram);
    }

    /// <summary>
    /// The topic a message goes to: its slot's routed topic, unless that one is known to be gone, then the
    /// destination's own topic.
    /// </summary>
    private int ThreadOf(OutboxEntry entry, Destination destination)
    {
        var thread = destination.ThreadFor(entry.EventKey);
        return thread != destination.ThreadId && _health.IsTopicMissing(destination.Id, thread) ? destination.ThreadId : thread;
    }

    private sealed record Attempt(SentMessage? EditTarget, int? ReplyTo, bool Photo, string? FileId, string? UploadPath);
}
