using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.Templates;
using Telegram.Bot.Extensions;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Templates;

public sealed class BuiltInTemplatesTests
{
    public static TheoryData<string> EnKeys() => [.. BuiltInTemplates.Keys("en")];

    [Fact]
    public void EveryTemplateKey_HasABuiltIn()
    {
        // Messages have no other text source, so a key without a built-in template would only get the last-resort text.
        Assert.Equal(TemplateKeys.All.Order(StringComparer.Ordinal), BuiltInTemplates.Keys("en"));
        Assert.All(TemplateKeys.All, key => Assert.False(string.IsNullOrWhiteSpace(BuiltInTemplates.Get(key, "en"))));
    }

    [Fact]
    public void UnknownLanguage_FallsBackToEn()
    {
        Assert.Equal(BuiltInTemplates.Get("media.movie", "en"), BuiltInTemplates.Get("media.movie", "xx"));
        Assert.Null(BuiltInTemplates.Get("media.nope", "en"));
        Assert.Null(BuiltInTemplates.Get("media.nope", "xx"));
    }

    [Fact]
    public void Text_HasNoCarriageReturns()
    {
        foreach (var key in BuiltInTemplates.Keys("en"))
        {
            Assert.DoesNotContain('\r', BuiltInTemplates.Get(key, "en")!);
        }
    }

    [Theory]
    [MemberData(nameof(EnKeys))]
    public void Validate_EachBuiltIn_NoErrors(string key)
    {
        var engine = new TemplateEngine(TimeProvider.System);
        var text = BuiltInTemplates.Get(key, "en")!;

        foreach (var sample in new[] { TemplateFieldCatalog.SampleFull(key), TemplateFieldCatalog.SampleSparse(key) })
        {
            var result = engine.Render(key, text, sample, strict: true);

            Assert.True(result.Ok, result.Error?.ToString());
            Assert.Empty(TelegramHtmlValidator.Validate(result.Output!));
            Assert.False(string.IsNullOrWhiteSpace(HtmlText.ToPlain(result.Output!)));
        }
    }

    [Fact]
    public void Movie_FullSample_RendersV02Layout()
    {
        var engine = new TemplateEngine(TimeProvider.System);
        var sample = new Dictionary<string, object?>(TemplateFieldCatalog.SampleFull("media.movie")) { ["upgraded"] = true };

        var result = engine.Render("media.movie", BuiltInTemplates.Get("media.movie", "en")!, sample, strict: true);

        Assert.Equal(
            "<b>Arrival</b> (2016)\n★ 7.9 · 1h 56m · Drama, Sci-Fi\n\nA linguist works with the military to talk to alien visitors.\n\n♻️ Upgraded\n📁 Movies",
            result.Output);
    }
}
