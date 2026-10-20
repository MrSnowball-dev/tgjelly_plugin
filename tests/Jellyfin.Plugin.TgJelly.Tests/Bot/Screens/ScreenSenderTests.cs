using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot.Screens;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Telegram.Bot.Types.ReplyMarkups;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Bot.Screens;

public sealed class ScreenSenderTests : IDisposable
{
    private static readonly Screen Hello = new("<b>Hi</b>", new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("Go", "1|m")));
    private readonly BotTestHarness _h = new();
    private readonly ScreenSender _sender;

    public ScreenSenderTests()
    {
        _h.Connect();
        _sender = new ScreenSender(_h.Connection);
    }

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task NoId_Sends()
    {
        var id = await _sender.ShowAsync(42, Hello, null, TestContext.Current.CancellationToken);

        var call = Assert.Single(_h.Api.CallsTo("sendMessage"));
        Assert.Equal("<b>Hi</b>", (string)call.Body["text"]!);
        Assert.Equal("1|m", (string)call.Body["reply_markup"]!["inline_keyboard"]![0]![0]!["callback_data"]!);
        Assert.True(id > 0);
    }

    [Fact]
    public async Task Edit_Edits()
    {
        var id = await _sender.ShowAsync(42, Hello, 77, TestContext.Current.CancellationToken);

        Assert.Equal(77, id);
        Assert.Equal(77, (int)Assert.Single(_h.Api.CallsTo("editMessageText")).Body["message_id"]!);
        Assert.Empty(_h.Api.CallsTo("sendMessage"));
    }

    [Theory]
    [InlineData("Bad Request: message to edit not found")]
    [InlineData("Bad Request: message can't be edited")]
    public async Task EditFails_SendsNew_AndReportsTheNewId(string description)
    {
        _h.Api.On("editMessageText", _ => FakeBotApi.Error(400, description));

        var id = await _sender.ShowAsync(42, Hello, 77, TestContext.Current.CancellationToken);

        Assert.Single(_h.Api.CallsTo("sendMessage"));
        Assert.NotEqual(77, id);
    }

    [Fact]
    public async Task NotModified_IsSuccess()
    {
        _h.Api.On("editMessageText", _ => FakeBotApi.Error(400, "Bad Request: message is not modified: specified new message content and reply markup are exactly the same"));

        var id = await _sender.ShowAsync(42, Hello, 77, TestContext.Current.CancellationToken);

        Assert.Equal(77, id);
        Assert.Empty(_h.Api.CallsTo("sendMessage"));
    }
}
