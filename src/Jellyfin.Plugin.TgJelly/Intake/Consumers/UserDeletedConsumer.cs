using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using MediaBrowser.Controller.Events;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.Intake.Consumers;

/// <summary>
/// Publishes <c>security.user_deleted</c> (spec §7.1).
/// </summary>
public sealed class UserDeletedConsumer : IEventConsumer<UserDeletedEventArgs>
{
    private readonly IntakeQueues _queues;
    private readonly ILogger<UserDeletedConsumer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserDeletedConsumer"/> class. Takes only the queue and a logger (F6).
    /// </summary>
    /// <param name="queues">The intake queues.</param>
    /// <param name="logger">The logger.</param>
    public UserDeletedConsumer(IntakeQueues queues, ILogger<UserDeletedConsumer> logger)
    {
        _queues = queues;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task OnEvent(UserDeletedEventArgs eventArgs)
    {
        ConsumerSupport.Publish(_queues, _logger, "security.user_deleted", () => ConsumerSupport.Values(
            (EventFields.User, eventArgs.Argument?.Username),
            (EventFields.UserId, eventArgs.Argument?.Id.ToString("N"))));
        return Task.CompletedTask;
    }
}
