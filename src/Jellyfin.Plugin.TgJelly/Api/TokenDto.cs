namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// The bot token, shown to an administrator on request (GET Token).
/// </summary>
public sealed class TokenDto
{
    /// <summary>Gets the token.</summary>
    public string Token { get; init; } = string.Empty;
}
