using System;
using Jellyfin.Plugin.TgJelly.Delivery;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Delivery;

public class PacingGateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 10, 0, 0, TimeSpan.Zero);
    private readonly PacingGate _gate = new(new FakeTimeProvider(T0));

    [Fact]
    public void SameChat_WaitsOneSecond()
    {
        _gate.RecordSend(1, T0);

        Assert.Equal(T0 + TimeSpan.FromSeconds(1), _gate.NextAllowed(1, false, T0));
        Assert.Equal(T0, _gate.NextAllowed(2, false, T0));
    }

    [Fact]
    public void GroupOrChannel_At20PerMinute_WaitsForTheWindow()
    {
        for (var i = 0; i < 20; i++)
        {
            _gate.RecordSend(-100, T0 + TimeSpan.FromSeconds(i));
        }

        Assert.Equal(T0 + TimeSpan.FromSeconds(60), _gate.NextAllowed(-100, true, T0 + TimeSpan.FromSeconds(20)));
        Assert.Equal(T0 + TimeSpan.FromSeconds(20), _gate.NextAllowed(-100, false, T0 + TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void Overall_2PerSecond()
    {
        _gate.RecordSend(1000, T0);
        Assert.Equal(T0 + TimeSpan.FromMilliseconds(100), _gate.NextAllowed(999, false, T0 + TimeSpan.FromMilliseconds(100)));
        _gate.RecordSend(1001, T0 + TimeSpan.FromMilliseconds(100));

        Assert.Equal(T0 + TimeSpan.FromSeconds(1), _gate.NextAllowed(999, false, T0 + TimeSpan.FromMilliseconds(500)));
        Assert.Equal(T0 + TimeSpan.FromSeconds(1.5), _gate.NextAllowed(999, false, T0 + TimeSpan.FromSeconds(1.5)));
    }

    [Fact]
    public void Pause_HoldsTheChatUntil()
    {
        _gate.Pause(1, T0 + TimeSpan.FromSeconds(7));

        Assert.Equal(T0 + TimeSpan.FromSeconds(7), _gate.NextAllowed(1, false, T0));
        Assert.Equal(T0, _gate.NextAllowed(2, false, T0));
    }
}
