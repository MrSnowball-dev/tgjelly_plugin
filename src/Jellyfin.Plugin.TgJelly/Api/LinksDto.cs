namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// The bot's <c>t.me</c> links; empty while no bot username is known.
/// </summary>
public sealed class LinksDto
{
    /// <summary>Gets the link that opens the bot's setup.</summary>
    public string OpenBot { get; init; } = string.Empty;

    /// <summary>Gets the link that starts adding a destination.</summary>
    public string StartAdd { get; init; } = string.Empty;

    /// <summary>Gets the link that adds the bot to a group.</summary>
    public string StartGroup { get; init; } = string.Empty;

    /// <summary>Gets the link that adds the bot to a channel as an admin who can post and edit.</summary>
    public string StartChannel { get; init; } = string.Empty;
}
