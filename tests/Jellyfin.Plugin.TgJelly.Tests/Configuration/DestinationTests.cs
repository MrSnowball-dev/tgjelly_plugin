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
}
