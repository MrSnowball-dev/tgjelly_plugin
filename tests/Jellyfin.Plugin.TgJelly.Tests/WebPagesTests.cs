using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests;

public class WebPagesTests
{
    [Fact]
    public void Pages_HtmlFirst_WithSpecNames()
    {
        using var dir = new TempDir();
        var pages = TestPlugin.Create(dir.Path).GetPages().ToList();

        Assert.Equal(["tgjelly", "tgjelly.js"], pages.Select(p => p.Name));
        Assert.True(pages[0].EnableInMainMenu);
        Assert.Equal("Telegram", pages[0].DisplayName);
        Assert.False(pages[1].EnableInMainMenu);
    }

    [Fact]
    public void EveryPage_IsAnEmbeddedResource()
    {
        using var dir = new TempDir();
        var resources = typeof(Plugin).Assembly.GetManifestResourceNames();

        foreach (var page in TestPlugin.Create(dir.Path).GetPages())
        {
            Assert.Contains(page.EmbeddedResourcePath, resources);
        }
    }

    [Fact]
    public void Html_MeetsTheWebClientsRules()
    {
        var html = ReadResource("Jellyfin.Plugin.TgJelly.Web.tgjelly.html");

        // viewContainer looks for this literal, with double quotes (F25).
        Assert.Contains("data-role=\"page\"", html, StringComparison.Ordinal);
        Assert.Contains("data-controller=\"__plugin/tgjelly.js\"", html, StringComparison.Ordinal);

        // translateHtml rewrites every ${...} in the HTML (F26); all JS lives in tgjelly.js.
        Assert.DoesNotContain("${", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Js_IsAnEsModuleWithDefaultExport_AndUsesApiClientUrls()
    {
        var js = ReadResource("Jellyfin.Plugin.TgJelly.Web.tgjelly.js");

        Assert.Contains("export default function", js, StringComparison.Ordinal);
        Assert.Contains("ApiClient.getUrl('TgJelly/", js, StringComparison.Ordinal);
        Assert.DoesNotContain("X-Emby-Token", js, StringComparison.Ordinal);
        Assert.DoesNotContain("api_key", js, StringComparison.Ordinal);
    }

    private static string ReadResource(string name)
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
