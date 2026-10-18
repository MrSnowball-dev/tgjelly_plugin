using System.Threading.Tasks;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Events.Updates;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.Intake.Consumers;

/// <summary>
/// Publishes <c>server.plugin_uninstalled</c> (spec §7.1).
/// </summary>
public sealed class PluginUninstalledConsumer : IEventConsumer<PluginUninstalledEventArgs>
{
    private readonly IntakeQueues _queues;
    private readonly ILogger<PluginUninstalledConsumer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginUninstalledConsumer"/> class. Takes only the queue and a logger (F6).
    /// </summary>
    /// <param name="queues">The intake queues.</param>
    /// <param name="logger">The logger.</param>
    public PluginUninstalledConsumer(IntakeQueues queues, ILogger<PluginUninstalledConsumer> logger)
    {
        _queues = queues;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task OnEvent(PluginUninstalledEventArgs eventArgs)
    {
        ConsumerSupport.Publish(_queues, _logger, "server.plugin_uninstalled", () => ConsumerSupport.Values(
            (EventFields.Plugin, eventArgs.Argument?.Name),
            (EventFields.Version, eventArgs.Argument?.Version?.ToString())));
        return Task.CompletedTask;
    }
}
