using System.Globalization;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using MediaBrowser.Controller.Events;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.Intake.Consumers;

/// <summary>
/// Publishes <c>security.lockout</c>.
/// </summary>
public sealed class LockoutConsumer : IEventConsumer<UserLockedOutEventArgs>
{
    private readonly IntakeQueues _queues;
    private readonly ILogger<LockoutConsumer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LockoutConsumer"/> class. Takes only the queue and a logger.
    /// </summary>
    /// <param name="queues">The intake queues.</param>
    /// <param name="logger">The logger.</param>
    public LockoutConsumer(IntakeQueues queues, ILogger<LockoutConsumer> logger)
    {
        _queues = queues;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task OnEvent(UserLockedOutEventArgs eventArgs)
    {
        ConsumerSupport.Publish(_queues, _logger, "security.lockout", () => ConsumerSupport.Values(
            (EventFields.User, eventArgs.Argument?.Username),
            (EventFields.UserKey, eventArgs.Argument?.Username?.ToUpperInvariant()),
            (EventFields.UserId, eventArgs.Argument?.Id.ToString("N")),
            (EventFields.Attempts, eventArgs.Argument?.InvalidLoginAttemptCount.ToString(CultureInfo.InvariantCulture))));
        return Task.CompletedTask;
    }
}
