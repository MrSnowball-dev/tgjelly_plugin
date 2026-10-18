using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Extensions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Jellyfin.Plugin.TgJelly.Delivery;

/// <summary>
/// Delivers one Outbox entry, applying every fallback of spec §9.4 and recording the outcome in the Outbox, the
/// destination's health and <c>status</c>. Never throws except <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class OutboxDelivery
{
    private const int MaxResends = 4;
    private const int CaptionLimit = 1024;
    private const int TextLimit = 4096;

    private readonly BotConnection _connection;
    private readonly Outbox _outbox;
    private readonly HealthService _health;
    private readonly SettingsStore _settings;
    private readonly StateStore _state;
    private readonly PacingGate _pacing;
    private readonly TimeProvider _time;
    private readonly ILogger<OutboxDelivery> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OutboxDelivery"/> class.
    /// </summary>
    /// <param name="connection">The bot connection.</param>
    /// <param name="outbox">The Outbox.</param>
    /// <param name="health">The health service.</param>
    /// <param name="settings">The settings, for migrations and the public URL.</param>
    /// <param name="state">The state store, for errors and flags.</param>
    /// <param name="pacing">The pacing gate.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public OutboxDelivery(BotConnection connection, Outbox outbox, HealthService health, SettingsStore settings, StateStore state, PacingGate pacing, TimeProvider time, ILogger<OutboxDelivery> logger)
    {
        _connection = connection;
        _outbox = outbox;
        _health = health;
        _settings = settings;
        _state = state;
        _pacing = pacing;
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
                var resend = OnFailure(entry, destination, attempt, ex);
                if (resend is null)
                {
                    return;
                }

                (entry, destination) = resend.Value;
                _outbox.Replace(entry);
            }
        }

        Drop(entry, destination, "too many fallbacks");
    }

    private static InlineKeyboardMarkup? Markup(OutboxMessage message)
        => message.NoButtons || message.Buttons.Count == 0
            ? null
            : new InlineKeyboardMarkup(message.Buttons.Select(row => row.Select(b => InlineKeyboardButton.WithUrl(b.Text, b.Url))));

    private static string Body(OutboxMessage message, string html) => message.PlainText ? HtmlText.ToPlain(html) : html;

    private Attempt Prepare(OutboxEntry entry, Destination destination)
    {
        SentMessage? target = null;
        if (entry.Op == OutboxOp.Edit && (entry.TargetKey is null || !_outbox.TryGetSent(destination.Id, entry.TargetKey, out target)))
        {
            // The message to edit isn't known (a different bot, an evicted record): send it as a new one (§9.3).
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
                new OutgoingText(destination.ChatId, destination.ThreadId, Body(message, message.Text), parseMode, buttons, attempt.ReplyTo, message.Silent),
                ct).ConfigureAwait(false);
        }

        if (attempt.FileId is { } fileId)
        {
            return await sender.SendPhotoAsync(
                new OutgoingPhoto(destination.ChatId, destination.ThreadId, InputFile.FromFileId(fileId), Body(message, message.Caption), parseMode, buttons, attempt.ReplyTo, message.Silent),
                ct).ConfigureAwait(false);
        }

        var stream = File.OpenRead(attempt.UploadPath!);
        await using (stream.ConfigureAwait(false))
        {
            var photo = InputFile.FromStream(stream, "poster" + Path.GetExtension(attempt.UploadPath));
            return await sender.SendPhotoAsync(
                new OutgoingPhoto(destination.ChatId, destination.ThreadId, photo, Body(message, message.Caption), parseMode, buttons, attempt.ReplyTo, message.Silent),
                ct).ConfigureAwait(false);
        }
    }

    private void OnSuccess(OutboxEntry entry, Destination destination, Attempt attempt, Message? sent)
    {
        var now = _time.GetUtcNow();
        _pacing.RecordSend(destination.ChatId, now);
        if (sent is null)
        {
            _outbox.Complete(entry, null);
        }
        else
        {
            _outbox.Complete(entry, new SentMessage { MessageId = sent.Id, Kind = attempt.Photo ? SentKind.Photo : SentKind.Text, SentAt = now, EventKey = entry.EventKey });
            if (attempt.UploadPath is not null && entry.Message.PosterCacheKey is { } cacheKey && sent.Photo is { Length: > 0 } sizes)
            {
                _outbox.CacheFileId(cacheKey, sizes[^1].FileId, now);
            }
        }

        if (Markup(entry.Message) is not null && _state.Read(s => s.Status.PublicUrlFlag is not null))
        {
            _state.Update(s => s.Status.PublicUrlFlag = null);
        }
    }

    // Returns the changed entry (and destination) to re-send now, or null when the outcome is recorded.
    private (OutboxEntry Entry, Destination Destination)? OnFailure(OutboxEntry entry, Destination destination, Attempt attempt, Exception exception)
    {
        var error = TelegramErrorClassifier.ClassifyDelivery(exception);
        var message = entry.Message;
        var now = _time.GetUtcNow();
        switch (error.Kind)
        {
            case DeliveryErrorKind.PhotosBlocked when attempt.Photo && attempt.EditTarget is null:
                _health.SetPhotosBlocked(destination.Id);
                message.ForceTextOnly = true;
                return (entry, destination);
            case DeliveryErrorKind.Lost:
                _health.MarkProblem(destination.Id, HealthStatus.Lost, error.Description);
                return null;
            case DeliveryErrorKind.NeedsPermissions:
                _health.MarkProblem(destination.Id, HealthStatus.NeedsPermissions, error.Description);
                return null;
            case DeliveryErrorKind.TopicMissing:
                _health.MarkProblem(destination.Id, HealthStatus.TopicMissing, error.Description);
                return null;
            case DeliveryErrorKind.Migrated when !entry.Migrated && error.MigrateToChatId is { } newChatId:
                var migrated = Migrate(destination, newChatId);
                if (migrated is null)
                {
                    break;
                }

                entry.Migrated = true;
                entry.DestinationId = migrated.Id;
                return (entry, migrated);
            case DeliveryErrorKind.ParseError when !message.PlainText:
                message.PlainText = true;
                return (entry, destination);
            case DeliveryErrorKind.BadButton when !message.NoButtons:
                message.NoButtons = true;
                var url = _settings.Current.PublicUrl;
                _state.Update(s => s.Status.PublicUrlFlag = new PublicUrlFlag { Url = url, At = now });
                return (entry, destination);
            case DeliveryErrorKind.BadPhoto when attempt.Photo && attempt.EditTarget is null:
                message.ForceTextOnly = true;
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
                return null;
            case DeliveryErrorKind.RateLimited:
                var until = now + (error.RetryAfter ?? TimeSpan.FromSeconds(5));
                _pacing.Pause(destination.ChatId, until);
                _outbox.Retry(entry.EntryId, until, countAttempt: false);
                return null;
            case DeliveryErrorKind.Transport:
            case DeliveryErrorKind.ServerError:
                if (error.Kind == DeliveryErrorKind.Transport && attempt.UploadPath is not null && ++entry.PhotoFailures >= 2)
                {
                    message.ForceTextOnly = true;
                }

                _outbox.Replace(entry);
                _outbox.Retry(entry.EntryId, now + Outbox.Backoff(entry.Attempts + 1, Random()), countAttempt: true);
                _logger.LogInformation("TgJelly will retry a message to {Destination}: {Error}", destination.Title, error.Description);
                return null;
        }

        Drop(entry, destination, error.Description);
        return null;
    }

    private void Drop(OutboxEntry entry, Destination destination, string reason)
    {
        _logger.LogWarning("TgJelly dropped a message to {Destination}: {Reason}", destination.Title, reason);
        _outbox.Drop(entry.EntryId);
        _state.RecordError("outbox", destination.Title + ": " + reason);
    }

    // Spec §9.4 row 5: rewrite the chat ID, or merge into the destination that already has the new chat.
    private Destination? Migrate(Destination destination, long newChatId)
    {
        string? mergedInto = null;
        _settings.Update(null, c =>
        {
            var d = c.Destinations.FirstOrDefault(x => x.Id == destination.Id);
            if (d is null)
            {
                return;
            }

            var other = c.Destinations.FirstOrDefault(x => x.Id != d.Id && x.ChatId == newChatId && x.ThreadId == d.ThreadId);
            if (other is null)
            {
                d.ChatId = newChatId;
                d.Kind = ChatKind.Supergroup;
                return;
            }

            other.Events = [.. other.Events.Union(d.Events, StringComparer.Ordinal)];
            other.SilentGroups = [.. other.SilentGroups.Union(d.SilentGroups, StringComparer.Ordinal)];
            other.LibraryIds = other.LibraryIds.Length == 0 || d.LibraryIds.Length == 0 ? [] : [.. other.LibraryIds.Union(d.LibraryIds, StringComparer.Ordinal)];
            if (other.Preset != d.Preset)
            {
                other.Preset = PresetKind.Custom;
            }

            c.Destinations = [.. c.Destinations.Where(x => x.Id != d.Id)];
            mergedInto = other.Id;
        });

        if (mergedInto is not null)
        {
            _outbox.Repoint(destination.Id, mergedInto);
            _health.Remove(destination.Id);
        }

        var id = mergedInto ?? destination.Id;
        return _settings.Current.Destinations.FirstOrDefault(x => x.Id == id);
    }

    private sealed record Attempt(SentMessage? EditTarget, int? ReplyTo, bool Photo, string? FileId, string? UploadPath);
}
