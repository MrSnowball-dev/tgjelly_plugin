using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.Restart;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Telegram.Bot;
using Telegram.Bot.Types;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Restart;

public sealed class RestartButtonTests : IDisposable
{
    private const long Group = -1001;
    private readonly BotTestHarness _h = new();
    private readonly IServerRestart _server = Substitute.For<IServerRestart>();
    private readonly CancellationTokenSource _stopping = new();
    private readonly RestartButton _button;

    public RestartButtonTests()
    {
        _h.Connect();
        _h.AddDestination(Group, events: BotTestHarness.AdminEvents);
        _server.IsPending.Returns(true);
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(_stopping.Token);
        _button = new RestartButton(_h.Connection, _h.Settings, _h.State, _server, new FakeServerInfo(), new Formatter(_h.Strings), _h.Time, lifetime, NullLogger<RestartButton>.Instance);
    }

    public void Dispose()
    {
        _stopping.Dispose();
        _h.Dispose();
    }

    [Fact]
    public async Task NotAnAdmin_IsRefused_AndNothingRestarts()
    {
        _h.Api.SetMember(Group, FakeBotApi.Member("member"));

        await _button.OnPressAsync(Press(), CancellationToken.None);

        _server.DidNotReceive().Restart();
        Assert.Equal(_h.Strings.Get("restart.notAdmin"), (string?)Assert.Single(_h.Api.CallsTo("answerCallbackQuery")).Body["text"]);
        Assert.Empty(_h.Api.CallsTo("editMessageText"));
        Assert.Null(_h.State.Read(s => s.PendingRestart));
    }

    [Fact]
    public async Task AdminPress_SaysRestarting_KeepsWhichMessage_AndRestarts()
    {
        _h.Api.SetMember(Group, FakeBotApi.Member("administrator"));

        await _button.OnPressAsync(Press(), CancellationToken.None);

        _server.Received(1).Restart();
        var edit = Assert.Single(_h.Api.CallsTo("editMessageText"));
        Assert.Equal("🔄 Restarting Home…", (string?)edit.Body["text"]);
        Assert.Null(edit.Body["reply_markup"]);
        var pending = _h.State.Read(s => s.PendingRestart);
        Assert.NotNull(pending);
        Assert.Equal((Group, 55, false), (pending.ChatId, pending.MessageId, pending.Photo));

        // Jellyfin stopping is the restart: the timeout then reports nothing.
        await _stopping.CancelAsync();
        await _button.Watch;
        Assert.Single(_h.Api.CallsTo("editMessageText"));
    }

    [Fact]
    public async Task RefusedRestart_ShowsTheError_WithTheButtonBack()
    {
        _server.When(s => s.Restart()).Do(_ => throw new InvalidOperationException("not allowed now"));

        await _button.OnPressAsync(Press(), CancellationToken.None);

        var failed = _h.Api.CallsTo("editMessageText").Last();
        Assert.Equal("⚠️ Restart of Home failed: not allowed now", (string?)failed.Body["text"]);
        Assert.Equal(RestartButton.Data, (string?)failed.Body["reply_markup"]!["inline_keyboard"]![0]![0]!["callback_data"]);
        Assert.Null(_h.State.Read(s => s.PendingRestart));
    }

    [Fact]
    public async Task StillRunningAfterTheTimeout_ShowsTheError()
    {
        _h.Settings.Update(null, c => c.Restart.TimeoutSeconds = 60);

        await _button.OnPressAsync(Press(), CancellationToken.None);
        _h.Time.Advance(TimeSpan.FromSeconds(60));
        await _button.Watch;

        var failed = _h.Api.CallsTo("editMessageText").Last();
        Assert.Equal("⚠️ Restart of Home failed: Jellyfin was still running 60 seconds after Restart was pressed", (string?)failed.Body["text"]);
        Assert.Null(_h.State.Read(s => s.PendingRestart));
    }

    [Fact]
    public async Task NoRestartPendingAnyMore_RemovesTheButton()
    {
        _server.IsPending.Returns(false);

        await _button.OnPressAsync(Press(), CancellationToken.None);

        _server.DidNotReceive().Restart();
        Assert.Equal(_h.Strings.Get("restart.notNeeded"), (string?)Assert.Single(_h.Api.CallsTo("answerCallbackQuery")).Body["text"]);
        Assert.Single(_h.Api.CallsTo("editMessageReplyMarkup"));
    }

    private static CallbackQuery Press()
        => System.Text.Json.JsonSerializer.Deserialize<CallbackQuery>(
            """{"id":"cb1","from":{"id":42,"is_bot":false,"first_name":"Ann"},"chat_instance":"1","data":"1|rs","message":{"message_id":55,"date":1760000000,"chat":{"id":-1001,"type":"supergroup","title":"Admins"},"text":"Jellyfin needs a restart"}}""",
            JsonBotAPI.Options)!;
}
