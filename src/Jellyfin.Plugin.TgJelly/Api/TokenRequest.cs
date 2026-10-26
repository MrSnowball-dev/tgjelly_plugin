using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// <c>POST TgJelly/Token</c>.
/// </summary>
public sealed class TokenRequest
{
    /// <summary>Gets or sets the bot token. Never logged; returned only by GET Token, to an administrator who asks.</summary>
    [Required]
    public string Token { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the user confirmed switching to a different bot.</summary>
    public bool ConfirmSwitch { get; set; }
}
