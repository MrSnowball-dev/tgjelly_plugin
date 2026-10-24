using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Pipeline;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Pipeline;

public class PresetUpgraderTests
{
    [Fact]
    public void Everything_MissingAKey_GetsItAdded_InCatalogOrder()
    {
        var c = Config(PresetKind.Everything, [.. EventCatalog.Expand(PresetKind.Everything).Where(k => k != "media.album")]);

        Assert.True(PresetUpgrader.Apply(c));

        Assert.Equal(EventCatalog.Expand(PresetKind.Everything), c.Destinations[0].Events);
    }

    [Theory]
    [InlineData(PresetKind.NewMedia)]
    [InlineData(PresetKind.AdminAlerts)]
    public void NewMediaAndAdminAlerts_BecomeCustom_KeepingTheirEvents(PresetKind preset)
    {
        var events = EventCatalog.Expand(preset).Where(k => k != "media.album").ToArray();
        var c = Config(preset, events);

        Assert.True(PresetUpgrader.Apply(c));

        Assert.Equal(PresetKind.Custom, c.Destinations[0].Preset);
        Assert.Equal(events, c.Destinations[0].Events);
    }

    [Fact]
    public void Custom_WithEveryEventOn_CountsAsEverything()
    {
        var c = Config(PresetKind.Custom, EventCatalog.Expand(PresetKind.Everything));

        Assert.True(PresetUpgrader.Apply(c));

        Assert.Equal(PresetKind.Everything, c.Destinations[0].Preset);
        Assert.Equal(EventCatalog.Expand(PresetKind.Everything), c.Destinations[0].Events);
    }

    [Fact]
    public void Custom_IsNeverTouched()
    {
        var c = Config(PresetKind.Custom, ["media.movie"]);

        Assert.False(PresetUpgrader.Apply(c));
        Assert.Equal(["media.movie"], c.Destinations[0].Events);
    }

    [Fact]
    public void UpToDate_ReturnsFalse()
        => Assert.False(PresetUpgrader.Apply(Config(PresetKind.Everything, EventCatalog.Expand(PresetKind.Everything))));

    internal static PluginConfiguration Config(PresetKind preset, string[] events)
        => new() { Destinations = [new Destination { Id = "abcd1234", Preset = preset, Events = events }] };
}
