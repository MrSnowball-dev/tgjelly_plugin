using System.Threading.Tasks;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Events.Authentication;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.Intake.Consumers;

/// <summary>
/// Publishes <c>security.login_succeeded</c>.
/// </summary>
public sealed class LoginSucceededConsumer : IEventConsumer<AuthenticationResultEventArgs>
{
    private readonly IntakeQueues _queues;
    private readonly ILogger<LoginSucceededConsumer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LoginSucceededConsumer"/> class. Takes only the queue and a logger.
    /// </summary>
    /// <param name="queues">The intake queues.</param>
    /// <param name="logger">The logger.</param>
    public LoginSucceededConsumer(IntakeQueues queues, ILogger<LoginSucceededConsumer> logger)
    {
        _queues = queues;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task OnEvent(AuthenticationResultEventArgs eventArgs)
    {
        ConsumerSupport.Publish(_queues, _logger, "security.login_succeeded", () => ConsumerSupport.Values(
            (EventFields.User, eventArgs.User?.Name),
            (EventFields.UserId, eventArgs.User?.Id.ToString("N")),
            (EventFields.Ip, eventArgs.SessionInfo?.RemoteEndPoint),
            (EventFields.Client, eventArgs.SessionInfo?.Client),
            (EventFields.Device, eventArgs.SessionInfo?.DeviceName),
            (EventFields.DeviceId, eventArgs.SessionInfo?.DeviceId)));
        return Task.CompletedTask;
    }
}
