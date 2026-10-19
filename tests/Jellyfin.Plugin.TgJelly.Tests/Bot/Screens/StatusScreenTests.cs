using System;
using System.Threading.Tasks;
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
}
