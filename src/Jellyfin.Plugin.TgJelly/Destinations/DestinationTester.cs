using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Log;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Strings;
using Jellyfin.Plugin.TgJelly.Templates;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Extensions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Jellyfin.Plugin.TgJelly.Destinations;

/// <summary>
/// Sends the test message ("✅ TgJelly test: this chat will receive …"), after a re-check that bypasses the hold.
/// Goes straight to Telegram, not through the Outbox, so the caller gets the result.
/// </summary>
public sealed class DestinationTester
{
    private const string PosterResource = "Jellyfin.Plugin.TgJelly.Templates.sample-poster.png";
    private const string LogoResource = "Jellyfin.Plugin.TgJelly.Logo.png";

    private readonly BotConnection _connection;
    private readonly HealthService _health;
    private readonly StringTable _strings;
    private readonly ILogger<DestinationTester> _logger;
    private readonly LogRecorder? _log;
    private readonly SettingsStore? _settings;

    /// <summary>
    /// Initializes a new instance of the <see cref="DestinationTester"/> class.
    /// </summary>
    /// <param name="connection">The bot connection.</param>
    /// <param name="health">The health service.</param>
    /// <param name="strings">The string table.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="log">The Log, which records every test message.</param>
    /// <param name="settings">The settings, to remember the name of a topic a test went to.</param>
    public DestinationTester(BotConnection connection, HealthService health, StringTable strings, ILogger<DestinationTester> logger, LogRecorder? log = null, SettingsStore? settings = null)
    {
        _settings = settings;
        _connection = connection;
        _health = health;
        _strings = strings;
        _logger = logger;
        _log = log;
    }

    /// <summary>
    /// Re-checks a destination and, unless it's unhealthy, sends the test message.
    /// </summary>
    /// <param name="destination">The destination.</param>
    /// <param name="ct">Cancels the send.</param>
    /// <returns>The result.</returns>
    public Task<TestSendResult> SendTestAsync(Destination destination, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return LoggedAsync(destination, "test", CheckAndSendAsync(destination, async (sender, token) => await sender.SendTextAsync(new OutgoingText(destination.ChatId, destination.ThreadId, Message(destination), ParseMode.Html, null, null, false), token).ConfigureAwait(false), ct));
    }

    /// <summary>
    /// Re-checks a destination and, unless it's unhealthy, sends a sample message: the poster with the caption, or the
    /// text alone when the sample has no poster or the destination blocks photos.
    /// </summary>
    /// <param name="destination">The destination.</param>
    /// <param name="sample">The sample message.</param>
    /// <param name="ct">Cancels the send.</param>
    /// <returns>The result.</returns>
    public Task<TestSendResult> SendSampleAsync(Destination destination, SampleMessage sample, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(sample);
        var group = EventCatalog.GroupOf(sample.EventKey);
        var silent = destination.SilentGroups.Contains(group, StringComparer.Ordinal);
        var buttons = sample.Buttons.Count == 0
            ? null
            : new InlineKeyboardMarkup(sample.Buttons.Select(row => row.Select(b => InlineKeyboardButton.WithUrl(b.Text, b.Url))));
        // A sample goes where that message would go: its slot's topic in a forum.
        var thread = destination.ThreadFor(sample.EventKey);
        Task<Message> SendText(ITelegramSender sender, CancellationToken token)
            => sender.SendTextAsync(new OutgoingText(destination.ChatId, thread, sample.TextHtml ?? sample.Html ?? string.Empty, ParseMode.Html, buttons, null, silent), token);

        async Task<Message?> Send(ITelegramSender sender, CancellationToken token)
        {
            if (!sample.Poster || _health.Get(destination.Id).PhotosBlocked)
            {
                return await SendText(sender, token).ConfigureAwait(false);
            }

            try
            {
                var stream = typeof(DestinationTester).Assembly.GetManifestResourceStream(group == "media" ? PosterResource : LogoResource)
                    ?? throw new InvalidOperationException("The sample poster is missing from the plugin.");
                await using (stream.ConfigureAwait(false))
                {
                    var photo = new OutgoingPhoto(destination.ChatId, thread, InputFile.FromStream(stream, "poster.png"), sample.Html ?? string.Empty, ParseMode.Html, buttons, null, silent);
                    return await sender.SendPhotoAsync(photo, token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && TelegramErrorClassifier.ClassifyDelivery(ex).Kind == DeliveryErrorKind.PhotosBlocked)
            {
                // A first-time photo refusal, as delivery handles it: remember it and send the text once instead.
                _health.SetPhotosBlocked(destination.Id);
                return await SendText(sender, token).ConfigureAwait(false);
            }
        }

        return LoggedAsync(destination, sample.EventKey, CheckAndSendAsync(destination, Send, ct, thread));
    }

    /// <summary>Records the test's outcome in the Log (never failing the test because of the Log).</summary>
    private async Task<TestSendResult> LoggedAsync(Destination destination, string eventKey, Task<TestSendResult> send)
    {
        var result = await send.ConfigureAwait(false);
        _log?.Test(destination, eventKey, result.Sent, result.Error);
        return result;
    }

    private async Task<TestSendResult> CheckAndSendAsync(Destination destination, Func<ITelegramSender, CancellationToken, Task<Message?>> send, CancellationToken ct, int? thread = null)
    {
        if (_connection.Sender is not { } sender)
        {
            return new TestSendResult(false, _strings.Get("destination.notConnected"), _health.Get(destination.Id));
        }

        DestinationHealth health;
        try
        {
            health = await _health.CheckNowAsync(destination.Id, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TgJelly could not check {Destination} before a test send", destination.Title);
            return new TestSendResult(false, _strings.Get("token.unreachable"), _health.Get(destination.Id));
        }

        if (health.Status != HealthStatus.Ok)
        {
            return new TestSendResult(false, health.LastError, health);
        }

        try
        {
            var sent = await send(sender, ct).ConfigureAwait(false);
            try
            {
                if (_settings is not null)
                {
                    TopicNames.LearnFrom(_settings, destination.Id, sent);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The test went out; a name it couldn't remember mustn't make it look failed.
                _logger.LogWarning(ex, "TgJelly could not remember a topic's name");
            }

            return new TestSendResult(true, null, _health.Get(destination.Id));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var error = TelegramErrorClassifier.ClassifyDelivery(ex);
            var status = error.Kind switch
            {
                DeliveryErrorKind.Lost => HealthStatus.Lost,
                DeliveryErrorKind.NeedsPermissions => HealthStatus.NeedsPermissions,
                DeliveryErrorKind.TopicMissing => HealthStatus.TopicMissing,
                _ => (HealthStatus?)null,
            };
            if (status == HealthStatus.TopicMissing && thread is { } t && t != destination.ThreadId)
            {
                // A routed topic is gone, not the destination's own: remember it, as delivery does.
                _health.SetTopicMissing(destination.Id, t);
            }
            else if (status is { } problem)
            {
                _health.MarkProblem(destination.Id, problem, error.Description);
            }

            _logger.LogWarning("TgJelly test send to {Destination} failed: {Error}", destination.Title, error.Description);
            return new TestSendResult(false, error.Description, _health.Get(destination.Id));
        }
    }

    /// <summary>
    /// Names the event groups a destination receives, as the test message does ("new media, server alerts").
    /// </summary>
    /// <param name="strings">The string table.</param>
    /// <param name="events">The destination's event keys.</param>
    /// <returns>Plain text, not escaped.</returns>
    internal static string GroupsText(StringTable strings, IReadOnlyCollection<string> events)
    {
        var groups = EventCatalog.Groups
            .Where(g => events.Any(e => EventCatalog.GroupOf(e) == g))
            .Select(g => strings.Get("groups." + g))
            .ToList();
        return groups.Count == 0 ? strings.Get("groups.none") : string.Join(strings.Get("groups.separator"), groups);
    }

    private string Message(Destination destination)
        => _strings.Get("test.message", ("groups", HtmlText.Escape(GroupsText(_strings, destination.Events))));
}
