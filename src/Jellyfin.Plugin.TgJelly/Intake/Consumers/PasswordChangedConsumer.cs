using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using MediaBrowser.Controller.Events;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.Intake.Consumers;

/// <summary>
/// Publishes <c>security.password_changed</c>.
/// </summary>
public sealed class PasswordChangedConsumer : IEventConsumer<UserPasswordChangedEventArgs>
{
    private readonly IntakeQueues _queues;
    private readonly ILogger<PasswordChangedConsumer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PasswordChangedConsumer"/> class. Takes only the queue and a logger.
    /// </summary>
    /// <param name="queues">The intake queues.</param>
    /// <param name="logger">The logger.</param>
    public PasswordChangedConsumer(IntakeQueues queues, ILogger<PasswordChangedConsumer> logger)
    {
        _queues = queues;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task OnEvent(UserPasswordChangedEventArgs eventArgs)
    {
        ConsumerSupport.Publish(_queues, _logger, "security.password_changed", () => ConsumerSupport.Values(
            (EventFields.User, eventArgs.Argument?.Username),
            (EventFields.UserId, eventArgs.Argument?.Id.ToString("N"))));
        return Task.CompletedTask;
    }
}
