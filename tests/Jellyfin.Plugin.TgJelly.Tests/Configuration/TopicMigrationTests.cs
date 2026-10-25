using Jellyfin.Plugin.TgJelly.Configuration;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Configuration;

public class TopicMigrationTests
{
    [Fact]
    public void V07PerEventMediaRoutes_TurnOnSeparateTopics_Once()
    {
        var c = new PluginConfiguration
        {
            Destinations =
            [
                new Destination { Id = "a", TopicRoutes = [new TopicRoute { Slot = "media.movie", ThreadId = 12 }] },
                new Destination { Id = "b", TopicRoutes = [new TopicRoute { Slot = "server", ThreadId = 9 }] },
            ],
        };

        c.Normalize();

        Assert.True(c.Destinations[0].SeparateMediaTopics);
        Assert.False(c.Destinations[1].SeparateMediaTopics);
        Assert.Equal(12, c.Destinations[0].ThreadFor("media.movie"));
        Assert.Equal(2, c.SchemaVersion);

        // Turned off later, the kept per-event routes don't turn it back on.
        c.Destinations[0].SeparateMediaTopics = false;
        c.Normalize();
        Assert.False(c.Destinations[0].SeparateMediaTopics);
    }
}
