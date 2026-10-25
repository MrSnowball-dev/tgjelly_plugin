using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using MediaBrowser.Controller.Events;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.Intake.Consumers;

/// <summary>
/// Publishes <c>security.user_created</c>.
/// </summary>
public sealed class UserCreatedConsumer : IEventConsumer<UserCreatedEventArgs>
{
    private readonly IntakeQueues _queues;
    private readonly ILogger<UserCreatedConsumer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserCreatedConsumer"/> class. Takes only the queue and a logger.
    /// </summary>
    /// <param name="queues">The intake queues.</param>
    /// <param name="logger">The logger.</param>
    public UserCreatedConsumer(IntakeQueues queues, ILogger<UserCreatedConsumer> logger)
    {
        _queues = queues;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task OnEvent(UserCreatedEventArgs eventArgs)
    {
        ConsumerSupport.Publish(_queues, _logger, "security.user_created", () => ConsumerSupport.Values(
            (EventFields.User, eventArgs.Argument?.Username),
            (EventFields.UserId, eventArgs.Argument?.Id.ToString("N"))));
        return Task.CompletedTask;
    }
}
