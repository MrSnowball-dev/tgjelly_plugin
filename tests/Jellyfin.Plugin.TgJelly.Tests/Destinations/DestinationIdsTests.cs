using System.Collections.Generic;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Destinations;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Destinations;

public class DestinationIdsTests
{
    [Fact]
    public void New_IsValid_AndAvoidsExistingIds()
    {
        var existing = new HashSet<string>();
        for (var i = 0; i < 500; i++)
        {
            var id = DestinationIds.New(existing);
            Assert.True(SettingsValidator.IsValidId(id));
            Assert.True(existing.Add(id));
        }
    }
}
