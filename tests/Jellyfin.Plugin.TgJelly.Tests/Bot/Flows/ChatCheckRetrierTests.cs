using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot.Flows;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Bot.Flows;

public sealed class ChatCheckRetrierTests : IDisposable
{
    private const long Channel = -1001234567890;
    private readonly BotTestHarness _h = new();
    private readonly List<TimeSpan> _delays = [];
    private readonly ChatCheckRetrier _retrier;

    public ChatCheckRetrierTests()
    {
        _h.Connect();
        _h.Api.AddChat(Channel, "channel", "Family");
        _retrier = new ChatCheckRetrier(new DestinationChecker(_h.Connection, _h.Strings), _h.Time)
        {
            Delay = (d, _) =>
            {
                _delays.Add(d);
                return Task.CompletedTask;
            },
        };
    }

    public void Dispose() => _h.Dispose();

    [Fact]
    public void Schedule_IsZeroThenOneAndAHalfThreeAndFiveSeconds()
        => Assert.Equal([TimeSpan.Zero, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5)], ChatCheckRetrier.Schedule);

    [Fact]
    public async Task Pass_StopsAtTheFirstTry()
    {
        _h.Api.SetMember(Channel, BotTestHarness.AdminCanPost);

        var result = await _retrier.CheckAsync(Channel, retry: true, TestContext.Current.CancellationToken);

        Assert.True(result.Passed);
        Assert.Empty(_delays);
        Assert.Single(_h.Api.CallsTo("getChatMember"));
    }

    [Fact]
    public async Task LatePass_WaitsTheGapsOfTheSchedule()
    {
        _h.Api.OnSequence("getChatMember", FakeBotApi.Ok(FakeBotApi.Member("left")), FakeBotApi.Ok(FakeBotApi.Member("left")), FakeBotApi.Ok(BotTestHarness.AdminCanPost));

        var result = await _retrier.CheckAsync(Channel, retry: true, TestContext.Current.CancellationToken);

        Assert.True(result.Passed);
        Assert.Equal([TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(1.5)], _delays);
    }

    [Fact]
    public async Task NeverPassing_TriesFourTimes_AndReturnsTheLastFailure()
    {
        var result = await _retrier.CheckAsync(Channel, retry: true, TestContext.Current.CancellationToken);

        Assert.False(result.Passed);
        Assert.Equal(HealthStatus.Lost, result.Problem);
        Assert.Equal(4, _h.Api.CallsTo("getChatMember").Count());
        Assert.Equal([TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(2)], _delays);
    }

    [Fact]
    public async Task ThrownCheck_DoesNotEndTheSchedule()
    {
        _h.Api.SetMember(Channel, BotTestHarness.AdminCanPost);
        var calls = 0;
        _h.Api.On("getChat", _ => calls++ == 0
            ? FakeBotApi.Error(500, "Internal Server Error")
            : FakeBotApi.Ok("{\"id\":" + Channel + ",\"type\":\"channel\",\"title\":\"Family\",\"accent_color_id\":0,\"max_reaction_count\":11}"));

        var result = await _retrier.CheckAsync(Channel, retry: true, TestContext.Current.CancellationToken);

        Assert.True(result.Passed);
        Assert.Equal([TimeSpan.FromSeconds(1.5)], _delays);
    }

    [Fact]
    public async Task CheckThatKeepsThrowing_IsTriedFourTimes_ThenFails()
    {
        _h.Api.On("getChat", _ => FakeBotApi.Error(500, "Internal Server Error"));

        await Assert.ThrowsAnyAsync<Exception>(() => _retrier.CheckAsync(Channel, retry: true, TestContext.Current.CancellationToken));

        Assert.Equal(4, _h.Api.CallsTo("getChat").Count());
    }

    [Fact]
    public async Task ThrownCheck_WithoutRetry_Fails()
    {
        _h.Api.On("getChat", _ => FakeBotApi.Error(500, "Internal Server Error"));

        await Assert.ThrowsAnyAsync<Exception>(() => _retrier.CheckAsync(Channel, retry: false, TestContext.Current.CancellationToken));

        Assert.Single(_h.Api.CallsTo("getChat"));
    }

    [Fact]
    public async Task WithoutRetry_TriesOnce()
    {
        var result = await _retrier.CheckAsync(Channel, retry: false, TestContext.Current.CancellationToken);

        Assert.False(result.Passed);
        Assert.Single(_h.Api.CallsTo("getChatMember"));
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task DefaultDelay_WaitsOnTheClock()
    {
        var retrier = new ChatCheckRetrier(new DestinationChecker(_h.Connection, _h.Strings), _h.Time);

        var wait = retrier.Delay(TimeSpan.FromSeconds(1.5), TestContext.Current.CancellationToken);
        Assert.False(wait.IsCompleted);
        _h.Time.Advance(TimeSpan.FromSeconds(1.5));

        await wait.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }
}
