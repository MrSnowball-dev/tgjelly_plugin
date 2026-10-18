namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// The Telegram chat type of a destination.
/// </summary>
public enum ChatKind
{
    /// <summary>A private chat with the bot.</summary>
    Private,

    /// <summary>A basic group.</summary>
    Group,

    /// <summary>A supergroup (possibly a forum).</summary>
    Supergroup,

    /// <summary>A channel.</summary>
    Channel,
}
