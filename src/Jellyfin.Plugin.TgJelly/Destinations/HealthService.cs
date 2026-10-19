using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Hosting;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.Destinations;

/// <summary>
/// Owns <c>state.health</c> (spec §9.4): records problems found by sends, runs re-checks (on request, at session
/// start, on <c>my_chat_member</c>, and every 30 minutes while a destination is unhealthy or has photos blocked),
/// and raises <see cref="Recovered"/> so held Outbox entries go out.
/// </summary>
public sealed class HealthService : BackgroundService
{
    /// <summary>
    /// How often an unhealthy destination is re-checked.
    /// </summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan LoopInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FailedCheckRetry = TimeSpan.FromMinutes(1);

    private readonly InstanceGate _gate;
    private readonly BotConnection _connection;
    private readonly DestinationChecker _checker;
    private readonly SettingsStore _settings;
    private readonly StateStore _state;
    private readonly TimeProvider _time;
    private readonly ILogger<HealthService> _logger;
    private readonly AsyncSignal _wake = new();
    private readonly Lock _requestGate = new();
    private readonly HashSet<string> _requested = new(StringComparer.Ordinal);
    private bool _requestedAll;

    /// <summary>
    /// Initializes a new instance of the <see cref="HealthService"/> class.
    /// </summary>
    /// <param name="gate">The instance gate.</param>
    /// <param name="connection">The bot connection.</param>
    /// <param name="checker">The chat checker.</param>
    /// <param name="settings">The settings, for the destinations.</param>
    /// <param name="state">The state store holding the health section.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public HealthService(InstanceGate gate, BotConnection connection, DestinationChecker checker, SettingsStore settings, StateStore state, TimeProvider time, ILogger<HealthService> logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _gate = gate;
        _connection = connection;
        _checker = checker;
        _settings = settings;
        _state = state;
        _time = time;
        _logger = logger;
        _connection.Changed += OnConnectionChanged;
    }

    /// <summary>
    /// Raised when a destination goes from unhealthy to OK.
    /// </summary>
    public event EventHandler? Recovered;

    /// <summary>
    /// Raised, with the destination ID, when a destination's status changes to Lost, NeedsPermissions or TopicMissing.
    /// Subscribers must be quick; one that throws is logged and ignored.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1003:Use generic event handler instances", Justification = "The plan 4 contract passes the destination ID itself.")]
    public event EventHandler<string>? ProblemFound;

    /// <summary>
    /// Returns a copy of a destination's health; OK when nothing is recorded.
    /// </summary>
    /// <param name="destinationId">The destination.</param>
    /// <returns>The health.</returns>
    public DestinationHealth Get(string destinationId)
        => _state.Read(s => s.Health.TryGetValue(destinationId, out var h) ? h.Copy() : new DestinationHealth());

    /// <summary>
    /// Returns whether entries for a destination may be sent.
    /// </summary>
    /// <param name="destinationId">The destination.</param>
    /// <returns><c>true</c> when its health is OK.</returns>
    public bool IsDeliverable(string destinationId)
        => _state.Read(s => !s.Health.TryGetValue(destinationId, out var h) || h.Status == HealthStatus.Ok);

    /// <summary>
    /// Records a problem found by a send or a check. A new kind of problem resets <c>Notified</c>.
    /// </summary>
    /// <param name="destinationId">The destination.</param>
    /// <param name="status">The problem.</param>
    /// <param name="error">Its description.</param>
    public void MarkProblem(string destinationId, HealthStatus status, string error)
    {
        var now = _time.GetUtcNow();
        var changed = false;
        _state.Update(s =>
        {
            var h = Entry(s, destinationId);
            changed = h.Status != status;
            if (changed)
            {
                h.Notified = false;
            }

            h.Status = status;
            h.LastError = error;
            h.At = now;
            h.NextCheckAt = now + RecheckInterval;
        });

        if (changed && status is HealthStatus.Lost or HealthStatus.NeedsPermissions or HealthStatus.TopicMissing)
        {
            RaiseProblemFound(destinationId);
        }
    }

    /// <summary>
    /// Records that photos are blocked at a destination; posters are skipped until a re-check allows them.
    /// </summary>
    /// <param name="destinationId">The destination.</param>
    public void SetPhotosBlocked(string destinationId)
    {
        var now = _time.GetUtcNow();
        _state.Update(s =>
        {
            var h = Entry(s, destinationId);
            h.PhotosBlocked = true;
            h.NextCheckAt ??= now + RecheckInterval;
        });
    }

    /// <summary>
    /// Applies a check result.
    /// </summary>
    /// <param name="destinationId">The destination.</param>
    /// <param name="result">The result.</param>
    public void SetChecked(string destinationId, ChatCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Passed)
        {
            MarkProblem(destinationId, result.Problem, result.Message ?? string.Empty);
            return;
        }

        var now = _time.GetUtcNow();
        var recovered = false;
        _state.Update(s =>
        {
            var h = Entry(s, destinationId);
            recovered = h.Status != HealthStatus.Ok;
            h.Status = HealthStatus.Ok;
            h.PhotosBlocked = result.PhotosBlocked;
            h.LastError = null;
            h.At = now;
            h.Notified = false;
            h.NextCheckAt = result.PhotosBlocked ? now + RecheckInterval : null;
        });

        if (recovered)
        {
            Recovered?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Forgets a removed destination's health.
    /// </summary>
    /// <param name="destinationId">The destination.</param>
    public void Remove(string destinationId) => _state.Update(s => s.Health.Remove(destinationId));

    /// <summary>
    /// Checks a destination now and applies the result. Errors the checker throws (network, 5xx) propagate.
    /// </summary>
    /// <param name="destinationId">The destination.</param>
    /// <param name="ct">Cancels the check.</param>
    /// <returns>The destination's health afterwards.</returns>
    public async Task<DestinationHealth> CheckNowAsync(string destinationId, CancellationToken ct)
    {
        var destination = _settings.Current.Destinations.FirstOrDefault(d => d.Id == destinationId)
            ?? throw new KeyNotFoundException("No destination " + destinationId);
        var result = await _checker.CheckAsync(destination.ChatId, destination.ThreadId, ct).ConfigureAwait(false);
        SetChecked(destinationId, result);
        return Get(destinationId);
    }

    /// <summary>
    /// Asks for a re-check of every destination in a chat (e.g. after <c>my_chat_member</c>).
    /// </summary>
    /// <param name="chatId">The chat.</param>
    public void RequestCheckForChat(long chatId)
    {
        var ids = _settings.Current.Destinations.Where(d => d.ChatId == chatId).Select(d => d.Id).ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        lock (_requestGate)
        {
            _requested.UnionWith(ids);
        }

        _wake.Set();
    }

    /// <summary>
    /// Asks for a re-check of every destination (at the start of a bot session, spec §9.2 step 5).
    /// </summary>
    public void RequestCheckAll()
    {
        lock (_requestGate)
        {
            _requestedAll = true;
        }

        _wake.Set();
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _connection.Changed -= OnConnectionChanged;
        base.Dispose();
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Supervisor.RunAsync("health", RunAsync, _time, _logger, stoppingToken);

    private static DestinationHealth Entry(StateDocument state, string destinationId)
    {
        if (!state.Health.TryGetValue(destinationId, out var h))
        {
            h = new DestinationHealth();
            state.Health[destinationId] = h;
        }

        return h;
    }

    private void RaiseProblemFound(string destinationId)
    {
        try
        {
            ProblemFound?.Invoke(this, destinationId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A TgJelly problem subscriber failed for destination {Destination}", destinationId);
        }
    }

    private void OnConnectionChanged(object? sender, EventArgs e) => _wake.Set();

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        await _gate.WaitForOwnershipAsync(stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            await _wake.WaitAsync(LoopInterval, _time, stoppingToken).ConfigureAwait(false);
            if (!_state.IsLoaded || !_connection.CanSend)
            {
                continue;
            }

            foreach (var id in TakeDue())
            {
                try
                {
                    await CheckNowAsync(id, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "TgJelly could not re-check destination {Destination}", id);
                    var retryAt = _time.GetUtcNow() + FailedCheckRetry;
                    _state.Update(s =>
                    {
                        if (s.Health.TryGetValue(id, out var h) && (h.Status != HealthStatus.Ok || h.PhotosBlocked))
                        {
                            h.NextCheckAt = retryAt;
                        }
                    });
                }
            }
        }
    }

    private List<string> TakeDue()
    {
        var now = _time.GetUtcNow();
        var existing = _settings.Current.Destinations.Select(d => d.Id).ToList();
        HashSet<string> due;
        lock (_requestGate)
        {
            due = _requestedAll ? new HashSet<string>(existing, StringComparer.Ordinal) : new HashSet<string>(_requested, StringComparer.Ordinal);
            _requested.Clear();
            _requestedAll = false;
        }

        _state.Read(s =>
        {
            foreach (var (id, h) in s.Health)
            {
                if ((h.Status != HealthStatus.Ok || h.PhotosBlocked) && h.NextCheckAt is { } at && at <= now)
                {
                    due.Add(id);
                }
            }

            return 0;
        });

        return existing.Where(due.Contains).ToList();
    }
}
