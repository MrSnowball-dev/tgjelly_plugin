using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Delivery;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Bot;

public sealed class UpdateRouterTests : IDisposable
{
    private readonly BotTestHarness _h = new();
    private readonly RecordingBotFlows _flows = new();
    private readonly BotWorkQueue _queue = new(NullLogger<BotWorkQueue>.Instance);
    private readonly ListLogger<UpdateRouter> _log = new();
    private readonly HealthService _health;
    private readonly UpdateRouter _router;

    public UpdateRouterTests()
    {
        _h.Connect();
        var gate = new InstanceGate(_h.Paths, _h.Time, NullLogger<InstanceGate>.Instance);
        _h.Track(gate);
        _health = new HealthService(gate, _h.Connection, new DestinationChecker(_h.Connection, _h.Strings), _h.Settings, _h.State, _h.Time, NullLogger<HealthService>.Instance);
        _h.Track(_health);
        var migration = new DestinationMigration(_h.Settings, new Outbox(_h.State, _h.Strings, _h.Time), _health);
        _router = new UpdateRouter(_queue, _flows, _h.Connection, _health, migration, _log);
    }

    [Fact]
    public async Task Callback_IsAnsweredBeforeTheWorkRuns()
    {
        await _router.HandleAsync(TelegramFixtures.Update("callback_query"), TestContext.Current.CancellationToken);

        Assert.Equal("cb1", (string)Assert.Single(_h.Api.CallsTo("answerCallbackQuery")).Body["callback_query_id"]!);
        Assert.Empty(_flows.Calls);

        await _queue.RunPendingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["callback:1|m"], _flows.Calls);
    }

    [Fact]
    public async Task TooOldCallback_IsIgnored_AndStillQueued()
    {
        _h.Api.On("answerCallbackQuery", _ => FakeBotApi.Error(400, "Bad Request: query is too old and response timeout expired or query ID is invalid"));

        await _router.HandleAsync(TelegramFixtures.Update("callback_query"), TestContext.Current.CancellationToken);
        await _queue.RunPendingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["callback:1|m"], _flows.Calls);
        Assert.Equal(0, _log.Count(LogLevel.Error));
    }

    [Fact]
    public async Task PrivateMessage_IsQueued()
    {
        await _router.HandleAsync(TelegramFixtures.Update("private_text", ("text", "hi")), TestContext.Current.CancellationToken);
        await _router.HandleAsync(TelegramFixtures.Update("private_command_start"), TestContext.Current.CancellationToken);

        Assert.Empty(_flows.Calls);
        await _queue.RunPendingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["message:hi", "message:/start"], _flows.Calls);
    }

    [Fact]
    public async Task GroupAndChannelMessages_AreIgnored()
    {
        await _router.HandleAsync(TelegramFixtures.Update("group_text"), TestContext.Current.CancellationToken);
        await _router.HandleAsync(TelegramFixtures.Update("group_text", ("chat_id", "-100555")), TestContext.Current.CancellationToken);
        await _queue.RunPendingAsync(TestContext.Current.CancellationToken);

        Assert.Empty(_flows.Calls);
        Assert.Empty(_h.Api.Calls);
    }

    [Fact]
    public async Task MigrateToChatId_RewritesTheDestination()
    {
        var d = _h.AddDestination(-1001, ChatKind.Group);

        await _router.HandleAsync(TelegramFixtures.Update("group_migrate_to", ("chat_id", "-1001"), ("new_chat_id", "-1009")), TestContext.Current.CancellationToken);
        Assert.Equal(-1001, _h.Settings.Current.Destinations.Single().ChatId);
        await _queue.RunPendingAsync(TestContext.Current.CancellationToken);

        var moved = _h.Settings.Current.Destinations.Single();
        Assert.Equal(d.Id, moved.Id);
        Assert.Equal(-1009, moved.ChatId);
        Assert.Equal(ChatKind.Supergroup, moved.Kind);
        Assert.Empty(_flows.Calls);
    }

    [Fact]
    public async Task MyChatMember_ReChecksAndQueues()
    {
        var d = _h.AddDestination(-1002);
        var gate = await _h.StartOwnedGateAsync();
        using var health = new HealthService(gate, _h.Connection, new DestinationChecker(_h.Connection, _h.Strings), _h.Settings, _h.State, _h.Time, NullLogger<HealthService>.Instance);
        await health.StartAsync(TestContext.Current.CancellationToken);
        var router = new UpdateRouter(_queue, _flows, _h.Connection, health, new DestinationMigration(_h.Settings, new Outbox(_h.State, _h.Strings, _h.Time), health), _log);

        await router.HandleAsync(TelegramFixtures.Update("my_chat_member_group_member", ("chat_id", "-1002")), TestContext.Current.CancellationToken);

        await Eventually.TrueAsync(() => _h.Api.CallsTo("getChat").Any(c => (long)c.Body["chat_id"]! == -1002), "the chat was re-checked");
        Assert.Equal(Jellyfin.Plugin.TgJelly.State.Documents.HealthStatus.Ok, health.Get(d.Id).Status);
        await _queue.RunPendingAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["member:-1002"], _flows.Calls);
        await health.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RedeliveredCallback_IsAnsweredAndHarmless()
    {
        var update = TelegramFixtures.Update("callback_query");

        await _router.HandleAsync(update, TestContext.Current.CancellationToken);
        await _router.HandleAsync(update, TestContext.Current.CancellationToken);
        await _queue.RunPendingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, _h.Api.CallsTo("answerCallbackQuery").Count());
        Assert.Equal(["callback:1|m", "callback:1|m"], _flows.Calls);
    }

    [Fact]
    public async Task ThrowingFlows_NeverEscapeTheRouter()
    {
        _flows.Throw = new InvalidOperationException("boom");
        _h.Api.On("answerCallbackQuery", _ => FakeBotApi.Error(500, "Internal Server Error"));

        await _router.HandleAsync(TelegramFixtures.Update("callback_query"), TestContext.Current.CancellationToken);
        await _router.HandleAsync(TelegramFixtures.Update("private_text"), TestContext.Current.CancellationToken);
        await _router.HandleAsync(TelegramFixtures.Update("my_chat_member_kicked"), TestContext.Current.CancellationToken);
        await _queue.RunPendingAsync(TestContext.Current.CancellationToken);

        Assert.True(_log.Count(LogLevel.Error) + _log.Count(LogLevel.Warning) >= 1);
    }

    [Fact]
    public async Task OtherUpdates_AreIgnored()
    {
        await _router.HandleAsync(new Telegram.Bot.Types.Update { Id = 5 }, TestContext.Current.CancellationToken);
        await _router.HandleAsync(TelegramFixtures.Update("private_text", ("text", "x")), TestContext.Current.CancellationToken);
        await _queue.RunPendingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["message:x"], _flows.Calls);
    }

    public void Dispose() => _h.Dispose();
}
