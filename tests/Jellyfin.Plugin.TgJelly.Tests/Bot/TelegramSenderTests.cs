using System.IO;
using Jellyfin.Plugin.TgJelly.Bot.Screens;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Bot;

public sealed class TelegramSenderTests : System.IDisposable
{
    private readonly FakeBotApi _api = new();

    private TelegramSender Sender => new(_api.CreateClient());

    [Fact]
    public async Task SendText_UsesHtml_Thread_Reply_Silence_AndNoPreview()
    {
        var ct = TestContext.Current.CancellationToken;
        await Sender.SendTextAsync(new OutgoingText(-1001, 7, "<b>hi</b>", ParseMode.Html, null, 42, Silent: true), ct);

        var body = Assert.Single(_api.CallsTo("sendMessage")).Body;
        Assert.Equal(-1001, (long)body["chat_id"]!);
        Assert.Equal(7, (int)body["message_thread_id"]!);
        Assert.Equal("html", ((string)body["parse_mode"]!).ToLowerInvariant()); // the Bot API ignores case
        Assert.Equal(42, (int)body["reply_parameters"]!["message_id"]!);
        Assert.True((bool)body["reply_parameters"]!["allow_sending_without_reply"]!);
        Assert.True((bool)body["disable_notification"]!);
        Assert.True((bool)body["link_preview_options"]!["is_disabled"]!);
    }

    [Fact]
    public async Task ThreadZero_SendsNoThreadId()
    {
        await Sender.SendTextAsync(new OutgoingText(-1001, 0, "x", ParseMode.None, null, null, false), TestContext.Current.CancellationToken);

        Assert.False(Assert.Single(_api.CallsTo("sendMessage")).Body.ContainsKey("message_thread_id"));
    }

    [Fact]
    public async Task SetCommands_SetsPrivateScope_AndClearsTheDefaultScope()
    {
        await Sender.SetCommandsAsync([new BotCommand("menu", "Open the menu")], TestContext.Current.CancellationToken);

        var calls = _api.CallsTo("setMyCommands").ToArray();
        Assert.Equal(2, calls.Length);
        Assert.Equal("all_private_chats", (string)calls[0].Body["scope"]!["type"]!);
        Assert.Equal("menu", (string)calls[0].Body["commands"]![0]!["command"]!);
        Assert.Equal("default", (string)calls[1].Body["scope"]!["type"]!);
        Assert.Empty(calls[1].Body["commands"]!.AsArray());
    }

    [Fact]
    public async Task SetChannelAdminRights_RequestsPostAndEdit()
    {
        await Sender.SetChannelAdminRightsAsync(TestContext.Current.CancellationToken);

        var body = Assert.Single(_api.CallsTo("setMyDefaultAdministratorRights")).Body;
        Assert.True((bool)body["for_channels"]!);
        Assert.True((bool)body["rights"]!["can_post_messages"]!);
        Assert.True((bool)body["rights"]!["can_edit_messages"]!);
    }

    [Fact]
    public async Task SendPhoto_UploadsTheStream_WithItsFileName()
    {
        using var stream = new MemoryStream([1, 2, 3]);
        var message = await Sender.SendPhotoAsync(
            new OutgoingPhoto(-1001, 0, InputFile.FromStream(stream, "poster.jpg"), "<b>cap</b>", ParseMode.Html, null, null, false),
            TestContext.Current.CancellationToken);

        var body = Assert.Single(_api.CallsTo("sendPhoto")).Body;
        Assert.Equal("attach://0", (string)body["photo"]!);
        Assert.Equal("file:poster.jpg", (string)body["0"]!);
        Assert.Equal("file-1", message.Photo![^1].FileId);
    }

    [Fact]
    public async Task Typing_IsAChatActionInTheThread()
    {
        await Sender.SendTypingAsync(-1001, 7, TestContext.Current.CancellationToken);

        var body = Assert.Single(_api.CallsTo("sendChatAction")).Body;
        Assert.Equal("typing", (string)body["action"]!);
        Assert.Equal(7, (int)body["message_thread_id"]!);
    }

    [Fact]
    public async Task ApiErrors_ArriveAsApiRequestException_WithParameters()
    {
        _api.On("sendMessage", _ => FakeBotApi.Error(429, "Too Many Requests: retry after 7", """{"retry_after":7}"""));

        var ex = await Assert.ThrowsAsync<ApiRequestException>(() =>
            Sender.SendTextAsync(new OutgoingText(1, 0, "x", ParseMode.None, null, null, false), TestContext.Current.CancellationToken));

        Assert.Equal(429, ex.ErrorCode);
        Assert.Equal(7, ex.Parameters!.RetryAfter);
        Assert.Single(_api.CallsTo("sendMessage")); // RetryThreshold = 0: Telegram.Bot did not retry
    }

    private static OutgoingText WithIcon()
        => new(
            42,
            0,
            "<b><tg-emoji emoji-id=\"5875271289605722323\">🏠</tg-emoji> Menu</b>",
            ParseMode.Html,
            new InlineKeyboardMarkup(new InlineKeyboardButton("Settings") { CallbackData = "1|s", IconCustomEmojiId = Icons.Settings }),
            null,
            false);

    [Fact]
    public async Task RejectedIcons_AreDroppedAndResent_ThenSkipped()
    {
        var ct = TestContext.Current.CancellationToken;
        var message = FakeBotApi.Ok("""{"message_id":9,"date":0,"chat":{"id":42,"type":"private"}}""");
        _api.OnSequence("sendMessage", FakeBotApi.Error(400, "Bad Request: custom emoji entities can't be used"), message, message);
        var sender = Sender;

        await sender.SendTextAsync(WithIcon(), ct);

        var calls = _api.CallsTo("sendMessage").ToArray();
        Assert.Equal(2, calls.Length);
        Assert.Contains("tg-emoji", (string)calls[0].Body["text"]!, System.StringComparison.Ordinal);
        Assert.Contains("icon_custom_emoji_id", calls[0].Body["reply_markup"]!.ToJsonString(), System.StringComparison.Ordinal);
        Assert.Equal("<b>🏠 Menu</b>", (string)calls[1].Body["text"]!);
        Assert.DoesNotContain("icon_custom_emoji_id", calls[1].Body["reply_markup"]!.ToJsonString(), System.StringComparison.Ordinal);
        Assert.Equal("1|s", (string)calls[1].Body["reply_markup"]!["inline_keyboard"]![0]![0]!["callback_data"]!);

        await sender.SendTextAsync(WithIcon(), ct);

        var third = _api.CallsTo("sendMessage").ElementAt(2);
        Assert.Equal("<b>🏠 Menu</b>", (string)third.Body["text"]!);
        Assert.DoesNotContain("icon_custom_emoji_id", third.Body["reply_markup"]!.ToJsonString(), System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectedIcons_InAnEdit_AreDroppedAndResent()
    {
        _api.OnSequence(
            "editMessageText",
            FakeBotApi.Error(400, "Bad Request: DOCUMENT_INVALID emoji"),
            FakeBotApi.Ok("""{"message_id":5,"date":0,"chat":{"id":42,"type":"private"}}"""));
        var text = WithIcon();

        await Sender.EditTextAsync(new MessageEdit(42, 5, text.Text, ParseMode.Html, text.Buttons), TestContext.Current.CancellationToken);

        var calls = _api.CallsTo("editMessageText").ToArray();
        Assert.Equal(2, calls.Length);
        Assert.Equal("<b>🏠 Menu</b>", (string)calls[1].Body["text"]!);
        Assert.DoesNotContain("icon_custom_emoji_id", calls[1].Body["reply_markup"]!.ToJsonString(), System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task OtherBadRequests_AreNotRetried()
    {
        _api.On("sendMessage", _ => FakeBotApi.Error(400, "Bad Request: chat not found"));

        await Assert.ThrowsAsync<ApiRequestException>(() => Sender.SendTextAsync(WithIcon(), TestContext.Current.CancellationToken));

        Assert.Single(_api.CallsTo("sendMessage"));
    }

    [Fact]
    public async Task MessagesWithoutIcons_AreNotRetried()
    {
        _api.On("sendMessage", _ => FakeBotApi.Error(400, "Bad Request: custom emoji entities can't be used"));

        await Assert.ThrowsAsync<ApiRequestException>(() =>
            Sender.SendTextAsync(new OutgoingText(42, 0, "plain", ParseMode.None, null, null, false), TestContext.Current.CancellationToken));

        Assert.Single(_api.CallsTo("sendMessage"));
    }

    [Fact]
    public async Task DeleteWebhook_KeepsPendingUpdates()
    {
        await Sender.DeleteWebhookAsync(TestContext.Current.CancellationToken);

        var body = Assert.Single(_api.CallsTo("deleteWebhook")).Body;
        Assert.False(body.ContainsKey("drop_pending_updates") && (bool)body["drop_pending_updates"]!);
    }

    [Fact]
    public async Task SendPrompt_SendsTheReplyKeyboard()
    {
        var keyboard = new ReplyKeyboardMarkup(
            KeyboardButton.WithRequestChat("Pick a channel", new KeyboardButtonRequestChat { RequestId = 7, ChatIsChannel = true, RequestTitle = true }))
        {
            ResizeKeyboard = true,
            OneTimeKeyboard = true,
        };

        await Sender.SendPromptAsync(new OutgoingPrompt(42, "<b>Pick</b>", keyboard), TestContext.Current.CancellationToken);

        var body = Assert.Single(_api.CallsTo("sendMessage")).Body;
        Assert.Equal("html", ((string)body["parse_mode"]!).ToLowerInvariant()); // the Bot API ignores case
        Assert.Equal(7, (int)body["reply_markup"]!["keyboard"]![0]![0]!["request_chat"]!["request_id"]!);
    }

    [Fact]
    public async Task DeleteMessage_TreatsNotFoundAsFalse()
    {
        var ct = TestContext.Current.CancellationToken;
        _api.On("deleteMessage", _ => FakeBotApi.Error(400, "Bad Request: message to delete not found"));
        Assert.False(await Sender.DeleteMessageAsync(42, 5, ct));

        _api.On("deleteMessage", _ => FakeBotApi.Error(400, "Bad Request: chat not found"));
        await Assert.ThrowsAsync<ApiRequestException>(() => Sender.DeleteMessageAsync(42, 5, ct));
    }

    [Fact]
    public async Task DeleteMessage_ReturnsTrueWhenDeleted()
    {
        Assert.True(await Sender.DeleteMessageAsync(42, 5, TestContext.Current.CancellationToken));

        var body = Assert.Single(_api.CallsTo("deleteMessage")).Body;
        Assert.Equal(5, (int)body["message_id"]!);
    }

    [Fact]
    public async Task CreateForumTopic_ReturnsTheThreadId()
    {
        var threadId = await Sender.CreateForumTopicAsync(-1001, "Jellyfin", TestContext.Current.CancellationToken);

        Assert.Equal(101, threadId);
        Assert.Equal("Jellyfin", (string)Assert.Single(_api.CallsTo("createForumTopic")).Body["name"]!);
    }

    public void Dispose() => _api.Dispose();
}
