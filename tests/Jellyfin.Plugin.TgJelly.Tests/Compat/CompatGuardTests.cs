using System;
using Jellyfin.Plugin.TgJelly.Compat;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Compat;

public class CompatGuardTests
{
    [Theory]
    [InlineData("10.11.0.0")]
    [InlineData("10.11.11.0")]
    public void Build1011_OnServer1011_IsCompatible(string server)
    {
        var result = CompatGuard.Evaluate(Version.Parse(server), 10, "10.11");

        Assert.True(result.IsCompatible);
        Assert.Null(result.Message);
    }

    [Theory]
    [InlineData("12.0.0.0")]
    [InlineData("12.2.0.0")]
    public void Build12_OnServer12_IsCompatible(string server)
    {
        var result = CompatGuard.Evaluate(Version.Parse(server), 12, "12");

        Assert.True(result.IsCompatible);
    }

    [Fact]
    public void Build1011_AfterUpgradeTo12_IsIncompatible_WithSpecBanner()
    {
        var result = CompatGuard.Evaluate(new Version(12, 2, 0, 0), 10, "10.11");

        Assert.False(result.IsCompatible);
        Assert.Equal(
            "This TgJelly build is for Jellyfin 10.11. Update the plugin (Dashboard → Plugins) to get the Jellyfin 12 build.",
            result.Message);
    }

    [Fact]
    public void Build12_OnOlderServer_NamesTheOlderLine()
    {
        var result = CompatGuard.Evaluate(new Version(10, 11, 5, 0), 12, "12");

        Assert.False(result.IsCompatible);
        Assert.Contains("get the Jellyfin 10.11 build", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_UsesThisBuildsTarget()
    {
        var server = new Version(BuildInfo.TargetJellyfinMajor, 0, 0, 0);

        Assert.True(CompatGuard.Evaluate(server).IsCompatible);
    }
}
