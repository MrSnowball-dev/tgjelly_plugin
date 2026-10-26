using System;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests;

public class PluginIdentityTests
{
    [Fact]
    public void Identity_IsFixedForever()
    {
        Assert.Equal("TgJelly", Plugin.PluginName);
        Assert.Equal(new Guid("e547b901-0dc4-42ae-8999-3871dd141270"), Plugin.PluginId);
        Assert.Equal("Jellyfin.Plugin.TgJelly", typeof(Plugin).Assembly.GetName().Name);
    }

}
