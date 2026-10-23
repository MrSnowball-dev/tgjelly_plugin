using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Api;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Compat;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Api;

public sealed class BotControllerTests : IAsyncLifetime
{
    private readonly BotTestHarness _h = new();
    private readonly FakeSupervisorControl _supervisor = new();
    private readonly BotTokenService _tokens;
    private BotController _controller = null!;
    private InstanceGate _gate = null!;

    public BotControllerTests()
    {
        _tokens = new BotTokenService(new FakeClientFactory(_h.Api), _h.Secrets, _h.State, _h.Settings, _supervisor, _h.Strings, _h.Time, NullLogger<BotTokenService>.Instance);
    }

    private static CompatResult Compatible => CompatGuard.Evaluate(new Version(BuildInfo.TargetJellyfinMajor, 0, 0, 0));

    public async ValueTask InitializeAsync()
    {
        _gate = await _h.StartOwnedGateAsync();
        _controller = new BotController(_tokens, _h.Strings, _gate, _h.State, Compatible);
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
    public async Task DeleteToken_CallsDisconnectAsync()
    {
        _h.Secrets.Save(new BotSecrets { BotToken = FakeBotApi.Token });

        Assert.IsType<NoContentResult>(await _controller.DeleteToken(false, CancellationToken.None));

        Assert.Equal(string.Empty, _h.Secrets.Load().BotToken);
        Assert.Equal(1, _supervisor.Restarts);
    }

    [Fact]
    public async Task DeleteToken_RemoveDestinations_PassesTheFlagOn()
    {
        _h.AddDestination(-1001);
        _h.Secrets.Save(new BotSecrets { BotToken = FakeBotApi.Token });

        Assert.IsType<NoContentResult>(await _controller.DeleteToken(true, CancellationToken.None));

        Assert.Empty(_h.Settings.Current.Destinations);
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

    [Fact]
    public async Task PostToken_BeforeOwnership_Is503_AndStoresNothing()
    {
        using var gate = new InstanceGate(_h.Paths, _h.Time, NullLogger<InstanceGate>.Instance); // not started: not the owner
        var controller = new BotController(_tokens, _h.Strings, gate, _h.State, Compatible);

        var result = await controller.PostToken(new TokenRequest { Token = FakeBotApi.Token }, CancellationToken.None);

        Assert.Equal(503, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        Assert.False(System.IO.File.Exists(_h.Paths.Secrets));
        Assert.Empty(_h.Api.Calls);
        Assert.Equal(503, Assert.IsType<ObjectResult>(await controller.DeleteToken(false, CancellationToken.None)).StatusCode);
    }

    [Fact]
    public async Task PostToken_OnAnIncompatibleBuild_Is409_AndNeverCallsTelegram()
    {
        var controller = new BotController(_tokens, _h.Strings, _gate, _h.State, CompatGuard.Evaluate(new Version(BuildInfo.TargetJellyfinMajor + 1, 0, 0, 0)));

        var result = await controller.PostToken(new TokenRequest { Token = FakeBotApi.Token }, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Empty(_h.Api.Calls);
    }

    public ValueTask DisposeAsync()
    {
        _tokens.Dispose();
        _h.Dispose();
        return ValueTask.CompletedTask;
    }
}
