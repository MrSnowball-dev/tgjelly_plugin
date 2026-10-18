namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// The outcome of <c>POST TgJelly/Token</c>; always HTTP 200, the page branches on <see cref="Outcome"/>.
/// </summary>
public sealed class TokenResponse
{
    /// <summary>Gets the outcome: Connected, NeedsConfirm, FormatInvalid, Rejected, Unreachable or Failed.</summary>
    public string Outcome { get; init; } = string.Empty;

    /// <summary>Gets what to show.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Gets the bot's username, when known.</summary>
    public string? BotUsername { get; init; }

    /// <summary>Gets a value indicating whether another app's webhook was removed.</summary>
    public bool WebhookDeleted { get; init; }
}
