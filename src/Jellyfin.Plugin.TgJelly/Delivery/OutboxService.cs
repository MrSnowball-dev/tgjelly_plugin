using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Compat;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.Hosting;
using Jellyfin.Plugin.TgJelly.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.Delivery;

/// <summary>
/// The Outbox loop (spec §9.3): while connected, picks the next deliverable entry and delivers it; otherwise sleeps
/// until the queue, a destination's health, the settings or the connection change, or a waiting entry is due.
/// </summary>
public sealed class OutboxService : BackgroundService
{
    private static readonly TimeSpan IdleWait = TimeSpan.FromSeconds(30);

    private readonly InstanceGate _gate;
    private readonly CompatResult _compat;
    private readonly BotConnection _connection;
    private readonly Outbox _outbox;
    private readonly OutboxDelivery _delivery;
    private readonly HealthService _health;
    private readonly SettingsStore _settings;
    private readonly PacingGate _pacing;
    private readonly TimeProvider _time;
    private readonly ILogger<OutboxService> _logger;
    private readonly AsyncSignal _wake = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="OutboxService"/> class.
    /// </summary>
    /// <param name="gate">The instance gate.</param>
    /// <param name="compat">The compatibility result; an incompatible build never sends.</param>
    /// <param name="connection">The bot connection.</param>
    /// <param name="outbox">The Outbox.</param>
    /// <param name="delivery">The delivery.</param>
    /// <param name="health">The health service.</param>
    /// <param name="settings">The settings, for destinations.</param>
    /// <param name="pacing">The pacing gate.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public OutboxService(InstanceGate gate, CompatResult compat, BotConnection connection, Outbox outbox, OutboxDelivery delivery, HealthService health, SettingsStore settings, PacingGate pacing, TimeProvider time, ILogger<OutboxService> logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(settings);
        _gate = gate;
        _compat = compat;
        _connection = connection;
        _outbox = outbox;
        _delivery = delivery;
        _health = health;
        _settings = settings;
        _pacing = pacing;
        _time = time;
        _logger = logger;
        _connection.Changed += Wake;
        _outbox.Changed += Wake;
        _health.Recovered += Wake;
        _settings.SettingsChanged += Wake;
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _connection.Changed -= Wake;
        _outbox.Changed -= Wake;
        _health.Recovered -= Wake;
        _settings.SettingsChanged -= Wake;
        base.Dispose();
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Supervisor.RunAsync("outbox", RunAsync, _time, _logger, stoppingToken);

    private void Wake(object? sender, EventArgs e) => _wake.Set();

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        await _gate.WaitForOwnershipAsync(stoppingToken).ConfigureAwait(false);
        if (!_compat.IsCompatible)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_connection.CanSend)
            {
                await _wake.WaitAsync(IdleWait, _time, stoppingToken).ConfigureAwait(false);
                continue;
            }

            var now = _time.GetUtcNow();
            _outbox.PurgeExpired(now);
            var settings = _settings.Current;
            _outbox.PruneSent(now, settings.Batching.LateEditHours);
            var destinations = settings.Destinations;
            foreach (var orphan in _outbox.DestinationIds().Where(id => destinations.All(d => d.Id != id)))
            {
                _outbox.RemoveDestination(orphan);
            }

            var views = destinations.ToDictionary(d => d.Id, d => new DestinationView(d.ChatId, d.Kind, d.Enabled && _health.IsDeliverable(d.Id)), StringComparer.Ordinal);
            var pick = _outbox.Pick(views, _pacing, now);
            if (pick.Entry is { } entry)
            {
                await _delivery.DeliverAsync(entry, destinations.First(d => d.Id == entry.DestinationId), stoppingToken).ConfigureAwait(false);
                continue;
            }

            var wait = pick.WakeAt is { } at ? Clamp(at - now) : IdleWait;
            await _wake.WaitAsync(wait, _time, stoppingToken).ConfigureAwait(false);
        }
    }

    private static TimeSpan Clamp(TimeSpan wait)
        => wait < TimeSpan.Zero ? TimeSpan.Zero : wait > IdleWait ? IdleWait : wait;
}
