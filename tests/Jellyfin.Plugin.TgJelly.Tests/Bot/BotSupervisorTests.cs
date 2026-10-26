using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Bot;

public sealed class BotSupervisorTests : IDisposable
{
    private const string TwoUpdates = """{"ok":true,"result":[{"update_id":10,"message":{"message_id":1,"date":0,"chat":{"id":42,"type":"private"},"text":"a"}},{"update_id":11,"message":{"message_id":2,"date":0,"chat":{"id":42,"type":"private"},"text":"b"}}]}""";

    private readonly BotTestHarness _h = new();
    private readonly FakeClientFactory _factory;
    private readonly FakeStartup _startup = new();
    private readonly RecordingUpdateHandler _handler = new();
    private HealthService? _health;

    public BotSupervisorTests()
    {
        _factory = new FakeClientFactory(_h.Api);
    }

    [Fact]
    public async Task NoToken_StaysNoToken_AndNeverCallsTelegram()
    {
        using var supervisor = await StartAsync(token: null);

        await Eventually.TrueAsync(() => _h.Connection.State == ConnectionState.NoToken, "no token");
        await Eventually.SettleAsync();
        Assert.Empty(_h.Api.Calls);
        await supervisor.StopAsync(CancellationToken.None);
    }


    [Fact]
    public async Task WaitsForJellyfinStartup()
    {
        _startup.HasCompleted = false;
        using var supervisor = await StartAsync();
        await Eventually.TrueAsync(() => _h.Connection.State == ConnectionState.WaitingForStartup, "waiting for startup");
        Assert.Empty(_h.Api.Calls);

        _startup.HasCompleted = true;

        await Eventually.AdvanceUntilAsync(_h.Time, TimeSpan.FromSeconds(1), () => _h.Connection.State == ConnectionState.Connected, "connected after startup");
        await supervisor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FirstPoll_HasNoOffset_LaterPollsConfirmThePreviousBatch()
    {
        _h.Api.OnSequence("getUpdates", TwoUpdates, FakeBotApi.Ok("[]"));
        using var supervisor = await StartAsync();

        await Eventually.TrueAsync(() => _h.Api.CallsTo("getUpdates").Count() >= 2, "two polls");

        var polls = _h.Api.CallsTo("getUpdates").ToArray();
        Assert.False(polls[0].Body.ContainsKey("offset"));
        Assert.Equal(12, (int)polls[1].Body["offset"]!);
        Assert.Equal([10, 11], _handler.Ids.Take(2).ToArray());
        await supervisor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Poll_UsesLimit100_Timeout50_AndTheThreeAllowedUpdates()
    {
        using var supervisor = await StartAsync();

        await Eventually.TrueAsync(() => _h.Api.CallsTo("getUpdates").Count() >= 2, "polled twice");

        var polls = _h.Api.CallsTo("getUpdates").ToArray();
        var body = polls[1].Body;
        Assert.Equal((100, 50), ((int)body["limit"]!, (int)body["timeout"]!));
        Assert.Equal(0, (int)polls[0].Body["timeout"]!); // the first poll of a session answers at once
        Assert.Equal(["message", "callback_query", "my_chat_member"], body["allowed_updates"]!.AsArray().Select(n => (string)n!).ToArray());
        await supervisor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TokenRejected_StopsPolling_UntilRestart()
    {
        _h.Api.On("getMe", _ => FakeBotApi.Error(401, "Unauthorized"));
        using var supervisor = await StartAsync();

        await Eventually.TrueAsync(() => _h.Connection.State == ConnectionState.TokenRejected, "token rejected");
        _h.Time.Advance(TimeSpan.FromMinutes(10));
        await Eventually.SettleAsync();
        Assert.Single(_h.Api.CallsTo("getMe"));

        supervisor.Restart();
        await Eventually.TrueAsync(() => _h.Api.CallsTo("getMe").Count() == 2, "a restart tries again");
        await supervisor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WebhookSet_IsAConflict_AndIsNeverDeleted()
    {
        _h.Api.On("getWebhookInfo", _ => FakeBotApi.Ok("""{"url":"https://other.example.com/hook","has_custom_certificate":false,"pending_update_count":3}"""));
        using var supervisor = await StartAsync();

        await Eventually.TrueAsync(() => _h.Connection.State == ConnectionState.WebhookConflict, "webhook conflict");
        Assert.Empty(_h.Api.CallsTo("getUpdates"));
        await Eventually.AdvanceUntilAsync(_h.Time, TimeSpan.FromMinutes(1), () => _h.Api.CallsTo("getWebhookInfo").Count() >= 2, "checked again after 5 min");

        Assert.Empty(_h.Api.CallsTo("deleteWebhook"));
        await supervisor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ConflictingInstance_BacksOffFrom30Seconds()
    {
        _h.Api.On("getUpdates", _ => FakeBotApi.Error(409, "Conflict: terminated by other getUpdates request; make sure that only one bot instance is running"));
        using var supervisor = await StartAsync();

        await Eventually.TrueAsync(() => _h.Connection.State == ConnectionState.ConflictingInstance, "conflict");
        _h.Time.Advance(TimeSpan.FromSeconds(29));
        await Eventually.SettleAsync();
        Assert.Single(_h.Api.CallsTo("getUpdates"));

        await Eventually.AdvanceUntilAsync(_h.Time, TimeSpan.FromSeconds(1), () => _h.Api.CallsTo("getUpdates").Count() == 2, "polled again after 30 s");
        await supervisor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TransportErrors_ShowUnreachable_ThenReconnect()
    {
        var failures = 0;
        _h.Api.OnAsync("getUpdates", async (_, ct) =>
        {
            if (Interlocked.Increment(ref failures) <= 3)
            {
                throw new HttpRequestException("network down");
            }

            await Task.Delay(20, ct);
            return FakeBotApi.Ok("[]");
        });
        using var supervisor = await StartAsync();

        await Eventually.TrueAsync(() => _h.Connection.State == ConnectionState.Unreachable, "unreachable");
        await Eventually.AdvanceUntilAsync(_h.Time, TimeSpan.FromSeconds(1), () => _h.Connection.State == ConnectionState.Connected, "reconnected by itself");
        await supervisor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RateLimited_WaitsRetryAfter()
    {
        _h.Api.OnSequence("getUpdates", FakeBotApi.Error(429, "Too Many Requests: retry after 3", """{"retry_after":3}"""), FakeBotApi.Ok("[]"));
        using var supervisor = await StartAsync();

        await Eventually.TrueAsync(() => _h.Api.CallsTo("getUpdates").Any(), "first poll");
        _h.Time.Advance(TimeSpan.FromSeconds(2));
        await Eventually.SettleAsync();
        Assert.Single(_h.Api.CallsTo("getUpdates"));

        await Eventually.AdvanceUntilAsync(_h.Time, TimeSpan.FromSeconds(1), () => _h.Api.CallsTo("getUpdates").Count() >= 2, "polled after retry_after");
        await supervisor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AThrowingHandler_SkipsTheUpdate_AndPollingContinues()
    {
        _handler.ThrowFor = u => u.Id == 10;
        _h.Api.OnSequence("getUpdates", TwoUpdates, FakeBotApi.Ok("[]"));
        using var supervisor = await StartAsync();

        await Eventually.TrueAsync(() => _handler.Saw(11) && _h.Api.CallsTo("getUpdates").Count() >= 2, "kept going");

        Assert.Equal(12, (int)_h.Api.CallsTo("getUpdates").ElementAt(1).Body["offset"]!);
        await supervisor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ARestart_StartsWithoutAnOffset_SoTheLastBatchMayArriveAgain()
    {
        _h.Api.OnSequence("getUpdates", TwoUpdates, FakeBotApi.Ok("[]"));
        using var supervisor = await StartAsync();
        await Eventually.TrueAsync(() => _h.Api.CallsTo("getUpdates").Count() >= 2, "batch confirmed");
        var before = _h.Api.CallsTo("getMe").Count();

        supervisor.Restart();

        // The old session may still finish a long poll after the restart, so wait for a poll that follows the new
        // session's getMe rather than counting polls.
        BotApiCall? FirstOfNewSession() => _h.Api.Calls.SkipWhile(c => c.Method != "getMe").Skip(1).SkipWhile(c => c.Method != "getMe").FirstOrDefault(c => c.Method == "getUpdates");
        await Eventually.TrueAsync(() => _h.Api.CallsTo("getMe").Count() > before && FirstOfNewSession() is not null, "new session polled");
        Assert.False(FirstOfNewSession()!.Body.ContainsKey("offset"));
        await supervisor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SessionStart_RequestsHealthChecks_AndLeavesTheProfileAlone()
    {
        _h.AddDestination(-1001);
        _h.AddDestination(-1002);
        using var supervisor = await StartAsync(startHealth: true);

        await Eventually.TrueAsync(() => _h.Api.CallsTo("getChat").Count() == 2, "both destinations re-checked");
        Assert.Empty(_h.Api.CallsTo("setMyCommands"));
        Assert.Empty(_h.Api.CallsTo("setMyDefaultAdministratorRights"));
        Assert.Equal((FakeBotApi.BotId, FakeBotApi.BotUsername), (_h.State.Read(s => s.Telegram.BotId)!.Value, _h.State.Read(s => s.Telegram.BotUsername)));
        await supervisor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Restart_DuringALongPoll_NeverPollsTwiceAtOnce()
    {
        var inFlight = 0;
        var maxInFlight = 0;
        var cancelled = 0;
        _h.Api.OnAsync("getUpdates", async (_, ct) =>
        {
            var now = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref maxInFlight, now);
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
                return FakeBotApi.Ok("[]");
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref cancelled);
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref inFlight);
            }
        });
        using var supervisor = await StartAsync();
        await Eventually.TrueAsync(() => Volatile.Read(ref inFlight) == 1, "first long poll in flight");

        for (var i = 0; i < 5; i++)
        {
            supervisor.Restart();
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        await Eventually.TrueAsync(() => _h.Api.CallsTo("getUpdates").Count() >= 2 && Volatile.Read(ref inFlight) == 1, "polling resumed");
        Assert.Equal(1, Volatile.Read(ref maxInFlight));
        Assert.Equal(_h.Api.CallsTo("getUpdates").Count() - 1, Volatile.Read(ref cancelled));
        await supervisor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Stop_ConfirmsTheLastBatch()
    {
        _h.Api.OnSequence("getUpdates", TwoUpdates, FakeBotApi.Ok("[]"));
        using var supervisor = await StartAsync();
        await Eventually.TrueAsync(() => _handler.Saw(11), "batch handled");

        await supervisor.StopAsync(CancellationToken.None);

        var last = _h.Api.CallsTo("getUpdates").Last().Body;
        Assert.Equal((12, 1, 0), ((int)last["offset"]!, (int)last["limit"]!, (int)last["timeout"]!));
    }

    [Fact]
    public async Task Stop_WhileTelegramHangs_ReturnsQuickly_AndDoesNotThrow()
    {
        var first = 0;
        _h.Api.OnAsync("getUpdates", async (_, ct) =>
        {
            if (Interlocked.Increment(ref first) == 1)
            {
                return TwoUpdates;
            }

            await Task.Delay(Timeout.Infinite, ct); // Telegram never answers
            return FakeBotApi.Ok("[]");
        });
        using var supervisor = await StartAsync();
        await Eventually.TrueAsync(() => _handler.Saw(11) && _h.Api.CallsTo("getUpdates").Count() >= 2, "a long poll hangs");

        var watch = Stopwatch.StartNew();
        await supervisor.StopAsync(CancellationToken.None);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"stopped in {watch.Elapsed}");
    }

    public void Dispose() => _h.Dispose();

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }

    private async Task<BotSupervisor> StartAsync(string? token = FakeBotApi.Token, bool startHealth = false)
    {
        if (token is not null)
        {
            _h.Secrets.Save(new BotSecrets { BotToken = token });
        }

        var gate = await _h.StartOwnedGateAsync();
        _health = new HealthService(gate, _h.Connection, new DestinationChecker(_h.Connection, _h.Strings), _h.Settings, _h.State, _h.Time, NullLogger<HealthService>.Instance);
        _h.Track(_health);
        if (startHealth)
        {
            await _health.StartAsync(CancellationToken.None);
        }

        var supervisor = new BotSupervisor(gate, _startup, _h.Secrets, _h.State, _factory, _h.Connection, _health, _handler, _h.Strings, _h.Time, NullLogger<BotSupervisor>.Instance);
        _h.Track(supervisor);
        await supervisor.StartAsync(CancellationToken.None);
        return supervisor;
    }

    [Fact]
    public async Task Connected_BeforeTheFirstLongPollReturns()
    {
        // Real Telegram holds an empty long poll for 50 s; Status, the Outbox and health checks must not wait for it.
        _h.Api.OnAsync("getUpdates", async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(50), _h.Time, ct);
            return FakeBotApi.Ok("[]");
        });
        using var supervisor = await StartAsync();

        await Eventually.TrueAsync(() => _h.Connection.State == ConnectionState.Connected, "connected once getMe and the webhook check passed");
        await supervisor.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AfterAnError_TheNextPollIsQuick()
    {
        var calls = 0;
        _h.Api.OnAsync("getUpdates", async (_, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 2)
            {
                throw new HttpRequestException("network blip");
            }

            await Task.Delay(20, ct);
            return FakeBotApi.Ok("[]");
        });
        using var supervisor = await StartAsync();

        await Eventually.AdvanceUntilAsync(_h.Time, TimeSpan.FromSeconds(1), () => _h.Api.CallsTo("getUpdates").Count() >= 4, "polling resumed");

        var timeouts = _h.Api.CallsTo("getUpdates").Select(c => (int)c.Body["timeout"]!).Take(4).ToArray();
        Assert.Equal([0, 50, 0, 50], timeouts);
        await supervisor.StopAsync(CancellationToken.None);
    }
}
