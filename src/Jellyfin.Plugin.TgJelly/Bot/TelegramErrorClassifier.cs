using System;
using Jellyfin.Plugin.TgJelly.Logging;
using Telegram.Bot.Exceptions;

namespace Jellyfin.Plugin.TgJelly.Bot;

/// <summary>
/// Classifies Telegram errors (spec §9.4, Appendix A). Descriptions are matched as case-insensitive substrings, and
/// the first matching row wins, so wording changes on Telegram's side degrade gracefully.
/// <see cref="OperationCanceledException"/> is never passed here; callers let it propagate.
/// </summary>
public static class TelegramErrorClassifier
{
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(5);

    private static readonly string[] LostPatterns = ["kicked", "not a member", "blocked", "deactivated", "was deleted", "can't initiate conversation"];
    private static readonly string[] RightsPatterns = ["need administrator rights", "not enough rights to send text messages", "have no write access"];
    private static readonly string[] ParsePatterns = ["can't parse entities", "unsupported start tag", "unmatched end tag", "can't find end tag", "byte offset"];
    private static readonly string[] ButtonPatterns = ["inline keyboard button", "button_url_invalid"];
    private static readonly string[] PhotoPatterns = ["photo_", "image_process_failed", "wrong file", "photo_invalid"];
    private static readonly string[] LengthPatterns = ["message is too long", "caption is too long"];
    private static readonly string[] EditPatterns = ["message to edit not found", "can't be edited", "there is no text in the message to edit"];

    /// <summary>
    /// Classifies a failed send or edit.
    /// </summary>
    /// <param name="exception">The exception.</param>
    /// <returns>The classification.</returns>
    public static DeliveryError ClassifyDelivery(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is not ApiRequestException api)
        {
            return new DeliveryError(exception is RequestException ? DeliveryErrorKind.Transport : DeliveryErrorKind.Other, SecretRedactor.Redact(exception.Message));
        }

        var d = api.Message;
        var code = api.ErrorCode;
        var kind = code switch
        {
            400 when Has(d, "not enough rights to send") && !Has(d, "text messages") => DeliveryErrorKind.PhotosBlocked,
            403 when HasAny(d, LostPatterns) => DeliveryErrorKind.Lost,
            400 when Has(d, "chat not found") => DeliveryErrorKind.Lost,
            400 when HasAny(d, RightsPatterns) => DeliveryErrorKind.NeedsPermissions,
            400 when Has(d, "message thread not found") => DeliveryErrorKind.TopicMissing,
            400 when api.Parameters?.MigrateToChatId is not null => DeliveryErrorKind.Migrated,
            400 when HasAny(d, ParsePatterns) => DeliveryErrorKind.ParseError,
            400 when HasAny(d, ButtonPatterns) => DeliveryErrorKind.BadButton,
            400 when HasAny(d, PhotoPatterns) => DeliveryErrorKind.BadPhoto,
            400 when HasAny(d, LengthPatterns) => DeliveryErrorKind.TooLong,
            400 when HasAny(d, EditPatterns) => DeliveryErrorKind.EditTargetGone,
            400 when Has(d, "message is not modified") => DeliveryErrorKind.NotModified,
            401 => DeliveryErrorKind.Unauthorized,
            429 => DeliveryErrorKind.RateLimited,
            >= 500 => DeliveryErrorKind.ServerError,
            _ => DeliveryErrorKind.Other,
        };

        return new DeliveryError(
            kind,
            SecretRedactor.Redact(d),
            kind == DeliveryErrorKind.RateLimited ? RetryAfter(api) : null,
            kind == DeliveryErrorKind.Migrated ? api.Parameters!.MigrateToChatId : null);
    }

    /// <summary>
    /// Classifies a failed <c>getMe</c>, <c>getWebhookInfo</c> or <c>getUpdates</c>.
    /// </summary>
    /// <param name="exception">The exception.</param>
    /// <returns>The classification.</returns>
    public static PollError ClassifyPoll(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is not ApiRequestException api)
        {
            return new PollError(exception is RequestException ? PollErrorKind.Unreachable : PollErrorKind.ApiError, SecretRedactor.Redact(exception.Message));
        }

        var kind = api.ErrorCode switch
        {
            401 => PollErrorKind.TokenRejected,
            409 when Has(api.Message, "terminated by other getUpdates") => PollErrorKind.ConflictingInstance,
            409 => PollErrorKind.WebhookConflict,
            429 => PollErrorKind.RateLimited,
            _ => PollErrorKind.ApiError,
        };

        return new PollError(kind, SecretRedactor.Redact(api.Message), kind == PollErrorKind.RateLimited ? RetryAfter(api) : null);
    }

    private static TimeSpan RetryAfter(ApiRequestException api)
        => api.Parameters?.RetryAfter is { } seconds ? TimeSpan.FromSeconds(seconds) : DefaultRetryAfter;

    private static bool Has(string description, string pattern) => description.Contains(pattern, StringComparison.OrdinalIgnoreCase);

    private static bool HasAny(string description, string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            if (Has(description, pattern))
            {
                return true;
            }
        }

        return false;
    }
}
