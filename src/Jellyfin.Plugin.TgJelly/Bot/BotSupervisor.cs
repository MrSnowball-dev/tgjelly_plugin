using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.Hosting;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.Strings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Jellyfin.Plugin.TgJelly.Bot;

/// <summary>
/// Owns the bot connection: waits for <c>state.lock</c>, Jellyfin's startup and a token;
/// then <c>getMe</c>, the webhook check and TgJelly's own GetUpdates loop with an in-memory offset. Each
/// <see cref="Restart"/> ends the current session completely before the next one starts, so two polls never overlap.
/// </summary>
public sealed class BotSupervisor : BackgroundService, ISupervisorControl
{
    /// <summary>
    /// The update types TgJelly asks for (always explicit).
    /// </summary>
    public static readonly UpdateType[] AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery, UpdateType.MyChatMember];

    private static readonly TimeSpan StartupPoll = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan WebhookRecheck = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(2);

    private readonly InstanceGate _gate;
    private readonly IServerStartup _startup;
    private readonly SecretsStore _secrets;
    private readonly StateStore _state;
    private readonly ITelegramClientFactory _factory;
    private readonly BotConnection _connection;
    private readonly HealthService _health;
    private readonly IUpdateHandler _handler;
    private readonly StringTable _strings;
    private readonly TimeProvider _time;
    private readonly ILogger<BotSupervisor> _logger;
    private readonly Lock _sessionGate = new();
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Owned and disposed by the session loop's using; this field only lets Restart cancel it.")]
    private CancellationTokenSource? _session;
    private ITelegramBotClient? _client;
    private int? _lastUpdateId;

    /// <summary>
    /// Initializes a new instance of the <see cref="BotSupervisor"/> class.
    /// </summary>
    /// <param name="gate">The instance gate.</param>
    /// <param name="startup">Jellyfin's startup state.</param>
    /// <param name="secrets">The secrets store, for the token.</param>
    /// <param name="state">The state store.</param>
    /// <param name="factory">The client factory.</param>
    /// <param name="connection">The bot connection this supervisor maintains.</param>
    /// <param name="health">The health service, for the startup re-check.</param>
    /// <param name="handler">The update handler.</param>
    /// <param name="strings">The string table.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public BotSupervisor(
        InstanceGate gate,
        IServerStartup startup,
        SecretsStore secrets,
        StateStore state,
        ITelegramClientFactory factory,
        BotConnection connection,
        HealthService health,
        IUpdateHandler handler,
        StringTable strings,
        TimeProvider time,
        ILogger<BotSupervisor> logger)
    {
        _gate = gate;
        _startup = startup;
        _secrets = secrets;
        _state = state;
        _factory = factory;
        _connection = connection;
        _health = health;
        _handler = handler;
        _strings = strings;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Gets the last handled update's ID in this session (in memory only).
    /// </summary>
    internal int? LastUpdateId
    {
        get
        {
            lock (_sessionGate)
            {
                return _lastUpdateId;
            }
        }
    }

    /// <inheritdoc />
    public void Restart()
    {
        lock (_sessionGate)
        {
            _session?.Cancel();
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TgJelly bot supervisor did not stop cleanly");
        }

        ITelegramBotClient? client;
        int? last;
        lock (_sessionGate)
        {
            client = _client;
            last = _lastUpdateId;
        }

        if (client is not null && last is { } id)
        {
            // Best effort: confirm the last batch so it isn't delivered again. Real time on purpose: shutdown must
            // finish within the host's grace even if a test clock never advances or Telegram never answers.
            using var timeout = new CancellationTokenSource(ConfirmTimeout);
            try
            {
                await client.GetUpdates(offset: id + 1, limit: 1, timeout: 0, cancellationToken: timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "TgJelly could not confirm the last update batch on shutdown");
            }
        }

        _connection.Detach();
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Supervisor.RunAsync("bot", RunAsync, _time, _logger, stoppingToken);

    private static Task WaitForRestartAsync(CancellationToken ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct);

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        _connection.SetState(ConnectionState.WaitingForInstance);
        await _gate.WaitForOwnershipAsync(stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            lock (_sessionGate)
            {
                _session = session;
                _lastUpdateId = null;
            }

            try
            {
                await RunSessionAsync(session.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (session.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("TgJelly bot session restarting");
            }
            finally
            {
                lock (_sessionGate)
                {
                    _session = null;
                }

                if (!stoppingToken.IsCancellationRequested)
                {
                    _connection.Detach();
                }
            }
        }
    }

    private async Task RunSessionAsync(CancellationToken ct)
    {
        while (!_startup.HasCompleted)
        {
            _connection.SetState(ConnectionState.WaitingForStartup);
            await Task.Delay(StartupPoll, _time, ct).ConfigureAwait(false);
        }

        var token = _secrets.Load().BotToken;
        if (string.IsNullOrEmpty(token))
        {
            _connection.SetState(ConnectionState.NoToken);
            await WaitForRestartAsync(ct).ConfigureAwait(false);
            return;
        }

        _connection.SetState(ConnectionState.Connecting);
        ITelegramBotClient client;
        try
        {
            client = _factory.Create(token);
        }
        catch (ArgumentException ex) when (ex.ParamName == "baseUrl")
        {
            _connection.SetState(ConnectionState.InvalidBaseUrl);
            _state.RecordError("bot", _strings.Get("connection.InvalidBaseUrl"));
            await WaitForRestartAsync(ct).ConfigureAwait(false);
            return;
        }
        catch (ArgumentException)
        {
            _connection.SetState(ConnectionState.TokenRejected, _strings.Get("token.format"));
            await WaitForRestartAsync(ct).ConfigureAwait(false);
            return;
        }

        lock (_sessionGate)
        {
            _client = client;
        }

        var sender = new TelegramSender(client, _logger);
        var backoff = new Backoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60));

        User me;
        while (true)
        {
            try
            {
                me = await sender.GetMeAsync(ct).ConfigureAwait(false);
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (!await HandleConnectErrorAsync(ex, backoff, ct).ConfigureAwait(false))
                {
                    return;
                }
            }
        }

        RememberBot(me);
        _connection.Attach(sender, me.Id, me.Username ?? string.Empty);

        while (true)
        {
            try
            {
                var webhook = await sender.GetWebhookInfoAsync(ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(webhook.Url))
                {
                    break;
                }

                // Deleted only by the dashboard's [Take over] or a token save.
                _connection.SetState(ConnectionState.WebhookConflict);
                await Task.Delay(WebhookRecheck, _time, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (!await HandleConnectErrorAsync(ex, backoff, ct).ConfigureAwait(false))
                {
                    return;
                }
            }
        }

        // Connected as soon as getMe and the webhook check pass: an empty long poll takes 50 s to return, and
        // neither Status nor the Outbox should wait for it.
        _connection.SetState(ConnectionState.Connected);
        _health.RequestCheckAll();
        await PollAsync(client, backoff, ct).ConfigureAwait(false);
    }

    // Returns false when the session should end (it then waited for a restart).
    private async Task<bool> HandleConnectErrorAsync(Exception ex, Backoff backoff, CancellationToken ct)
    {
        var error = TelegramErrorClassifier.ClassifyPoll(ex);
        if (error.Kind == PollErrorKind.TokenRejected)
        {
            _connection.SetState(ConnectionState.TokenRejected, error.Description);
            await WaitForRestartAsync(ct).ConfigureAwait(false);
            return false;
        }

        if (error.Kind == PollErrorKind.Unreachable)
        {
            _connection.SetState(ConnectionState.Unreachable, error.Description);
        }

        _logger.LogWarning("TgJelly could not connect to Telegram: {Error}", error.Description);
        await Task.Delay(error.RetryAfter ?? backoff.Next(), _time, ct).ConfigureAwait(false);
        return true;
    }

    private async Task PollAsync(ITelegramBotClient client, Backoff backoff, CancellationToken ct)
    {
        var conflictBackoff = new Backoff(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5));

        // The first poll of a session, and the first after an error, answer at once, so the state (and recovery
        // after an outage) shows without waiting for a 50 s long poll.
        var quick = true;
        while (true)
        {
            Update[] updates;
            try
            {
                var offset = LastUpdateId is { } last ? last + 1 : (int?)null;
                updates = await client.GetUpdates(offset: offset, limit: 100, timeout: quick ? 0 : 50, allowedUpdates: AllowedUpdates, cancellationToken: ct).ConfigureAwait(false);
                quick = false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                quick = true;
                var error = TelegramErrorClassifier.ClassifyPoll(ex);
                switch (error.Kind)
                {
                    case PollErrorKind.TokenRejected:
                        _connection.SetState(ConnectionState.TokenRejected, error.Description);
                        await WaitForRestartAsync(ct).ConfigureAwait(false);
                        return;
                    case PollErrorKind.WebhookConflict:
                        // Back to the webhook check: end this session; the next one starts at once.
                        _connection.SetState(ConnectionState.WebhookConflict, error.Description);
                        return;
                    case PollErrorKind.ConflictingInstance:
                        _connection.SetState(ConnectionState.ConflictingInstance, error.Description);
                        await Task.Delay(conflictBackoff.Next(), _time, ct).ConfigureAwait(false);
                        continue;
                    case PollErrorKind.RateLimited:
                        await Task.Delay(error.RetryAfter ?? backoff.Next(), _time, ct).ConfigureAwait(false);
                        continue;
                    case PollErrorKind.Unreachable:
                        _connection.SetState(ConnectionState.Unreachable, error.Description);
                        break;
                }

                _logger.LogWarning("TgJelly poll failed: {Error}", error.Description);
                await Task.Delay(backoff.Next(), _time, ct).ConfigureAwait(false);
                continue;
            }

            _connection.SetState(ConnectionState.Connected);
            backoff.Reset();
            conflictBackoff.Reset();
            foreach (var update in updates)
            {
                try
                {
                    await _handler.HandleAsync(update, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "TgJelly could not handle update {UpdateId}; skipped", update.Id);
                }

                lock (_sessionGate)
                {
                    _lastUpdateId = update.Id;
                }
            }
        }
    }

    private void RememberBot(User me)
    {
        var username = me.Username ?? string.Empty;
        if (_state.IsLoaded && _state.Read(s => s.Telegram.BotId != me.Id || s.Telegram.BotUsername != username))
        {
            _state.Update(s =>
            {
                s.Telegram.BotId = me.Id;
                s.Telegram.BotUsername = username;
            });
        }
    }

    private sealed class Backoff
    {
        private readonly TimeSpan _first;
        private readonly TimeSpan _max;
        private TimeSpan _next;

        public Backoff(TimeSpan first, TimeSpan max)
        {
            _first = first;
            _max = max;
            _next = first;
        }

        public TimeSpan Next()
        {
            var current = _next;
            _next = _next * 2 > _max ? _max : _next * 2;
            return current;
        }

        public void Reset() => _next = _first;
    }
}
