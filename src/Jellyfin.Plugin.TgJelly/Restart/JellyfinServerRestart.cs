using MediaBrowser.Controller;

namespace Jellyfin.Plugin.TgJelly.Restart;

/// <summary>
/// <see cref="IServerRestart"/> over <c>IServerApplicationHost.HasPendingRestart</c> and
/// <see cref="ISystemManager.Restart"/> (the same call as the dashboard's Restart button; on 10.11 and 12).
/// </summary>
/// <param name="host">The server host.</param>
/// <param name="system">The system manager.</param>
public sealed class JellyfinServerRestart(IServerApplicationHost host, ISystemManager system) : IServerRestart
{
    /// <inheritdoc />
    public bool IsPending => host.HasPendingRestart;

    /// <inheritdoc />
    public void Restart() => system.Restart();
}
