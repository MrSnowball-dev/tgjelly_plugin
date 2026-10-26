using System.Linq;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.State.Documents;

namespace Jellyfin.Plugin.TgJelly.Destinations;

/// <summary>
/// Whether the manual setup fallbacks are shown: after a setup failure in the last 7 days, while some
/// destination is unhealthy, or while a chat was added outside setup. It only controls what the two UIs show; the
/// API accepts manual adds at any time.
/// </summary>
public sealed class SetupGate
{
    private readonly BotMemory _memory;
    private readonly HealthService _health;
    private readonly SettingsStore _settings;

    /// <summary>
    /// Initializes a new instance of the <see cref="SetupGate"/> class.
    /// </summary>
    /// <param name="memory">The bot's memory, for the failures and the detected chats.</param>
    /// <param name="health">The health service.</param>
    /// <param name="settings">The settings store.</param>
    public SetupGate(BotMemory memory, HealthService health, SettingsStore settings)
    {
        _memory = memory;
        _health = health;
        _settings = settings;
    }

    /// <summary>
    /// Tells whether the manual fallbacks are unlocked.
    /// </summary>
    /// <returns><c>true</c> when any of the conditions holds.</returns>
    public bool IsOpen()
    {
        var current = _settings.Current;
        return _memory.HasRecentFailure()
            || current.Destinations.Any(d => _health.Get(d.Id).Status != HealthStatus.Ok)
            || _memory.VisibleDetected(current).Count > 0;
    }
}
