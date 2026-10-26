namespace Jellyfin.Plugin.TgJelly.Bot;

/// <summary>
/// Whether Jellyfin has finished starting (nothing is sent before that).
/// </summary>
public interface IServerStartup
{
    /// <summary>
    /// Gets a value indicating whether Jellyfin's startup has completed.
    /// </summary>
    bool HasCompleted { get; }
}
