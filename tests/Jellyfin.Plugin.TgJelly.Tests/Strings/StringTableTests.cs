using Jellyfin.Plugin.TgJelly.Strings;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Strings;

public class StringTableTests
{
    private static readonly StringTable Strings = StringTable.LoadEmbedded();

    [Fact]
    public void Get_FillsNamedPlaceholders()
        => Assert.Equal("Connected as @jf_bot", Strings.Get("connection.Connected", ("bot", "jf_bot")));

    [Fact]
    public void Get_LeavesAMissingArgumentInPlace()
        => Assert.Equal("Connected as @{bot}", Strings.Get("connection.Connected"));

    [Theory]
    [InlineData(1, "+1 more security/server event, see the dashboard")]
    [InlineData(2, "+2 more security/server events, see the dashboard")]
    [InlineData(1000, "+1,000 more security/server events, see the dashboard")]
    public void Plural_PicksOneOrOther_AndFormatsTheCount(long count, string expected)
        => Assert.Equal(expected, Strings.Plural("outbox.summary", count));

    [Fact]
    public void UnknownKey_ReturnsTheKey() => Assert.Equal("no.such.key", Strings.Get("no.such.key"));

    [Fact]
    public void AnUnknownLanguage_FallsBackToEnglish()
        => Assert.Equal("Token rejected", StringTable.LoadEmbedded("xx").Get("connection.TokenRejected"));

    [Fact]
    public void EveryKeyThePluginUses_Exists()
    {
        foreach (var key in StringKeys.All)
        {
            Assert.NotEqual(key, Strings.Get(key));
        }
    }
}
