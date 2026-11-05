using System.Linq;
using Jellyfin.Plugin.TgJelly.Api;
using Jellyfin.Plugin.TgJelly.Configuration;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Api;

public class MessageSwitchesPatchTests
{
    // A switch the dashboard can show must be one it can save: v1.1 shipped ShowRestartButton without it, and every
    // save of it was refused as "changes nothing".
    [Fact]
    public void EverySwitch_CanBeSentAlone_AndIsApplied()
    {
        foreach (var name in MessageSwitches.Names)
        {
            var patch = new MessageSwitchesPatch();
            var property = typeof(MessageSwitchesPatch).GetProperty(name);
            Assert.NotNull(property);
            var switches = new MessageSwitches();
            var flipped = !switches.Get(name);
            property.SetValue(patch, flipped);

            Assert.True(patch.HasAny, name);
            patch.ApplyTo(switches);
            Assert.Equal(flipped, switches.Get(name));
            Assert.All(MessageSwitches.Names.Where(n => n != name), other => Assert.Equal(new MessageSwitches().Get(other), switches.Get(other)));
        }
    }
}
