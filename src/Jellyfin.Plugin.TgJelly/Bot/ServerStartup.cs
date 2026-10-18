using MediaBrowser.Controller;

namespace Jellyfin.Plugin.TgJelly.Bot;

/// <summary>
/// <see cref="IServerStartup"/> over <see cref="IServerApplicationHost.CoreStartupHasCompleted"/>.
/// </summary>
/// <param name="host">The server host.</param>
public sealed class ServerStartup(IServerApplicationHost host) : IServerStartup
{
    /// <inheritdoc />
    public bool HasCompleted => host.CoreStartupHasCompleted;
}
