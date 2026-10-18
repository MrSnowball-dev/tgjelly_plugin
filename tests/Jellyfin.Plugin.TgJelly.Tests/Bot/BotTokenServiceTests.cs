using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Logging;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Bot;

public sealed class BotTokenServiceTests : IDisposable
{
    private readonly BotTestHarness _h = new();
    private readonly FakeClientFactory _factory;
    private readonly FakeSupervisorControl _supervisor = new();
    private readonly BotTokenService _service;

    public BotTokenServiceTests()
    {
        _factory = new FakeClientFactory(_h.Api);
        _service = NewService(NullLogger<BotTokenService>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task BadFormat_IsFormatInvalid_AndStoresNothing()
    {
        _factory.Throw = _ => new ArgumentException("Bot token invalid", "token");

        var result = await _service.SaveAsync("nope", true, false, Ct);

        Assert.Equal((TokenSaveOutcome.FormatInvalid, _h.Strings.Get("token.format")), (result.Outcome, result.Message));
        AssertNothingStored();
    }

    [Fact]
    public async Task Http401_IsRejected_AndStoresNothing()
    {
        _h.Api.On("getMe", _ => FakeBotApi.Error(401, "Unauthorized"));

        var result = await _service.SaveAsync(FakeBotApi.Token, true, false, Ct);

        Assert.Equal((TokenSaveOutcome.Rejected, _h.Strings.Get("token.rejected")), (result.Outcome, result.Message));
        AssertNothingStored();
    }

    [Fact]
    public async Task Unreachable_ReturnsUnreachable_AndStoresNothing()
    {
        _h.Api.On("getMe", _ => throw new HttpRequestException("Connection refused"));

        var result = await _service.SaveAsync(FakeBotApi.Token, true, false, Ct);

        Assert.Equal((TokenSaveOutcome.Unreachable, _h.Strings.Get("token.unreachable")), (result.Outcome, result.Message));
        AssertNothingStored();
    }

    [Fact]
    public async Task FirstToken_StoresTokenAndBot_QueuesProfileCalls_AndRestartsTheSupervisor()
    {
        var result = await _service.SaveAsync("  " + FakeBotApi.Token + " ", true, false, Ct);

        Assert.Equal((TokenSaveOutcome.Connected, FakeBotApi.BotUsername, false), (result.Outcome, result.BotUsername, result.WebhookDeleted));
        Assert.Equal(FakeBotApi.Token, _h.Secrets.Load().BotToken);
        Assert.Equal((FakeBotApi.BotId, FakeBotApi.BotUsername), (_h.State.Read(s => s.Telegram.BotId)!.Value, _h.State.Read(s => s.Telegram.BotUsername)));
        Assert.True(_h.State.Read(s => s.Telegram.Profile.SetProfile));
        Assert.Equal(ProfileCalls.AllCalls, _h.State.Read(s => s.Telegram.Profile.PendingCalls.ToArray()));
        Assert.Equal(1, _supervisor.Restarts);
    }

    [Fact]
    public async Task SetProfileOff_QueuesNoProfileCalls()
    {
        await _service.SaveAsync(FakeBotApi.Token, false, false, Ct);

        Assert.False(_h.State.Read(s => s.Telegram.Profile.SetProfile));
        Assert.Empty(_h.State.Read(s => s.Telegram.Profile.PendingCalls));
    }

    [Fact]
    public async Task SameBot_KeepsHealthSentMessagesAndTheOutbox()
    {
        var d = _h.AddDestination(-1001);
        SeedBotScopedState(FakeBotApi.BotId, d.Id);

        var result = await _service.SaveAsync(FakeBotApi.Token, true, false, Ct);

        Assert.Equal(TokenSaveOutcome.Connected, result.Outcome);
        Assert.Equal(HealthStatus.NeedsPermissions, _h.State.Read(s => s.Health[d.Id].Status));
        Assert.Single(_h.State.ReadQueue(q => q.SentMessages));
        Assert.Single(_h.State.ReadQueue(q => q.Outbox));
        Assert.Single(_h.State.ReadQueue(q => q.FileIdCache));
    }

    [Fact]
    public async Task DifferentBot_WithDestinations_NeedsConfirm_AndChangesNothing()
    {
        var a = _h.AddDestination(-1001);
        _h.AddDestination(-1002);
        SeedBotScopedState(999, a.Id);

        var result = await _service.SaveAsync(FakeBotApi.Token, true, false, Ct);

        Assert.Equal(TokenSaveOutcome.NeedsConfirm, result.Outcome);
        Assert.Equal("@tgjelly_test_bot is a different bot. It isn't in your 2 destinations, so they will need to be added again.", result.Message);
        Assert.Equal(string.Empty, _h.Secrets.Load().BotToken);
        Assert.Equal(999, _h.State.Read(s => s.Telegram.BotId));
        Assert.Single(_h.State.ReadQueue(q => q.SentMessages));
        Assert.Equal(0, _supervisor.Restarts);
    }

    [Fact]
    public async Task DifferentBot_Confirmed_ResetsBotScopedState_AndMarksEveryDestinationLost()
    {
        var a = _h.AddDestination(-1001);
        var b = _h.AddDestination(-1002);
        SeedBotScopedState(999, a.Id);
        var revision = _h.Settings.Current.Revision;

        var result = await _service.SaveAsync(FakeBotApi.Token, true, true, Ct);

        Assert.Equal(TokenSaveOutcome.Connected, result.Outcome);
        Assert.Empty(_h.State.ReadQueue(q => q.SentMessages));
        Assert.Empty(_h.State.ReadQueue(q => q.FileIdCache));
        Assert.Single(_h.State.ReadQueue(q => q.Outbox)); // kept, held while Lost
        foreach (var id in new[] { a.Id, b.Id })
        {
            var health = _h.State.Read(s => s.Health[id]);
            Assert.Equal((HealthStatus.Lost, "bot changed: add @tgjelly_test_bot to this chat"), (health.Status, health.LastError));
        }

        Assert.Equal(revision, _h.Settings.Current.Revision);
        Assert.Equal(FakeBotApi.BotId, _h.State.Read(s => s.Telegram.BotId));
    }

    [Fact]
    public async Task DifferentBot_WithoutDestinations_SwitchesWithoutAsking()
    {
        _h.State.Update(s => s.Telegram.BotId = 999);

        var result = await _service.SaveAsync(FakeBotApi.Token, true, false, Ct);

        Assert.Equal(TokenSaveOutcome.Connected, result.Outcome);
        Assert.Equal(FakeBotApi.BotId, _h.State.Read(s => s.Telegram.BotId));
    }

    [Fact]
    public async Task AWebhook_IsDeletedOnSave_AndReported()
    {
        _h.Api.On("getWebhookInfo", _ => FakeBotApi.Ok("""{"url":"https://other.example.com/hook","has_custom_certificate":false,"pending_update_count":0}"""));

        var result = await _service.SaveAsync(FakeBotApi.Token, true, false, Ct);

        Assert.True(result.WebhookDeleted);
        Assert.Single(_h.Api.CallsTo("deleteWebhook"));
    }

    [Fact]
    public async Task Disconnect_ClearsTheTokenAndBotScopedState_KeepsTheBotIdentityAndProxyCredentials()
    {
        var d = _h.AddDestination(-1001);
        await _service.SaveAsync(FakeBotApi.Token, true, false, Ct);
        _h.Secrets.Update(s => s with { ProxyUsername = "u", ProxyPassword = "p" });
        SeedBotScopedState(FakeBotApi.BotId, d.Id);
        var identity = (_h.State.Read(s => s.Telegram.BotId)!.Value, _h.State.Read(s => s.Telegram.BotUsername));

        _service.Disconnect();

        var secrets = _h.Secrets.Load();
        Assert.Equal((string.Empty, "u", "p"), (secrets.BotToken, secrets.ProxyUsername, secrets.ProxyPassword));
        Assert.Empty(_h.State.Read(s => s.Health));
        Assert.Empty(_h.State.ReadQueue(q => q.SentMessages));
        Assert.Empty(_h.State.ReadQueue(q => q.Outbox));
        Assert.Empty(_h.State.ReadQueue(q => q.FileIdCache));
        Assert.Empty(_h.State.Read(s => s.Telegram.Profile.PendingCalls));
        Assert.Equal(identity, (_h.State.Read(s => s.Telegram.BotId)!.Value, _h.State.Read(s => s.Telegram.BotUsername)));
        Assert.Single(_h.Settings.Current.Destinations);
        Assert.Equal(2, _supervisor.Restarts);
    }

    [Fact]
    public async Task ReconnectingTheSameBotAfterDisconnect_IsNotASwitch()
    {
        _h.AddDestination(-1001);
        await _service.SaveAsync(FakeBotApi.Token, true, false, Ct);
        _service.Disconnect();

        var result = await _service.SaveAsync(FakeBotApi.Token, true, false, Ct);

        Assert.Equal(TokenSaveOutcome.Connected, result.Outcome);
    }

    [Fact]
    public async Task TakeOverWebhook_DeletesItAndRestarts()
    {
        Assert.False(await _service.TakeOverWebhookAsync(Ct));
        Assert.Empty(_h.Api.CallsTo("deleteWebhook"));

        _h.Secrets.Save(new BotSecrets { BotToken = FakeBotApi.Token });
        Assert.True(await _service.TakeOverWebhookAsync(Ct));

        Assert.Single(_h.Api.CallsTo("deleteWebhook"));
        Assert.Equal(1, _supervisor.Restarts);
    }

    [Fact]
    public async Task ConcurrentSaves_RunOneAtATime()
    {
        var inFlight = 0;
        var max = 0;
        _h.Api.OnAsync("getMe", async (_, ct) =>
        {
            var now = Interlocked.Increment(ref inFlight);
            max = Math.Max(max, now);
            await Task.Delay(100, ct);
            Interlocked.Decrement(ref inFlight);
            return FakeBotApi.Ok("""{"id":123456789,"is_bot":true,"first_name":"b","username":"tgjelly_test_bot"}""");
        });

        await Task.WhenAll(_service.SaveAsync(FakeBotApi.Token, true, false, Ct), _service.SaveAsync(FakeBotApi.Token, true, false, Ct));

        Assert.Equal(1, max);
    }

    [Fact]
    public async Task NothingReturnedOrLogged_ContainsTheToken()
    {
        var provider = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(provider));
        var service = NewService(new RedactingLogger<BotTokenService>(factory));
        _h.Api.On("getMe", _ => FakeBotApi.Error(500, "Internal error for bot" + FakeBotApi.Token));

        var result = await service.SaveAsync(FakeBotApi.Token, true, false, Ct);

        Assert.Equal(TokenSaveOutcome.Failed, result.Outcome);
        Assert.DoesNotContain(FakeBotApi.Token, result.Message, StringComparison.Ordinal);
        Assert.NotEmpty(provider.Entries);
        Assert.All(provider.Entries, e =>
        {
            Assert.DoesNotContain(FakeBotApi.Token, e.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(FakeBotApi.Token, e.Exception, StringComparison.Ordinal);
        });
    }

    public void Dispose() => _h.Dispose();

    private BotTokenService NewService(ILogger<BotTokenService> logger)
        => new(_factory, _h.Secrets, _h.State, _h.Settings, _supervisor, _h.Strings, _h.Time, logger);

    private void AssertNothingStored()
    {
        Assert.Equal(string.Empty, _h.Secrets.Load().BotToken);
        Assert.Null(_h.State.Read(s => s.Telegram.BotId));
        Assert.Equal(0, _supervisor.Restarts);
    }

    private void SeedBotScopedState(long botId, string destinationId)
    {
        var now = _h.Time.GetUtcNow();
        _h.State.Update(s =>
        {
            s.Telegram.BotId = botId;
            s.Telegram.BotUsername = "old_bot";
            s.Telegram.Profile = new ProfileState { SetProfile = true, PendingCalls = ["commands"] };
            s.Health[destinationId] = new DestinationHealth { Status = HealthStatus.NeedsPermissions, LastError = "rights" };
        });
        _h.State.UpdateQueue(q =>
        {
            q.SentMessages[destinationId + "|g"] = new SentMessage { MessageId = 7, SentAt = now };
            q.FileIdCache["item|1"] = new CachedFileId { FileId = "f", CachedAt = now };
            q.Outbox.Add(new OutboxEntry { EntryId = "e1", DestinationId = destinationId, EventKey = "media.movie", QueuedAt = now });
        });
    }
}
