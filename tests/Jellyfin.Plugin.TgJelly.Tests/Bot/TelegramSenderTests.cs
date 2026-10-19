using System.IO;
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
