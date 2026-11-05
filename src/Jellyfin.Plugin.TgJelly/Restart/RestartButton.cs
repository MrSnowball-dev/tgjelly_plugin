using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Bot.Screens;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Library;
using Jellyfin.Plugin.TgJelly.Log;
using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Strings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Jellyfin.Plugin.TgJelly.Restart;

/// <summary>
/// The Restart button of "Jellyfin needs a restart". A press from a chat admin (anyone in a private chat) edits the
/// message to "Restarting…", saves a <see cref="PendingRestart"/> and restarts Jellyfin; once Jellyfin is back, the
/// pipeline edits the same message to "Jellyfin is up" (<c>server.started</c>'s template) instead of sending one to
/// that chat. A refused restart, or Jellyfin still running after the timeout, edits it to the error with the button
/// back. Never throws except <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class RestartButton
{
    /// <summary>The button's callback data.</summary>
    public static readonly string Data = CallbackData.Format("rs");

    // answerCallbackQuery's text limit.
    private const int PopupLimit = 200;

    private static readonly TimeSpan StaleGrace = TimeSpan.FromMinutes(5);

    private readonly BotConnection _connection;
    private readonly SettingsStore _settings;
    private readonly StateStore _state;
    private readonly IServerRestart _server;
    private readonly IServerInfo _info;
    private readonly Formatter _formatter;
    private readonly TimeProvider _time;
    private readonly CancellationToken _stopping;
    private readonly ILogger<RestartButton> _logger;
    private readonly LogRecorder? _log;

    /// <summary>
    /// Initializes a new instance of the <see cref="RestartButton"/> class.
    /// </summary>
    /// <param name="connection">The bot connection.</param>
    /// <param name="settings">The settings: destinations and the timeout.</param>
    /// <param name="state">The state store, which keeps the pending restart across it.</param>
    /// <param name="server">Jellyfin's restart.</param>
    /// <param name="info">The server info, for its name.</param>
    /// <param name="formatter">The formatter, for the texts.</param>
    /// <param name="time">The clock.</param>
    /// <param name="lifetime">The host lifetime: its stop ends the timeout without an error, since that is the restart.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="log">Where presses and failures are recorded for the Log, if anywhere.</param>
    public RestartButton(BotConnection connection, SettingsStore settings, StateStore state, IServerRestart server, IServerInfo info, Formatter formatter, TimeProvider time, IHostApplicationLifetime lifetime, ILogger<RestartButton> logger, LogRecorder? log = null)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        _connection = connection;
        _settings = settings;
        _state = state;
        _server = server;
        _info = info;
        _formatter = formatter;
        _time = time;
        _stopping = lifetime.ApplicationStopping;
        _logger = logger;
        _log = log;
    }

    /// <summary>Gets the task that waits for the timeout of the last press (for tests).</summary>
    internal Task Watch { get; private set; } = Task.CompletedTask;

    private StringTable Strings => _formatter.Strings;

    /// <summary>
    /// Returns the button.
    /// </summary>
    /// <param name="strings">The string table, for its label.</param>
    /// <returns>A callback button.</returns>
    public static OutboxButton Button(StringTable strings)
    {
        ArgumentNullException.ThrowIfNull(strings);
        return new OutboxButton { Text = strings.Get("button.restart"), CallbackData = Data };
    }

    /// <summary>
    /// Whether a press is of this button.
    /// </summary>
    /// <param name="query">The press.</param>
    /// <returns><c>true</c> for a Restart press.</returns>
    public static bool IsPress(CallbackQuery query) => string.Equals(query?.Data, Data, StringComparison.Ordinal);

    /// <summary>
    /// Handles a press, in any chat; the press is not answered before this.
    /// </summary>
    /// <param name="query">The press.</param>
    /// <param name="ct">Cancels the handling.</param>
    /// <returns>A task.</returns>
    public async Task OnPressAsync(CallbackQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (_connection.Sender is not { } sender)
        {
            return;
        }

        try
        {
            await PressAsync(sender, query, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "TgJelly could not handle a Restart press");
        }
    }

    private async Task PressAsync(ITelegramSender sender, CallbackQuery query, CancellationToken ct)
    {
        if (query.Message is not { } message)
        {
            await AnswerAsync(sender, query.Id, null, ct).ConfigureAwait(false);
            return;
        }

        var chat = message.Chat;
        if (chat.Type != ChatType.Private)
        {
            string? refusal;
            try
            {
                var member = await sender.GetChatMemberAsync(chat.Id, query.From.Id, ct).ConfigureAwait(false);
                refusal = member.Status is ChatMemberStatus.Administrator or ChatMemberStatus.Creator ? null : Strings.Get("restart.notAdmin");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                refusal = Strings.Get("restart.checkFailed", ("error", Reason(ex)));
            }

            if (refusal is not null)
            {
                await AnswerAsync(sender, query.Id, refusal, ct).ConfigureAwait(false);
                return;
            }
        }

        var destinations = _settings.Current.Destinations;
        var thread = message.MessageThreadId ?? 0;
        var destination = destinations.FirstOrDefault(d => d.ChatId == chat.Id && d.ThreadId == thread)
            ?? destinations.FirstOrDefault(d => d.ChatId == chat.Id);
        if (destination is null)
        {
            await AnswerAsync(sender, query.Id, Strings.Get("restart.notDestination"), ct).ConfigureAwait(false);
            await RemoveButtonAsync(sender, chat.Id, message.Id, ct).ConfigureAwait(false);
            return;
        }

        // One that outlived its timeout by far was left by a crash before the restart: it doesn't block a new one.
        var timeout = Timeout();
        var now = _time.GetUtcNow();
        if (_state.Read(s => s.PendingRestart is { } earlier && now - earlier.At < timeout + StaleGrace))
        {
            await AnswerAsync(sender, query.Id, Strings.Get("restart.already"), ct).ConfigureAwait(false);
            return;
        }

        if (!_server.IsPending)
        {
            await AnswerAsync(sender, query.Id, Strings.Get("restart.notNeeded"), ct).ConfigureAwait(false);
            await RemoveButtonAsync(sender, chat.Id, message.Id, ct).ConfigureAwait(false);
            return;
        }

        var pending = new PendingRestart
        {
            DestinationId = destination.Id,
            ChatId = chat.Id,
            MessageId = message.Id,
            Photo = message.Photo is { Length: > 0 },
            LogId = "restart|" + destination.Id + "|" + now.UtcTicks.ToString(CultureInfo.InvariantCulture),
            At = now,
        };
        _state.Update(s => s.PendingRestart = pending);
        await AnswerAsync(sender, query.Id, null, ct).ConfigureAwait(false);
        _log?.RestartAsked(pending.LogId, destination, NameOf(query.From));
        _logger.LogInformation("TgJelly is restarting Jellyfin: Restart was pressed in {Destination}", destination.Title);
        await EditAsync(sender, pending, Strings.Get("restart.restarting", ("server", _info.ServerName)), withButton: false, ct).ConfigureAwait(false);

        // The new instance reads it to edit the message; Jellyfin's stop flushes too, but not after a crash.
        _state.FlushAll();
        try
        {
            _server.Restart();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Jellyfin refused the restart asked for with the Restart button");
            await FailAsync(sender, pending, Reason(ex), CancellationToken.None).ConfigureAwait(false);
            return;
        }

        Watch = WatchAsync(pending, timeout);
    }

    private TimeSpan Timeout()
        => TimeSpan.FromSeconds(Math.Clamp(_settings.Current.Restart.TimeoutSeconds, RestartOptions.MinTimeoutSeconds, RestartOptions.MaxTimeoutSeconds));

    // Jellyfin stopping cancels the wait: that is the restart. Still running after the timeout, it failed.
    private async Task WatchAsync(PendingRestart pending, TimeSpan timeout)
    {
        try
        {
            await Task.Delay(timeout, _time, _stopping).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (_connection.Sender is { } sender)
            {
                var seconds = ((int)timeout.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                await FailAsync(sender, pending, Strings.Get("restart.failed.timeout", ("seconds", seconds)), _stopping).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "TgJelly could not report a restart that did not happen");
        }
    }

    private async Task FailAsync(ITelegramSender sender, PendingRestart pending, string reason, CancellationToken ct)
    {
        var mine = false;
        _state.Update(s =>
        {
            if (s.PendingRestart?.LogId == pending.LogId)
            {
                s.PendingRestart = null;
                mine = true;
            }
        });
        if (!mine)
        {
            return;
        }

        _logger.LogWarning("The restart asked for with the Restart button failed: {Reason}", reason);
        _log?.RestartFailed(pending.LogId, reason);
        await EditAsync(sender, pending, Strings.Get("restart.failed", ("server", _info.ServerName), ("error", reason)), withButton: true, ct).ConfigureAwait(false);
    }

    private async Task EditAsync(ITelegramSender sender, PendingRestart pending, string text, bool withButton, CancellationToken ct)
    {
        var button = Button(Strings);
        var buttons = withButton ? new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData(button.Text, Data)) : null;
        var edit = new MessageEdit(pending.ChatId, pending.MessageId, _formatter.Escape(text), ParseMode.Html, buttons);
        try
        {
            if (pending.Photo)
            {
                await sender.EditCaptionAsync(edit, ct).ConfigureAwait(false);
            }
            else
            {
                await sender.EditTextAsync(edit, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "TgJelly could not edit the message whose Restart button was pressed");
        }
    }

    private async Task RemoveButtonAsync(ITelegramSender sender, long chatId, int messageId, CancellationToken ct)
    {
        try
        {
            await sender.EditButtonsAsync(chatId, messageId, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "TgJelly could not remove a Restart button");
        }
    }

    private async Task AnswerAsync(ITelegramSender sender, string queryId, string? text, CancellationToken ct)
    {
        try
        {
            if (text is null)
            {
                await sender.AnswerCallbackAsync(queryId, ct).ConfigureAwait(false);
            }
            else
            {
                await sender.AnswerCallbackAsync(queryId, text.Length > PopupLimit ? text[..(PopupLimit - 1)] + "…" : text, ct).ConfigureAwait(false);
            }
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("query is too old", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("TgJelly ignored an expired Restart press");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "TgJelly could not answer a Restart press");
        }
    }

    private static string NameOf(User user)
    {
        var name = string.Join(' ', new[] { user.FirstName, user.LastName }.Where(n => !string.IsNullOrWhiteSpace(n)));
        return user.Username is { Length: > 0 } username ? (name.Length > 0 ? name + " (@" + username + ")" : "@" + username) : name;
    }

    private string Reason(Exception ex)
        => string.IsNullOrWhiteSpace(ex.Message) ? Strings.Get("restart.failed.noDetails") : Logging.SecretRedactor.Redact(ex.Message);
}
