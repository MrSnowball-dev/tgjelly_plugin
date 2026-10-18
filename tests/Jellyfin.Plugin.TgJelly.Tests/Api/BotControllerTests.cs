using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Api;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Api;

public sealed class BotControllerTests : IDisposable
{
    private readonly BotTestHarness _h = new();
    private readonly FakeSupervisorControl _supervisor = new();
    private readonly BotTokenService _tokens;
    private readonly BotController _controller;

    public BotControllerTests()
    {
        _tokens = new BotTokenService(new FakeClientFactory(_h.Api), _h.Secrets, _h.State, _h.Settings, _supervisor, _h.Strings, _h.Time, NullLogger<BotTokenService>.Instance);
        _controller = new BotController(_tokens, _h.Strings);
    }

    [Fact]
    public async Task PostToken_ReturnsTheOutcomeAs200()
    {
        _h.Api.On("getMe", _ => FakeBotApi.Error(401, "Unauthorized"));

        var rejected = await _controller.PostToken(new TokenRequest { Token = FakeBotApi.Token }, CancellationToken.None);

        Assert.Equal("Rejected", rejected.Value!.Outcome);

        _h.Api.On("getMe", _ => FakeBotApi.Ok("""{"id":123456789,"is_bot":true,"first_name":"b","username":"tgjelly_test_bot"}"""));
        var connected = await _controller.PostToken(new TokenRequest { Token = FakeBotApi.Token }, CancellationToken.None);

        Assert.Equal(("Connected", "tgjelly_test_bot"), (connected.Value!.Outcome, connected.Value.BotUsername));
    }

    [Fact]
    public async Task PostToken_ForADifferentBot_NeedsConfirm()
    {
        _h.AddDestination(-1001);
        _h.State.Update(s => s.Telegram.BotId = 999);

        var result = await _controller.PostToken(new TokenRequest { Token = FakeBotApi.Token }, CancellationToken.None);

        Assert.Equal("NeedsConfirm", result.Value!.Outcome);
        Assert.Contains("different bot", result.Value.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteToken_Returns204_AndDisconnects()
    {
        _h.Secrets.Save(new BotSecrets { BotToken = FakeBotApi.Token });

        Assert.IsType<NoContentResult>(_controller.DeleteToken());

        Assert.Equal(string.Empty, _h.Secrets.Load().BotToken);
        Assert.Equal(1, _supervisor.Restarts);
    }

    [Fact]
    public async Task TakeOver_WithoutAToken_Is409()
    {
        var result = await _controller.TakeOverWebhook(CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(_h.Strings.Get("destination.notConnected"), Assert.IsType<MessageDto>(conflict.Value).Message);
    }

    [Fact]
    public async Task TakeOver_WithAToken_DeletesTheWebhook()
    {
        _h.Secrets.Save(new BotSecrets { BotToken = FakeBotApi.Token });

        Assert.IsType<NoContentResult>(await _controller.TakeOverWebhook(CancellationToken.None));

        Assert.Single(_h.Api.CallsTo("deleteWebhook"));
    }

    public void Dispose()
    {
        _tokens.Dispose();
        _h.Dispose();
    }
}
