using System.Threading.Tasks;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Events.Authentication;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.Intake.Consumers;

/// <summary>
/// Publishes <c>security.login_failed</c>.
/// </summary>
public sealed class LoginFailedConsumer : IEventConsumer<AuthenticationRequestEventArgs>
{
    private readonly IntakeQueues _queues;
    private readonly ILogger<LoginFailedConsumer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LoginFailedConsumer"/> class. Takes only the queue and a logger.
    /// </summary>
    /// <param name="queues">The intake queues.</param>
    /// <param name="logger">The logger.</param>
    public LoginFailedConsumer(IntakeQueues queues, ILogger<LoginFailedConsumer> logger)
    {
        _queues = queues;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task OnEvent(AuthenticationRequestEventArgs eventArgs)
    {
        ConsumerSupport.Publish(_queues, _logger, "security.login_failed", () => ConsumerSupport.Values(
            (EventFields.User, eventArgs.Username),
            (EventFields.UserKey, eventArgs.Username?.ToUpperInvariant()),
            (EventFields.Ip, eventArgs.RemoteEndPoint),
            (EventFields.Client, eventArgs.App),
            (EventFields.Device, eventArgs.DeviceName),
            (EventFields.DeviceId, eventArgs.DeviceId)));
        return Task.CompletedTask;
    }
}
