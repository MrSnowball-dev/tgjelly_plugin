namespace Jellyfin.Plugin.TgJelly.Bot;

/// <summary>
/// The outcome of saving a bot token (spec §6.1).
/// </summary>
public enum TokenSaveOutcome
{
    /// <summary>Saved; the supervisor connects with it.</summary>
    Connected,

    /// <summary>A different bot while destinations exist; nothing changed until the switch is confirmed.</summary>
    NeedsConfirm,

    /// <summary>The text isn't a bot token.</summary>
    FormatInvalid,

    /// <summary>Telegram rejected the token.</summary>
    Rejected,

    /// <summary>Telegram can't be reached; the page opens the proxy settings.</summary>
    Unreachable,

    /// <summary>Another error.</summary>
    Failed,
}
