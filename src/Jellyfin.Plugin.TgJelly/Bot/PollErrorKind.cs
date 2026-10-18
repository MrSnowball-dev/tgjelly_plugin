namespace Jellyfin.Plugin.TgJelly.Bot;

/// <summary>
/// What a failed <c>getMe</c> or <c>getUpdates</c> means (spec §9.2 step 4).
/// </summary>
public enum PollErrorKind
{
    /// <summary>401: the token was revoked or is wrong.</summary>
    TokenRejected,

    /// <summary>409 "terminated by other getUpdates request": another app polls this bot.</summary>
    ConflictingInstance,

    /// <summary>409 about a webhook.</summary>
    WebhookConflict,

    /// <summary>429.</summary>
    RateLimited,

    /// <summary>Any other Bot API error.</summary>
    ApiError,

    /// <summary>A network failure.</summary>
    Unreachable,
}
