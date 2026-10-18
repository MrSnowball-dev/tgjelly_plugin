using Jellyfin.Plugin.TgJelly.Configuration;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Configuration;

public class EventCatalogTests
{
    [Fact]
    public void Presets_ExpandToTheSpecsKeys()
    {
        Assert.Equal(19, EventCatalog.Expand(PresetKind.Everything).Length);
        Assert.Equal(["media.movie", "media.episodes", "media.album", "media.musicvideo", "media.book"], EventCatalog.Expand(PresetKind.NewMedia));
        var admin = EventCatalog.Expand(PresetKind.AdminAlerts);
        Assert.Equal(13, admin.Length);
        Assert.DoesNotContain("security.login_succeeded", admin);
        Assert.Contains("server.started", admin);
        Assert.Empty(EventCatalog.Expand(PresetKind.Custom));
    }

    [Theory]
    [InlineData("media.movie", "media")]
    [InlineData("security.lockout", "security")]
    [InlineData("server.restart_pending", "server")]
    public void GroupOf_IsThePrefix(string key, string group) => Assert.Equal(group, EventCatalog.GroupOf(key));

    [Fact]
    public void Keys_AreUnique_AndAllKnown()
    {
        Assert.Equal(EventCatalog.AllKeys.Count, new System.Collections.Generic.HashSet<string>(EventCatalog.AllKeys).Count);
        Assert.All(EventCatalog.AllKeys, k => Assert.True(EventCatalog.IsKnown(k)));
        Assert.False(EventCatalog.IsKnown("media.unknown"));
        Assert.Equal(["media", "security", "server"], EventCatalog.Groups);
    }
}
