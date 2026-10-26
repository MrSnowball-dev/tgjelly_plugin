namespace Jellyfin.Plugin.TgJelly.Bot;

/// <summary>
/// Restarts the bot session, e.g. after a token or proxy change.
/// </summary>
public interface ISupervisorControl
{
    /// <summary>
    /// Cancels the current session; a new one starts with the current token and settings.
    /// </summary>
    void Restart();
}
