using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.Templates;
using Telegram.Bot.Extensions;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Templates;

public sealed class TemplateFitterTests
{
    private const int Limit = 1024;
    private const int Budget = Limit - TemplateFitter.DelayedReserve;

    private static readonly TemplateEngine Engine = new(TimeProvider.System);

    private static Func<IReadOnlyDictionary<string, object?>, TemplateRenderResult> Renderer(string template) =>
        values => Engine.Render("k", template, values, strict: false);

    private static IReadOnlyDictionary<string, object?> Values(string title, string? overview, int items = 0) =>
        new Dictionary<string, object?>
        {
            ["title"] = title,
            ["overview"] = overview,
            ["items"] = Enumerable.Range(1, items).Select(i => (object?)("line " + i)).ToList(),
            ["items_more"] = 0L,
        };

    [Fact]
    public void With_NullText_KeepsKeyAsNull()
    {
        var model = TemplateModel.Of(Values("T", "short"), "overview", null, null);

        var copy = model.With(null, 0);

        Assert.True(copy.ContainsKey("overview"));
        Assert.Null(copy["overview"]);
        Assert.Equal("short", model.Values["overview"]);
    }

    [Fact]
    public void Fit_ShortText_Unchanged()
    {
        var model = TemplateModel.Of(Values("T", "short"), "overview", null, null);

        var result = TemplateFitter.Fit(model, Renderer("{{ title }}\n\n{{ overview }}"), Limit);

        Assert.Equal("T\n\nshort", result.Html);
        Assert.False(result.Truncated);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Fit_LongOverview_PrefixWithEllipsis()
    {
        var overview = string.Concat(Enumerable.Repeat("word ", 400));
        var model = TemplateModel.Of(Values("T", overview), "overview", null, null);

        var result = TemplateFitter.Fit(model, Renderer("{{ title }}\n\n{{ overview }}"), Limit);

        Assert.True(HtmlText.PlainLength(result.Html!) <= Budget);
        Assert.EndsWith("…", result.Html, StringComparison.Ordinal);
        Assert.False(result.Html!.EndsWith(" …", StringComparison.Ordinal));
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Fit_OverviewUnder20Runes_Dropped()
    {
        var model = TemplateModel.Of(Values(new string('t', 970), "0123456789 abcdefghij"), "overview", null, null);

        var result = TemplateFitter.Fit(model, Renderer("{{ title }}\n\n{{ overview }}"), Limit);

        Assert.DoesNotContain("0123", result.Html, StringComparison.Ordinal);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Fit_List_KeepsMostAndSetsMore()
    {
        const string Template = "{{ title }}\n{{ for i in items }}{{ i }}\n{{ end }}{{ if items_more > 0 }}+{{ items_more }} more{{ end }}";
        var values = Values("T", null, 200);
        var render = Renderer(Template);
        var expectedKept = 1;
        for (var kept = 199; kept >= 1; kept--)
        {
            var attempt = new Dictionary<string, object?>(values)
            {
                ["items"] = ((List<object?>)values["items"]!).Take(kept).ToList(),
                ["items_more"] = (long)(200 - kept),
            };
            if (HtmlText.PlainLength(render(attempt).Output!) <= Budget)
            {
                expectedKept = kept;
                break;
            }
        }

        var result = TemplateFitter.Fit(TemplateModel.Of(values, null, "items", "items_more"), render, Limit);

        Assert.Contains($"line {expectedKept}\n", result.Html, StringComparison.Ordinal);
        Assert.DoesNotContain($"line {expectedKept + 1}\n", result.Html, StringComparison.Ordinal);
        Assert.Contains($"+{200 - expectedKept} more", result.Html, StringComparison.Ordinal);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Fit_ListOver1000_CapsBeforeRendering()
    {
        const string Template = "{{ for i in items }}{{ i }}\n{{ end }}{{ if items_more > 0 }}+{{ items_more }} more{{ end }}";

        var result = TemplateFitter.Fit(TemplateModel.Of(Values("T", null, 1500), null, "items", "items_more"), Renderer(Template), 100000);

        Assert.Null(result.Error);
        Assert.Contains("+500 more", result.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Fit_OutputOverTheEngineLimit_ShrinksTheList()
    {
        // 1000 items of 30 characters are over the engine's 16,384-character output limit.
        const string Template = "{{ for i in items }}{{ i }}\n{{ end }}{{ if items_more > 0 }}+{{ items_more }} more{{ end }}";
        var values = new Dictionary<string, object?>
        {
            ["items"] = Enumerable.Range(1, 1200).Select(i => (object?)$"item {i:0000} {new string('x', 19)}").ToList(),
            ["items_more"] = 0L,
        };

        var result = TemplateFitter.Fit(TemplateModel.Of(values, null, "items", "items_more"), Renderer(Template), 4096);

        Assert.Null(result.Error);
        Assert.False(result.Truncated);
        Assert.True(HtmlText.PlainLength(result.Html!) <= 4096 - TemplateFitter.DelayedReserve);
        Assert.Contains("more", result.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Fit_OutputOverTheEngineLimit_NothingToShrink_ReturnsTheError()
    {
        var result = TemplateFitter.Fit(TemplateModel.Of(Values("T", null), null, null, null), Renderer("{{ for i in 1..900 }}{{ \"x\" | string.pad_right 100 }}{{ end }}"), Limit);

        Assert.NotNull(result.Error);
        Assert.Null(result.Html);
    }

    [Fact]
    public void Fit_TextOverTheEngineStringLimit_IsShortened()
    {
        var model = TemplateModel.Of(Values("T", string.Concat(Enumerable.Repeat("word ", 5000))), "overview", null, null);

        var result = TemplateFitter.Fit(model, Renderer("{{ title }}\n\n{{ overview }}"), Limit);

        Assert.Null(result.Error);
        Assert.False(result.Truncated);
        Assert.EndsWith("word…", result.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Fit_MinListKeptZero_CanKeepNoItem()
    {
        var values = new Dictionary<string, object?> { ["items"] = new List<object?> { new string('a', 2000), "b" }, ["items_more"] = 0L };
        var list = (List<object?>)values["items"]!;
        var model = TemplateModel.Custom(
            values,
            null,
            list.Count,
            (source, _, kept) => new Dictionary<string, object?>(source) { ["items"] = list.Take(kept).ToList(), ["items_more"] = (long)(list.Count - kept) },
            minListKept: 0);

        var result = TemplateFitter.Fit(model, Renderer("Head{{ for i in items }}\n{{ i }}{{ end }}\n+{{ items_more }} more"), Limit);

        Assert.Equal("Head\n+2 more", result.Html);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Fit_StillTooLong_TruncatesAndFlags()
    {
        var model = TemplateModel.Of(Values(new string('t', 3000), null), "overview", null, null);

        var result = TemplateFitter.Fit(model, Renderer("{{ title }}"), Limit);

        Assert.True(result.Truncated);
        Assert.Equal(Budget, HtmlText.PlainLength(result.Html!));
    }

    [Fact]
    public void Fit_RenderError_ReturnsError()
    {
        var model = TemplateModel.Of(Values("T", null), "overview", null, null);

        var result = TemplateFitter.Fit(model, Renderer("{{ for }}"), Limit);

        Assert.NotNull(result.Error);
        Assert.Null(result.Html);
    }
}
