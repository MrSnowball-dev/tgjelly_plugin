using System.Threading.Tasks;
using MediaBrowser.Common.Updates;
using MediaBrowser.Controller.Events;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.Intake.Consumers;

/// <summary>
/// Publishes <c>server.plugin_install_failed</c> (spec §7.1).
/// </summary>
public sealed class PluginInstallFailedConsumer : IEventConsumer<InstallationFailedEventArgs>
{
    private readonly IntakeQueues _queues;
    private readonly ILogger<PluginInstallFailedConsumer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginInstallFailedConsumer"/> class. Takes only the queue and a logger (F6).
    /// </summary>
    /// <param name="queues">The intake queues.</param>
    /// <param name="logger">The logger.</param>
    public PluginInstallFailedConsumer(IntakeQueues queues, ILogger<PluginInstallFailedConsumer> logger)
    {
        _queues = queues;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task OnEvent(InstallationFailedEventArgs eventArgs)
    {
        ConsumerSupport.Publish(_queues, _logger, "server.plugin_install_failed", () => ConsumerSupport.Values(
            (EventFields.Plugin, eventArgs.InstallationInfo?.Name),
            (EventFields.Version, eventArgs.InstallationInfo?.Version?.ToString()),
            (EventFields.Error, eventArgs.Exception?.Message)));
        return Task.CompletedTask;
    }
}
