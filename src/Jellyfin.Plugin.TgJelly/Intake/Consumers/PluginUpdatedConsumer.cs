using System.Threading.Tasks;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Events.Updates;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.Intake.Consumers;

/// <summary>
/// Publishes <c>server.plugin_updated</c> (spec §7.1).
/// </summary>
public sealed class PluginUpdatedConsumer : IEventConsumer<PluginUpdatedEventArgs>
{
    private readonly IntakeQueues _queues;
    private readonly ILogger<PluginUpdatedConsumer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginUpdatedConsumer"/> class. Takes only the queue and a logger (F6).
    /// </summary>
    /// <param name="queues">The intake queues.</param>
    /// <param name="logger">The logger.</param>
    public PluginUpdatedConsumer(IntakeQueues queues, ILogger<PluginUpdatedConsumer> logger)
    {
        _queues = queues;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task OnEvent(PluginUpdatedEventArgs eventArgs)
    {
        ConsumerSupport.Publish(_queues, _logger, "server.plugin_updated", () => ConsumerSupport.Values(
            (EventFields.Plugin, eventArgs.Argument?.Name),
            (EventFields.Version, eventArgs.Argument?.Version?.ToString()),
            (EventFields.Changelog, eventArgs.Argument?.Changelog)));
        return Task.CompletedTask;
    }
}
