using Jellyfin.Plugin.TgJelly.Templates;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Templates;

public sealed class PluralRulesTests
{
    [Fact]
    public void En_OneAndOther()
    {
        string[] forms = ["episode", "episodes"];

        Assert.Equal("episode", PluralRules.Pick("en", 1, forms));
        Assert.Equal("episodes", PluralRules.Pick("en", 0, forms));
        Assert.Equal("episodes", PluralRules.Pick("en", 2, forms));
    }

    [Fact]
    public void MissingForms_UseLast()
    {
        Assert.Equal("x", PluralRules.Pick("en", 2, ["x"]));
        Assert.Equal(string.Empty, PluralRules.Pick("en", 2, []));
    }

    [Fact]
    public void UnknownLanguage_UsesEnglish()
    {
        Assert.Equal("a", PluralRules.Pick("zz", 1, ["a", "b"]));
        Assert.Equal("b", PluralRules.Pick("zz", 5, ["a", "b"]));
    }
}
