using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Pipeline;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Pipeline;

public class PresetUpgraderTests
{
    [Fact]
    public void NewMedia_MissingAKey_GetsItAdded_InCatalogOrder()
    {
        var c = Config(PresetKind.NewMedia, [.. EventCatalog.Expand(PresetKind.NewMedia).Where(k => k != "media.album")]);

        Assert.True(PresetUpgrader.Apply(c));

        Assert.Equal(EventCatalog.Expand(PresetKind.NewMedia), c.Destinations[0].Events);
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
