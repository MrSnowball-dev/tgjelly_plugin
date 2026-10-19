using System;
using Jellyfin.Plugin.TgJelly.Updates;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Updates;

public class ReleaseVersionTests
{
    [Theory]
    [InlineData("v12.2", "12.2.0.0")]
    [InlineData("v10.11.11", "10.11.11.0")]
    [InlineData("12.3-rc1", "12.3.0.0")]
    [InlineData("V12", "12.0.0.0")]
    [InlineData(" v10.11.0 ", "10.11.0.0")]
    public void TryParse_IsLenient(string tag, string expected)
    {
        Assert.True(ReleaseVersion.TryParse(tag, out var version));
        Assert.Equal(Version.Parse(expected), version);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("v")]
    [InlineData("v.")]
    [InlineData("99999999999.1")]
    public void TryParse_RejectsNonVersions(string? tag) => Assert.False(ReleaseVersion.TryParse(tag, out _));

    [Fact]
    public void Normalize_HasFourParts()
    {
        Assert.Equal(new Version(12, 2, 0, 0), ReleaseVersion.Normalize(new Version(12, 2)));
        Assert.Equal(new Version(10, 11, 11, 0), ReleaseVersion.Normalize(new Version(10, 11, 11)));
        Assert.Equal(new Version(1, 2, 3, 4), ReleaseVersion.Normalize(new Version(1, 2, 3, 4)));
    }
}
