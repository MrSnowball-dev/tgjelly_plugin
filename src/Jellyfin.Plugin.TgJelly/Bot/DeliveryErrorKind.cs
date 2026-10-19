namespace Jellyfin.Plugin.TgJelly.Bot;

/// <summary>
/// What a failed send or edit means, rows 1–15 of the spec §9.4 table, in order.
/// </summary>
public enum DeliveryErrorKind
{
    /// <summary>Row 1: photos aren't allowed; resend as text.</summary>
    PhotosBlocked,

    /// <summary>Row 2: the bot is gone from the chat.</summary>
    Lost,

    /// <summary>Row 3: the bot lacks rights.</summary>
    NeedsPermissions,

    /// <summary>Row 4: the forum topic is gone.</summary>
    TopicMissing,

    /// <summary>Row 5: the group became a supergroup.</summary>
    Migrated,

    /// <summary>Row 6: Telegram couldn't parse the HTML.</summary>
    ParseError,

    /// <summary>Row 7: a button URL was rejected.</summary>
    BadButton,

    /// <summary>Row 8: the photo was rejected.</summary>
    BadPhoto,

    /// <summary>Row 9: the text or caption is too long.</summary>
    TooLong,

    /// <summary>Row 10: the message to edit is gone or can't be edited.</summary>
    EditTargetGone,

    /// <summary>Row 11: the edit changes nothing; counts as success.</summary>
    NotModified,

    /// <summary>Row 12: too many requests; wait <c>retry_after</c>.</summary>
    RateLimited,

    /// <summary>Row 13: a network failure; retry with backoff.</summary>
    Transport,

    /// <summary>Row 14: a Telegram server error; retry with backoff.</summary>
    ServerError,

    /// <summary>401: the token was revoked; retried with backoff while the poll loop reports TokenRejected.</summary>
    Unauthorized,

    /// <summary>Row 15: anything else; log and drop.</summary>
    Other,
}
