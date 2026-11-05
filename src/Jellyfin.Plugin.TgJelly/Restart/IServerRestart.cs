namespace Jellyfin.Plugin.TgJelly.Restart;

/// <summary>
/// Jellyfin's pending-restart flag and its restart, as the dashboard's Restart button uses them.
/// </summary>
public interface IServerRestart
{
    /// <summary>Gets a value indicating whether Jellyfin needs a restart.</summary>
    bool IsPending { get; }

    /// <summary>Asks Jellyfin to restart; it stops shortly after this returns.</summary>
    void Restart();
}
