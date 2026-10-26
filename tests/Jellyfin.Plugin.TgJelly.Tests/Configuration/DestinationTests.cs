using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Configuration;

public class DestinationTests
{
    [Fact]
    public void RememberTopic_AddsRenamesAndKeepsANameWhenGivenEmpty()
    {
        var d = new Destination();

        d.RememberTopic(7, "Films");
        d.RememberTopic(8, string.Empty);
        d.RememberTopic(7, "Movies");
        d.RememberTopic(7, string.Empty);

        Assert.Equal([(7, "Movies"), (8, string.Empty)], d.KnownTopics.Select(t => (t.ThreadId, t.Name)));
    }

    [Fact]
    public void SetRoutes_ReplacesAndRemoves_InSlotOrder()
    {
        var d = new Destination { TopicRoutes = [new TopicRoute { Slot = "server", ThreadId = 9 }, new TopicRoute { Slot = "media", ThreadId = 5 }] };

        d.SetRoutes([("security", 8), ("media", null), ("server", 4)]);

        Assert.Equal([("security", 8), ("server", 4)], d.TopicRoutes.Select(r => (r.Slot, r.ThreadId)));
    }

    [Fact]
    public void SetRoutes_HandEditedDuplicateSlots_KeepsOneRoute()
    {
        var d = new Destination { TopicRoutes = [new TopicRoute { Slot = "media", ThreadId = 5 }, new TopicRoute { Slot = "media", ThreadId = 6 }] };

        d.SetRoutes([("server", 4)]);

        Assert.Equal([("media", 6), ("server", 4)], d.TopicRoutes.Select(r => (r.Slot, r.ThreadId)));
    }
}
