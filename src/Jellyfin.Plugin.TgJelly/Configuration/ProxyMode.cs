namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// How the bot and the update checker reach the internet (spec §9.1).
/// </summary>
public enum ProxyMode
{
    /// <summary>.NET default: follows HTTP_PROXY, HTTPS_PROXY and ALL_PROXY.</summary>
    System,

    /// <summary>No proxy at all.</summary>
    None,

    /// <summary>An HTTP(S) proxy.</summary>
    Http,

    /// <summary>A SOCKS5 proxy.</summary>
    Socks5,
}
