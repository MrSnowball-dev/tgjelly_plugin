using System;

namespace Jellyfin.Plugin.TgJelly.Bot;

/// <summary>
/// The Bot API base URL override from <c>TGJELLY_BOT_API_BASE_URL</c>, read once at startup and used by
/// the integration tests. Never part of settings, the API or any UI.
/// </summary>
/// <param name="BaseUrl">The override, or <c>null</c> for the real Bot API.</param>
public sealed record BotApiEnvironment(string? BaseUrl)
{
    /// <summary>
    /// The environment variable's name.
    /// </summary>
    public const string VariableName = "TGJELLY_BOT_API_BASE_URL";

    /// <summary>
    /// Reads the environment variable.
    /// </summary>
    /// <returns>The environment.</returns>
    public static BotApiEnvironment FromEnvironment() => FromValue(Environment.GetEnvironmentVariable(VariableName));

    /// <summary>
    /// Builds the environment from a raw value; blank means unset (Telegram.Bot switches to local-server mode for any
    /// non-null base URL, and throws for an empty one).
    /// </summary>
    /// <param name="value">The raw value.</param>
    /// <returns>The environment.</returns>
    public static BotApiEnvironment FromValue(string? value) => new(string.IsNullOrWhiteSpace(value) ? null : value.Trim());
}
