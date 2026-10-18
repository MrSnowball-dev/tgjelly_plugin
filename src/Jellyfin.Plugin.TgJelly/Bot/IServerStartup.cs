namespace Jellyfin.Plugin.TgJelly.Bot;

/// <summary>
/// Whether Jellyfin has finished starting (spec §5.4 rule 5: nothing is sent before that).
/// </summary>
public interface IServerStartup
{
    /// <summary>
    /// Gets a value indicating whether Jellyfin's startup has completed.
    /// </summary>
    bool HasCompleted { get; }
}
