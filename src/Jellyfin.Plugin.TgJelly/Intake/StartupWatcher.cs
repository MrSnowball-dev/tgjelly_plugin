using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Hosting;
using Jellyfin.Plugin.TgJelly.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.Intake;

/// <summary>
/// Emits <c>server.started</c> once per plugin start, after Jellyfin's startup has completed.
/// <c>CoreStartupHasCompleted</c> has no change event, so it is polled every second. A restart creates a new instance,
/// so each start announces once.
/// </summary>
public sealed class StartupWatcher : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly IServerStartup _startup;
    private readonly IServerInfo _server;
    private readonly IntakeQueues _queues;
    private readonly TimeProvider _time;
    private readonly ILogger<StartupWatcher> _logger;
    private bool _announced;

    /// <summary>
    /// Initializes a new instance of the <see cref="StartupWatcher"/> class.
    /// </summary>
    /// <param name="startup">Jellyfin's startup state.</param>
    /// <param name="server">The server info.</param>
    /// <param name="queues">The intake queues.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public StartupWatcher(IServerStartup startup, IServerInfo server, IntakeQueues queues, TimeProvider time, ILogger<StartupWatcher> logger)
    {
        _startup = startup;
        _server = server;
        _queues = queues;
        _time = time;
        _logger = logger;
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
            _logger.LogWarning(ex, "TgJelly startup watcher did not stop cleanly");
        }
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Supervisor.RunAsync("startup watcher", RunAsync, _time, _logger, stoppingToken);

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!_announced)
        {
            if (_startup.HasCompleted)
            {
                _queues.Publish("server.started", Consumers.ConsumerSupport.Values(
                    (EventFields.Version, _server.VersionString),
                    (EventFields.Server, _server.ServerName)));
                _announced = true;
                return;
            }

            await Task.Delay(PollInterval, _time, stoppingToken).ConfigureAwait(false);
        }
    }
}
