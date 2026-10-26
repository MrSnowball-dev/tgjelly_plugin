using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Logging;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.Strings;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Types;

namespace Jellyfin.Plugin.TgJelly.Bot;

/// <summary>
/// Saving a bot token, switching bots, disconnecting and taking over a webhook. Nothing is stored
/// until every check has passed, and one save runs at a time.
/// </summary>
public sealed class BotTokenService : IDisposable
{
    private readonly ITelegramClientFactory _factory;
    private readonly SecretsStore _secrets;
    private readonly StateStore _state;
    private readonly SettingsStore _settings;
    private readonly ISupervisorControl _supervisor;
    private readonly StringTable _strings;
    private readonly TimeProvider _time;
    private readonly ILogger<BotTokenService> _logger;
    private readonly Log.LogRecorder? _log;
    private readonly SemaphoreSlim _saving = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="BotTokenService"/> class.
    /// </summary>
    /// <param name="factory">The client factory.</param>
    /// <param name="secrets">The secrets store.</param>
    /// <param name="state">The state store.</param>
    /// <param name="settings">The settings, for the destinations.</param>
    /// <param name="supervisor">Restarts the bot session.</param>
    /// <param name="strings">The string table.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="log">Where messages a disconnect drops from the Outbox are recorded for the Log, if anywhere.</param>
    public BotTokenService(ITelegramClientFactory factory, SecretsStore secrets, StateStore state, SettingsStore settings, ISupervisorControl supervisor, StringTable strings, TimeProvider time, ILogger<BotTokenService> logger, Log.LogRecorder? log = null)
    {
        _log = log;
        _factory = factory;
        _secrets = secrets;
        _state = state;
        _settings = settings;
        _supervisor = supervisor;
        _strings = strings;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Checks and saves a token.
    /// </summary>
    /// <param name="token">The token as pasted.</param>
    /// <param name="confirmSwitch">Whether the user confirmed switching to a different bot.</param>
    /// <param name="ct">Cancels the save.</param>
    /// <returns>The result.</returns>
    public async Task<TokenSaveResult> SaveAsync(string token, bool confirmSwitch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(token);
        token = token.Trim();
        await _saving.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ITelegramSender sender;
            try
            {
                sender = new TelegramSender(_factory.Create(token));
            }
            catch (ArgumentException ex) when (ex.ParamName == "baseUrl")
            {
                return new TokenSaveResult(TokenSaveOutcome.Failed, _strings.Get("connection.InvalidBaseUrl"));
            }
            catch (ArgumentException)
            {
                return new TokenSaveResult(TokenSaveOutcome.FormatInvalid, _strings.Get("token.format"));
            }

            User me;
            try
            {
                me = await sender.GetMeAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var error = TelegramErrorClassifier.ClassifyPoll(ex);
                _logger.LogWarning(ex, "TgJelly could not verify a bot token");
                return error.Kind switch
                {
                    PollErrorKind.TokenRejected => new TokenSaveResult(TokenSaveOutcome.Rejected, _strings.Get("token.rejected")),
                    PollErrorKind.Unreachable => new TokenSaveResult(TokenSaveOutcome.Unreachable, _strings.Get("token.unreachable")),
                    _ => new TokenSaveResult(TokenSaveOutcome.Failed, _strings.Get("token.failed", ("error", SecretRedactor.Redact(error.Description)))),
                };
            }

            var username = me.Username ?? string.Empty;
            var storedBot = _state.Read(s => s.Telegram.BotId);
            var destinations = _settings.Current.Destinations.Select(d => d.Id).ToArray();
            var differentBot = storedBot is { } id && id != me.Id;
            if (differentBot && destinations.Length > 0 && !confirmSwitch)
            {
                return new TokenSaveResult(TokenSaveOutcome.NeedsConfirm, _strings.Plural("token.switch", destinations.Length, ("bot", username)), username);
            }

            var webhookDeleted = false;
            try
            {
                var webhook = await sender.GetWebhookInfoAsync(ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(webhook.Url))
                {
                    // The only automatic webhook deletion.
                    await sender.DeleteWebhookAsync(ct).ConfigureAwait(false);
                    webhookDeleted = true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The supervisor reports a remaining webhook as a conflict; the token is still good.
                _logger.LogWarning(ex, "TgJelly could not check the bot's webhook");
            }

            var now = _time.GetUtcNow();
            var lostMessage = _strings.Get("health.botChanged", ("bot", username));
            if (differentBot)
            {
                _state.Update(s => _state.UpdateQueue(q => BotStateReset.ForDifferentBot(s, q, destinations, lostMessage, now)));
            }

            _secrets.Update(s => s with { BotToken = token });
            _state.Update(s =>
            {
                s.Telegram.BotId = me.Id;
                s.Telegram.BotUsername = username;
            });
            _supervisor.Restart();
            _logger.LogInformation("TgJelly connected bot @{Bot}", username);
            return new TokenSaveResult(TokenSaveOutcome.Connected, _strings.Get("connection.Connected", ("bot", username)), username, webhookDeleted);
        }
        finally
        {
            _saving.Release();
        }
    }

    /// <summary>
    /// Disconnects the bot: clears the token and the bot-scoped state, keeps the proxy credentials and
    /// the bot identity. Waits for a token save still in flight, so that save can't finish after it.
    /// </summary>
    /// <param name="removeDestinations">Whether to remove every destination too, in one settings change. Their Health,
    /// SentMessages and Outbox entries go with the bot-scoped state either way.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>A task.</returns>
    public async Task DisconnectAsync(bool removeDestinations, CancellationToken ct)
    {
        await _saving.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (removeDestinations)
            {
                _settings.Update(null, c => c.Destinations = []);
            }

            _secrets.Update(s => s with { BotToken = string.Empty });
            List<string> dropped = [];
            _state.Update(s => _state.UpdateQueue(q =>
            {
                dropped = [.. q.Outbox.Select(Log.LogRecorder.RowIdOf)];
                BotStateReset.ForDisconnect(s, q);
            }));
            _log?.Disconnected(dropped);
            _supervisor.Restart();
        }
        finally
        {
            _saving.Release();
        }
    }

    /// <summary>
    /// Deletes another app's webhook with the stored token, then restarts the session (the dashboard's [Take over]).
    /// </summary>
    /// <param name="ct">Cancels the call.</param>
    /// <returns><c>false</c> when no token is stored.</returns>
    public async Task<bool> TakeOverWebhookAsync(CancellationToken ct)
    {
        var token = _secrets.Load().BotToken;
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        await new TelegramSender(_factory.Create(token)).DeleteWebhookAsync(ct).ConfigureAwait(false);
        _supervisor.Restart();
        return true;
    }

    /// <inheritdoc />
    public void Dispose() => _saving.Dispose();
}
