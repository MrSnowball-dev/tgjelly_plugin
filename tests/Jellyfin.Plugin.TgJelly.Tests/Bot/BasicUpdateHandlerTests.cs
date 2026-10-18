using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Telegram.Bot.Types;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Bot;

public sealed class BasicUpdateHandlerTests : IDisposable
{
    private readonly BotTestHarness _h = new();
    private readonly HealthService _health;
    private readonly BasicUpdateHandler _handler;

    public BasicUpdateHandlerTests()
    {
        _h.Connect();
        var gate = new InstanceGate(_h.Paths, _h.Time, NullLogger<InstanceGate>.Instance);
        _h.Track(gate);
        _health = new HealthService(gate, _h.Connection, new DestinationChecker(_h.Connection, _h.Strings), _h.Settings, _h.State, _h.Time, NullLogger<HealthService>.Instance);
        _h.Track(_health);
        _handler = new BasicUpdateHandler(_h.Connection, _health, NullLogger<BasicUpdateHandler>.Instance);
    }

    [Fact]
    public async Task CallbackQuery_IsAnswered()
    {
        await _handler.HandleAsync(Parse("""{"update_id":1,"callback_query":{"id":"cb1","from":{"id":42,"is_bot":false,"first_name":"a"},"chat_instance":"x","data":"v1:menu"}}"""), TestContext.Current.CancellationToken);

        Assert.Equal("cb1", (string)Assert.Single(_h.Api.CallsTo("answerCallbackQuery")).Body["callback_query_id"]!);
    }

    [Fact]
    public async Task TooOldCallback_IsIgnored()
    {
        _h.Api.On("answerCallbackQuery", _ => FakeBotApi.Error(400, "Bad Request: query is too old and response timeout expired or query ID is invalid"));

        await _handler.HandleAsync(Parse("""{"update_id":1,"callback_query":{"id":"cb1","from":{"id":42,"is_bot":false,"first_name":"a"},"chat_instance":"x","data":"v1:menu"}}"""), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MyChatMember_RequestsACheckForThatChat()
    {
        var d = _h.AddDestination(-1002);
        var gate = await _h.StartOwnedGateAsync();
        using var health = new HealthService(gate, _h.Connection, new DestinationChecker(_h.Connection, _h.Strings), _h.Settings, _h.State, _h.Time, NullLogger<HealthService>.Instance);
        await health.StartAsync(TestContext.Current.CancellationToken);
        var handler = new BasicUpdateHandler(_h.Connection, health, NullLogger<BasicUpdateHandler>.Instance);

        await handler.HandleAsync(Parse("""{"update_id":2,"my_chat_member":{"chat":{"id":-1002,"type":"supergroup","title":"g"},"from":{"id":42,"is_bot":false,"first_name":"a"},"date":0,"old_chat_member":{"status":"left","user":{"id":123456789,"is_bot":true,"first_name":"b"}},"new_chat_member":{"status":"member","user":{"id":123456789,"is_bot":true,"first_name":"b"}}}}"""), TestContext.Current.CancellationToken);

        await Eventually.TrueAsync(() => _h.Api.CallsTo("getChat").Any(c => (long)c.Body["chat_id"]! == -1002), "the chat was re-checked");
        Assert.Equal(Jellyfin.Plugin.TgJelly.State.Documents.HealthStatus.Ok, health.Get(d.Id).Status);
        await health.StopAsync(TestContext.Current.CancellationToken);
    }

    public void Dispose() => _h.Dispose();

    private static Update Parse(string json) => JsonSerializer.Deserialize<Update>(json, JsonBotAPI.Options)!;
}
