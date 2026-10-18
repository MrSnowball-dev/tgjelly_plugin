using System;
using Jellyfin.Plugin.TgJelly.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Bot;

public class TelegramErrorClassifierTests
{
    public static TheoryData<int, string, DeliveryErrorKind> Delivery => new()
    {
        { 400, "Bad Request: not enough rights to send photos to the chat", DeliveryErrorKind.PhotosBlocked },
        { 400, "Bad Request: not enough rights to send text messages to the chat", DeliveryErrorKind.NeedsPermissions },
        { 403, "Forbidden: bot was blocked by the user", DeliveryErrorKind.Lost },
        { 403, "Forbidden: user is deactivated", DeliveryErrorKind.Lost },
        { 403, "Forbidden: bot was kicked from the group chat", DeliveryErrorKind.Lost },
        { 403, "Forbidden: bot was kicked from the supergroup chat", DeliveryErrorKind.Lost },
        { 403, "Forbidden: bot was kicked from the channel chat", DeliveryErrorKind.Lost },
        { 403, "Forbidden: bot is not a member of the group chat", DeliveryErrorKind.Lost },
        { 403, "Forbidden: bot is not a member of the supergroup chat", DeliveryErrorKind.Lost },
        { 403, "Forbidden: bot is not a member of the channel chat", DeliveryErrorKind.Lost },
        { 403, "Forbidden: the group chat was deleted", DeliveryErrorKind.Lost },
        { 403, "Forbidden: bot can't initiate conversation with a user", DeliveryErrorKind.Lost },
        { 400, "Bad Request: chat not found", DeliveryErrorKind.Lost },
        { 400, "Bad Request: private chat not found", DeliveryErrorKind.Lost },
        { 400, "Bad Request: need administrator rights in the channel chat", DeliveryErrorKind.NeedsPermissions },
        { 400, "Bad Request: have no write access to the chat", DeliveryErrorKind.NeedsPermissions },
        { 400, "Bad Request: message thread not found", DeliveryErrorKind.TopicMissing },
        { 400, "Bad Request: can't parse entities: Unsupported start tag \"x\" at byte offset 12", DeliveryErrorKind.ParseError },
        { 400, "Bad Request: can't parse entities: Can't find end tag corresponding to start tag \"b\"", DeliveryErrorKind.ParseError },
        { 400, "Bad Request: inline keyboard button URL 'http://jellyfin/web' is invalid: Wrong HTTP URL", DeliveryErrorKind.BadButton },
        { 400, "Bad Request: BUTTON_URL_INVALID", DeliveryErrorKind.BadButton },
        { 400, "Bad Request: IMAGE_PROCESS_FAILED", DeliveryErrorKind.BadPhoto },
        { 400, "Bad Request: wrong file identifier/HTTP URL specified", DeliveryErrorKind.BadPhoto },
        { 400, "Bad Request: PHOTO_INVALID_DIMENSIONS", DeliveryErrorKind.BadPhoto },
        { 400, "Bad Request: message is too long", DeliveryErrorKind.TooLong },
        { 400, "Bad Request: message caption is too long", DeliveryErrorKind.TooLong },
        { 400, "Bad Request: message to edit not found", DeliveryErrorKind.EditTargetGone },
        { 400, "Bad Request: message can't be edited", DeliveryErrorKind.EditTargetGone },
        { 400, "Bad Request: there is no text in the message to edit", DeliveryErrorKind.EditTargetGone },
        { 400, "Bad Request: message is not modified: specified new message content and reply markup are exactly the same as a current content and reply markup of the message", DeliveryErrorKind.NotModified },
        { 429, "Too Many Requests: retry after 9", DeliveryErrorKind.RateLimited },
        { 502, "Bad Gateway", DeliveryErrorKind.ServerError },
        { 400, "Bad Request: something new", DeliveryErrorKind.Other },
    };

    [Theory]
    [MemberData(nameof(Delivery))]
    public void Delivery_FirstMatchingRowWins_CaseInsensitive(int code, string description, DeliveryErrorKind expected)
    {
        Assert.Equal(expected, TelegramErrorClassifier.ClassifyDelivery(Api(code, description)).Kind);
        Assert.Equal(expected, TelegramErrorClassifier.ClassifyDelivery(Api(code, description.ToUpperInvariant())).Kind);
    }

    [Fact]
    public void Migration_CarriesTheNewChatId()
    {
        var error = TelegramErrorClassifier.ClassifyDelivery(Api(400, "Bad Request: group chat was upgraded to a supergroup chat", migrateTo: -1009876));
        Assert.Equal((DeliveryErrorKind.Migrated, -1009876L), (error.Kind, error.MigrateToChatId));
    }

    [Fact]
    public void RateLimit_CarriesRetryAfter() =>
        Assert.Equal(TimeSpan.FromSeconds(9), TelegramErrorClassifier.ClassifyDelivery(Api(429, "Too Many Requests: retry after 9", retryAfter: 9)).RetryAfter);

    [Theory]
    [InlineData("Bot API Request timed out")]
    [InlineData("Bot API Service Failure: HttpRequestException: Connection refused")]
    public void TransportErrors_AreTransport(string message)
        => Assert.Equal(DeliveryErrorKind.Transport, TelegramErrorClassifier.ClassifyDelivery(new RequestException(message)).Kind);

    [Theory]
    [InlineData(401, "Unauthorized", PollErrorKind.TokenRejected)]
    [InlineData(409, "Conflict: terminated by other getUpdates request; make sure that only one bot instance is running", PollErrorKind.ConflictingInstance)]
    [InlineData(409, "Conflict: can't use getUpdates method while webhook is active; use deleteWebhook to delete the webhook first", PollErrorKind.WebhookConflict)]
    [InlineData(409, "Conflict: terminated by setWebhook request", PollErrorKind.WebhookConflict)]
    [InlineData(429, "Too Many Requests: retry after 3", PollErrorKind.RateLimited)]
    [InlineData(500, "Internal Server Error", PollErrorKind.ApiError)]
    public void Poll_Errors(int code, string description, PollErrorKind expected)
        => Assert.Equal(expected, TelegramErrorClassifier.ClassifyPoll(Api(code, description)).Kind);

    private static ApiRequestException Api(int code, string description, long? migrateTo = null, int? retryAfter = null)
        => new(description, code, new ResponseParameters { MigrateToChatId = migrateTo, RetryAfter = retryAfter });

    [Fact]
    public void NonTelegramExceptions_AreOther()
        => Assert.Equal(DeliveryErrorKind.Other, TelegramErrorClassifier.ClassifyDelivery(new InvalidOperationException("x")).Kind);

    [Fact]
    public void PollTransport_IsUnreachable()
        => Assert.Equal(PollErrorKind.Unreachable, TelegramErrorClassifier.ClassifyPoll(new RequestException("Bot API Request timed out")).Kind);

    [Fact]
    public void RateLimit_WithoutParameters_Waits5Seconds()
        => Assert.Equal(TimeSpan.FromSeconds(5), TelegramErrorClassifier.ClassifyPoll(new ApiRequestException("Too Many Requests", 429)).RetryAfter);
}
