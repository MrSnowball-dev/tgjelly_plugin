namespace Jellyfin.Plugin.TgJelly.Bot;

/// <summary>
/// The bot connection's state, shown on the Status page.
/// </summary>
public enum ConnectionState
{
    /// <summary>No token is stored.</summary>
    NoToken,

    /// <summary>A previous instance still holds <c>state.lock</c>.</summary>
    WaitingForInstance,

    /// <summary>Jellyfin hasn't finished starting.</summary>
    WaitingForStartup,

    /// <summary>Connecting: <c>getMe</c>, the webhook check, the first poll.</summary>
    Connecting,

    /// <summary>The last poll succeeded.</summary>
    Connected,

    /// <summary>Telegram rejected the token (401).</summary>
    TokenRejected,

    /// <summary>Another app set a webhook for this bot.</summary>
    WebhookConflict,

    /// <summary>Another app polls this bot.</summary>
    ConflictingInstance,

    /// <summary>Telegram can't be reached.</summary>
    Unreachable,

    /// <summary><c>TGJELLY_BOT_API_BASE_URL</c> isn't a valid URL.</summary>
    InvalidBaseUrl,
}
