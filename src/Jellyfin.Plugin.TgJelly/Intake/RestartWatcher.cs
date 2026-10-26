using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Hosting;
using MediaBrowser.Controller;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.Intake;

/// <summary>
/// Watches <c>HasPendingRestartChanged</c> and publishes <c>server.restart_pending</c> when a restart becomes
/// pending; the pipeline adds the reasons.
/// </summary>
public sealed class RestartWatcher : BackgroundService
{
    private readonly IServerApplicationHost _host;
    private readonly IntakeQueues _queues;
    private readonly ILogger<RestartWatcher> _logger;
    private readonly Lock _gate = new();
    private bool _subscribed;

    /// <summary>
    /// Initializes a new instance of the <see cref="RestartWatcher"/> class.
    /// </summary>
    /// <param name="host">The server host.</param>
    /// <param name="queues">The intake queues.</param>
    /// <param name="logger">The logger.</param>
    public RestartWatcher(IServerApplicationHost host, IntakeQueues queues, ILogger<RestartWatcher> logger)
    {
        _host = host;
        _queues = queues;
        _logger = logger;
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_subscribed)
            {
                _host.HasPendingRestartChanged -= OnPendingRestartChanged;
                _subscribed = false;
            }
        }

        try
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TgJelly restart watcher did not stop cleanly");
        }
    }

    /// <summary>
    /// Handles a change of the pending-restart flag.
    /// </summary>
    /// <param name="sender">The sender.</param>
    /// <param name="e">The event.</param>
    internal void OnPendingRestartChanged(object? sender, EventArgs e)
    {
        try
        {
            if (_host.HasPendingRestart)
            {
                _queues.Publish("server.restart_pending", new Dictionary<string, string>(StringComparer.Ordinal));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TgJelly could not take a pending restart");
        }
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Supervisor.RunAsync("restart watcher", RunAsync, TimeProvider.System, _logger, stoppingToken);

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        lock (_gate)
        {
            if (!_subscribed)
            {
                _host.HasPendingRestartChanged += OnPendingRestartChanged;
                _subscribed = true;
            }
        }

        await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
    }
}
