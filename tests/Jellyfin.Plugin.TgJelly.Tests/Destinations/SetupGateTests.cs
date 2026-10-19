using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Destinations;

public sealed class SetupGateTests : IAsyncLifetime
{
    private readonly BotTestHarness _h = new();
    private BotMemory _memory = null!;
    private HealthService _health = null!;
    private SetupGate _gate = null!;

    public async ValueTask InitializeAsync()
    {
        _h.Connect();
        _memory = new BotMemory(_h.State, _h.Time);
        _health = new HealthService(await _h.StartOwnedGateAsync(), _h.Connection, new DestinationChecker(_h.Connection, _h.Strings), _h.Settings, _h.State, _h.Time, NullLogger<HealthService>.Instance);
        _h.Track(_health);
        _gate = new SetupGate(_memory, _health, _h.Settings);
    }

    public ValueTask DisposeAsync()
    {
        _h.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public void Closed_WhenEverythingIsFine()
    {
        _h.AddDestination(-1001);
        _memory.RecordDetected(-1002, "Family", ChatKind.Supergroup); // still inside its 30 s grace

        Assert.False(_gate.IsOpen());
    }

    [Fact]
    public void Opens_OnARecentFailure_ExpiresAfter7Days()
    {
        _memory.RecordFailure(SetupFailureKind.CheckFailed);
        Assert.True(_gate.IsOpen());

        _h.Time.Advance(TimeSpan.FromDays(7) - TimeSpan.FromSeconds(1));
        Assert.True(_gate.IsOpen());

        _h.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(_gate.IsOpen());
    }

    [Fact]
    public void Opens_OnUnhealthy()
    {
        var d = _h.AddDestination(-1001);
        _h.AddDestination(-1002);

        _health.MarkProblem(d.Id, HealthStatus.Lost, "kicked");

        Assert.True(_gate.IsOpen());
    }

    [Fact]
    public void Opens_OnVisibleDetected()
    {
        _memory.RecordDetected(-1002, "Family", ChatKind.Supergroup);
        _h.Time.Advance(TimeSpan.FromSeconds(31));

        Assert.True(_gate.IsOpen());
    }

    [Fact]
    public void Closed_WhenTheDetectedChatIsADestination()
    {
        _memory.RecordDetected(-1002, "Family", ChatKind.Supergroup);
        _h.AddDestination(-1002);
        _h.Time.Advance(TimeSpan.FromSeconds(31));

        Assert.False(_gate.IsOpen());
    }
}
