using System.Linq;
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

    [Theory]
    [InlineData("en", "en")]
    [InlineData("EN-us", "en")]
    [InlineData("pt-BR", "en")]
    [InlineData(null, "en")]
    [InlineData("", "en")]
    public void BestMatch_PicksExactThenPrimarySubtagThenEnglish(string? lang, string expected)
        => Assert.Equal(expected, StringTable.BestMatch(lang));

    [Fact]
    public void EmbeddedLanguages_ListsEnglish() => Assert.Contains("en", StringTable.EmbeddedLanguages);

    [Fact]
    public void Snapshot_HasEveryEnglishKey()
    {
        var english = StringTable.LoadEmbedded("en").Snapshot();

        Assert.NotEmpty(english);
        Assert.Equal("Connected as @{bot}", english["connection.Connected"]);
        Assert.Equal(english.Keys.Order(), StringTable.LoadEmbedded("xx").Snapshot().Keys.Order());
    }

    [Fact]
    public void WebStrings_HaveNoDecorativeSymbols()
    {
        string[] symbols = ["\u25CF", "\u2713", "\u2714", "\u2705", "\u26A0", "\u274C", "\u2717", "\uD83D\uDFE2", "\uD83D\uDD34"];

        foreach (var (key, value) in StringTable.LoadEmbedded("en").Snapshot().Where(pair => pair.Key.StartsWith("web.", System.StringComparison.Ordinal)))
        {
            foreach (var symbol in symbols)
            {
                Assert.False(value.Contains(symbol, System.StringComparison.Ordinal), $"{key} contains a decorative symbol");
            }
        }
    }

    [Fact]
    public void Strings_NoVagueErrors()
    {
        foreach (var (key, value) in StringTable.LoadEmbedded("en").Snapshot())
        {
            Assert.False(value.Contains("unknown error", System.StringComparison.OrdinalIgnoreCase), $"{key} says 'unknown error'");
            Assert.False(value.Contains("something went wrong", System.StringComparison.OrdinalIgnoreCase), $"{key} says 'something went wrong'");
            Assert.False(
                value.Trim().TrimEnd('.', '!').Equals("try again", System.StringComparison.OrdinalIgnoreCase) && !key.EndsWith("tryAgain", System.StringComparison.Ordinal),
                $"{key} is only 'try again'");
        }
    }
}
