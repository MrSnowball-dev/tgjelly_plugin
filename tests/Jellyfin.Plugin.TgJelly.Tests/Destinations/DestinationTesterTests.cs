using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Destinations;

public sealed class DestinationTesterTests : IDisposable
{
    private readonly BotTestHarness _h = new();
    private readonly HealthService _health;
    private readonly DestinationTester _tester;

    public DestinationTesterTests()
    {
        _h.Connect();
        var gate = new InstanceGate(_h.Paths, _h.Time, NullLogger<InstanceGate>.Instance);
        _h.Track(gate);
        _health = new HealthService(gate, _h.Connection, new DestinationChecker(_h.Connection, _h.Strings), _h.Settings, _h.State, _h.Time, NullLogger<HealthService>.Instance);
        _h.Track(_health);
        _tester = new DestinationTester(_h.Connection, _health, _h.Strings, NullLogger<DestinationTester>.Instance);
    }

    [Fact]
    public async Task Test_SendsTheGroupsAsItalicText()
    {
        var d = _h.AddDestination(-1001, preset: PresetKind.AdminAlerts);

        var result = await _tester.SendTestAsync(d, TestContext.Current.CancellationToken);

        Assert.True(result.Sent);
        var body = Assert.Single(_h.Api.CallsTo("sendMessage")).Body;
        Assert.Equal("✅ TgJelly test: this chat will receive <i>security alerts, server alerts</i>", (string)body["text"]!);
        Assert.Equal("html", ((string)body["parse_mode"]!).ToLowerInvariant());
    }

    [Fact]
    public async Task Test_WithNoEvents_SaysNothingYet()
    {
        var d = _h.AddDestination(-1001, preset: PresetKind.Custom);

        await _tester.SendTestAsync(d, TestContext.Current.CancellationToken);

        Assert.Contains("nothing yet", (string)Assert.Single(_h.Api.CallsTo("sendMessage")).Body["text"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Test_RechecksFirst_SoAHeldDestinationRecoversAndReceivesIt()
    {
        var d = _h.AddDestination(-1001, threadId: 5);
        _health.MarkProblem(d.Id, HealthStatus.Lost, "kicked");

        var result = await _tester.SendTestAsync(d, TestContext.Current.CancellationToken);

        Assert.True(result.Sent);
        Assert.Equal(HealthStatus.Ok, result.Health.Status);
        Assert.Equal(5, (int)Assert.Single(_h.Api.CallsTo("sendMessage")).Body["message_thread_id"]!);
    }

    [Fact]
    public async Task Test_ToALostChat_ReportsTheProblem_AndSendsNothing()
    {
        var d = _h.AddDestination(-1001);
        _h.Api.SetMember(-1001, FakeBotApi.Member("left"));

        var result = await _tester.SendTestAsync(d, TestContext.Current.CancellationToken);

        Assert.False(result.Sent);
        Assert.Equal(HealthStatus.Lost, result.Health.Status);
        Assert.Equal(_h.Strings.Get("check.lost"), result.Error);
        Assert.Empty(_h.Api.CallsTo("sendMessage"));
    }

    public void Dispose() => _h.Dispose();
}
