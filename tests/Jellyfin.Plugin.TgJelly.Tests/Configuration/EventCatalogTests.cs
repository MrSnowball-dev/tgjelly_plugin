using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Configuration;

public class EventCatalogTests
{
    [Fact]
    public void HasAll_IsTrueOnlyWithEveryEvent()
    {
        Assert.Equal(19, EventCatalog.AllKeys.Count);
        Assert.True(EventCatalog.HasAll([.. EventCatalog.AllKeys]));
        Assert.False(EventCatalog.HasAll([.. EventCatalog.AllKeys.Skip(1)]));
        Assert.False(EventCatalog.HasAll([]));
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
