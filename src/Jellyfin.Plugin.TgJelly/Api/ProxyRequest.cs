using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// <c>PUT TgJelly/Proxy</c>. An omitted username or password keeps the stored one; an empty
/// string clears it.
/// </summary>
public sealed class ProxyRequest
{
    /// <summary>Gets or sets the revision the change is based on.</summary>
    public long ExpectedRevision { get; set; }

    /// <summary>Gets or sets the mode: System, None, Http or Socks5.</summary>
    [Required]
    public string Mode { get; set; } = string.Empty;

    /// <summary>Gets or sets the proxy URL (Http and Socks5).</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the proxy username, or <c>null</c> to keep the stored one.</summary>
    public string? Username { get; set; }

    /// <summary>Gets or sets the proxy password, or <c>null</c> to keep the stored one.</summary>
    public string? Password { get; set; }
}
