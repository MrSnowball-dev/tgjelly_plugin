using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot.Types;

namespace Jellyfin.Plugin.TgJelly.Bot;

/// <summary>
/// Handles one update from the poll loop. Must stay fast; long work goes to a work queue.
/// </summary>
public interface IUpdateHandler
{
    /// <summary>
    /// Handles an update.
    /// </summary>
    /// <param name="update">The update.</param>
    /// <param name="ct">Cancels handling.</param>
    /// <returns>A task.</returns>
    Task HandleAsync(Update update, CancellationToken ct);
}
