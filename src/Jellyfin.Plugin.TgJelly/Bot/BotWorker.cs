using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Compat;
using Jellyfin.Plugin.TgJelly.Hosting;
using Jellyfin.Plugin.TgJelly.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.Bot;

/// <summary>
/// Runs the <see cref="BotWorkQueue"/> one item at a time while this instance owns the state, ticks the
/// <see cref="IBotFlows"/> every 30 seconds, and on stop runs what is still queued for at most 2 seconds.
/// Registered right before <see cref="BotSupervisor"/>, so it stops after the last update was handed to it.
/// </summary>
public sealed class BotWorker : BackgroundService
{
    private static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdleWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DrainBudget = TimeSpan.FromSeconds(2);

    private readonly InstanceGate _gate;
    private readonly CompatResult _compat;
    private readonly BotWorkQueue _queue;
    private readonly IBotFlows _flows;
    private readonly StateStore _state;
    private readonly TimeProvider _time;
    private readonly ILogger<BotWorker> _logger;
    private volatile bool _started;

    /// <summary>
    /// Initializes a new instance of the <see cref="BotWorker"/> class.
    /// </summary>
    /// <param name="gate">The instance gate.</param>
    /// <param name="compat">The compatibility result; an incompatible build never runs work.</param>
    /// <param name="queue">The work queue.</param>
    /// <param name="flows">The bot flows, ticked here.</param>
    /// <param name="state">The state store, where failures are recorded.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public BotWorker(InstanceGate gate, CompatResult compat, BotWorkQueue queue, IBotFlows flows, StateStore state, TimeProvider time, ILogger<BotWorker> logger)
    {
        _gate = gate;
        _compat = compat;
        _queue = queue;
        _flows = flows;
        _state = state;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        if (!_started)
        {
            return;
        }

        try
        {
            using var budget = new CancellationTokenSource(DrainBudget);
            await _queue.RunPendingAsync(budget.Token, RecordFailure).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TgJelly's bot worker could not drain on stop");
        }
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Supervisor.RunAsync("bot-worker", RunAsync, _time, _logger, stoppingToken);

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        await _gate.WaitForOwnershipAsync(stoppingToken).ConfigureAwait(false);
        if (!_compat.IsCompatible)
        {
            return;
        }

        _started = true;
        var lastTick = _time.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            await _queue.WaitAsync(IdleWait, stoppingToken).ConfigureAwait(false);
            await _queue.RunPendingAsync(stoppingToken, RecordFailure).ConfigureAwait(false);
            if (_time.GetUtcNow() - lastTick < TickEvery)
            {
                continue;
            }

            lastTick = _time.GetUtcNow();
            try
            {
                await _flows.TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RecordFailure("tick", ex);
            }
        }
    }

    private void RecordFailure(string item, Exception ex)
    {
        _logger.LogError(ex, "TgJelly's bot work item {Item} failed", item);
        _state.RecordError("bot", $"{item}: {ex.Message}");
    }
}
