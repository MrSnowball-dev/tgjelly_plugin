using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Bot.Screens;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Bot.Screens;

public sealed class StatusScreenTests : IDisposable
{
    private readonly BotTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Connected_ShowsJellyfinServerAndVersion()
    {
        var rig = await BotRig.CreateAsync(_h);
        rig.Server.ServerName = "Home";
        rig.Server.Version = new Version(12, 2, 0, 0);

        var html = rig.Status.Build().Html;

        Assert.Contains("Connection: connected to Jellyfin Home (12.2)", html, StringComparison.Ordinal);
        Assert.DoesNotContain("@", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connected_EmptyServerName_ShowsVersionOnly()
    {
        var rig = await BotRig.CreateAsync(_h);
        rig.Server.ServerName = string.Empty;

        var html = rig.Status.Build().Html;

        Assert.Contains("Connection: connected to Jellyfin (12.2)", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OtherStates_Unchanged()
    {
        var rig = await BotRig.CreateAsync(_h);
        _h.Connection.SetState(ConnectionState.TokenRejected);

        var html = rig.Status.Build().Html;

        Assert.Contains("Connection: Token rejected", html, StringComparison.Ordinal);
        Assert.DoesNotContain("connected to Jellyfin", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Build_KeepsOnlyTheLastThreeErrors_AndEscapesThem()
    {
        var rig = await BotRig.CreateAsync(_h);
        for (var i = 1; i <= 4; i++)
        {
            _h.State.RecordError("outbox", "boom <" + i + ">");
        }

        var html = rig.Status.Build().Html;

        Assert.DoesNotContain("boom &lt;1&gt;", html, StringComparison.Ordinal);
        Assert.Contains("boom &lt;4&gt;", html, StringComparison.Ordinal);
        Assert.Contains("boom &lt;2&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Build_ShowsALiveDestinationsTitleInItsErrors()
    {
        var rig = await BotRig.CreateAsync(_h);
        var d = _h.AddDestination(-100111);
        _h.State.RecordError("outbox", "<dest:" + d.Id + ">: Forbidden");

        var html = rig.Status.Build().Html;

        Assert.Contains(d.Title + ": Forbidden", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Build_WithManyDestinationsAndLongErrors_StaysUnderTelegramsLimit_AndPointsToTheDashboard()
    {
        var rig = await BotRig.CreateAsync(_h);
        for (var i = 1; i <= 150; i++)
        {
            _h.AddDestination(-1000000 - i);
        }

        for (var i = 1; i <= 3; i++)
        {
            _h.State.RecordError("outbox", new string('<', 2000));
        }

        var html = rig.Status.Build().Html;

        Assert.True(html.Length <= 4096, "length " + html.Length);
        Assert.Contains("more on the dashboard", html, StringComparison.Ordinal);
        Assert.Contains("Chat -1000001", html, StringComparison.Ordinal);
    }
}
