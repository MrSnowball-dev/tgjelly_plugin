using System.Text;

namespace Jellyfin.Plugin.TgJelly.State;

/// <summary>
/// The contents of secrets.json. Never returned by any API, and never printed: <see cref="object.ToString"/>
/// shows only whether each value is set.
/// </summary>
public sealed record BotSecrets
{
    /// <summary>Gets an empty set of secrets.</summary>
    public static BotSecrets Empty { get; } = new();

    /// <summary>Gets the Telegram bot token.</summary>
    public string BotToken { get; init; } = string.Empty;

    /// <summary>Gets the proxy username.</summary>
    public string ProxyUsername { get; init; } = string.Empty;

    /// <summary>Gets the proxy password.</summary>
    public string ProxyPassword { get; init; } = string.Empty;

    // Replaces the compiler-generated member list, which would print the token. The record's ToString calls it.
#pragma warning disable IDE0051
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("HasBotToken = ").Append(BotToken.Length > 0)
            .Append(", HasProxyUsername = ").Append(ProxyUsername.Length > 0)
            .Append(", HasProxyPassword = ").Append(ProxyPassword.Length > 0);
        return true;
    }
#pragma warning restore IDE0051
}
