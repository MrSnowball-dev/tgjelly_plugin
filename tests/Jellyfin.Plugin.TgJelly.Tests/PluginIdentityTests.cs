using System;
using Jellyfin.Plugin.TgJelly.Compat;
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

    [Fact]
    public void BuildInfo_MatchesTargetFramework()
    {
        // The net9.0 build is the Jellyfin 10.11 track, the net10.0 build is the Jellyfin 12 track.
        var expectedMajor = Environment.Version.Major >= 10 ? 12 : 10;
        int actualMajor = BuildInfo.TargetJellyfinMajor;
        Assert.Equal(expectedMajor, actualMajor);
    }
}
