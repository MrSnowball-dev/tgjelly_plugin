using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// <c>POST TgJelly/Token</c> (spec §6.1, §11.2).
/// </summary>
public sealed class TokenRequest
{
    /// <summary>Gets or sets the bot token. Never logged or returned.</summary>
    [Required]
    public string Token { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the user confirmed switching to a different bot.</summary>
    public bool ConfirmSwitch { get; set; }
}
