namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// The Restart button of "Jellyfin needs a restart": how long a restart may take before it counts as failed.
/// </summary>
public class RestartOptions
{
    /// <summary>The shortest allowed timeout, in seconds.</summary>
    public const int MinTimeoutSeconds = 30;

    /// <summary>The longest allowed timeout, in seconds.</summary>
    public const int MaxTimeoutSeconds = 900;

    /// <summary>
    /// Gets or sets how many seconds after a press Jellyfin may still be running before the message says the restart
    /// failed.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 120;
}
