using System;
using System.IO;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Templates;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Templates;

public sealed class TemplateDocsTests
{
    [Fact]
    public void Docs_MatchCatalog()
    {
        var path = Path.Combine(RepoRoot(), "docs", "templates.md");
        var generated = TemplateDocs.Render();

        if (Environment.GetEnvironmentVariable("TGJELLY_WRITE_DOCS") == "1")
        {
            File.WriteAllText(path, generated);
            return;
        }

        Assert.Equal(generated, File.ReadAllText(path));
    }

    [Fact]
    public void Docs_ExamplesRender_WithFullAndSparseSamples()
    {
        var engine = new TemplateEngine(TimeProvider.System);
        foreach (var key in EventCatalog.AllKeys)
        {
            var example = TemplateDocs.Example(key);
            Assert.Empty(engine.Parse(example));
            Assert.True(engine.Render(key, example, TemplateFieldCatalog.SampleFull(key), strict: true).Ok, key);
            Assert.True(engine.Render(key, example, TemplateFieldCatalog.SampleSparse(key), strict: true).Ok, key);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Jellyfin.Plugin.TgJelly.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
