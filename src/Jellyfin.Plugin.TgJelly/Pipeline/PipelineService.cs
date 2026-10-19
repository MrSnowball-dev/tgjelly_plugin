using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Compat;
using Jellyfin.Plugin.TgJelly.Hosting;
using Jellyfin.Plugin.TgJelly.Intake;
using Jellyfin.Plugin.TgJelly.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.Pipeline;

/// <summary>
/// Runs the notification pipeline while this instance owns the state, and drains it on stop (spec §12): the intake
/// queues go into readiness and the aggregator, and held lockouts go to the Outbox, within <see cref="DrainBudget"/>.
/// </summary>
public sealed class PipelineService : BackgroundService
{
    /// <summary>How long the graceful drain may take.</summary>
    public static readonly TimeSpan DrainBudget = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan IdleWait = TimeSpan.FromSeconds(1);

    private readonly InstanceGate _gate;
    private readonly CompatResult _compat;
    private readonly StateStore _state;
    private readonly IntakeQueues _queues;
    private readonly NotificationPipeline _pipeline;
    private readonly TimeProvider _time;
    private readonly ILogger<PipelineService> _logger;
    private volatile bool _started;

    /// <summary>
    /// Initializes a new instance of the <see cref="PipelineService"/> class.
    /// </summary>
    /// <param name="gate">The instance gate.</param>
    /// <param name="compat">The compatibility result; an incompatible build never starts.</param>
    /// <param name="state">The state store.</param>
    /// <param name="queues">The intake queues.</param>
    /// <param name="pipeline">The pipeline.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public PipelineService(InstanceGate gate, CompatResult compat, StateStore state, IntakeQueues queues, NotificationPipeline pipeline, TimeProvider time, ILogger<PipelineService> logger)
    {
        _gate = gate;
        _compat = compat;
        _state = state;
        _queues = queues;
        _pipeline = pipeline;
        _time = time;
        _logger = logger;
    }

    /// <summary>Gets a value indicating whether the pipeline was started.</summary>
    internal bool IsStarted => _started;

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
            _pipeline.Drain(DrainBudget);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The notification pipeline could not drain on stop");
        }
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Supervisor.RunAsync("pipeline", RunAsync, _time, _logger, stoppingToken);

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        await _gate.WaitForOwnershipAsync(stoppingToken).ConfigureAwait(false);
        if (!_compat.IsCompatible)
        {
            return;
        }

        while (!_state.IsLoaded)
        {
            await Task.Delay(IdleWait, _time, stoppingToken).ConfigureAwait(false);
        }

        if (!_started)
        {
            _pipeline.Start();
            _started = true;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await _queues.WaitAsync(IdleWait, stoppingToken).ConfigureAwait(false);
            _pipeline.RunOnce();
        }
    }
}
