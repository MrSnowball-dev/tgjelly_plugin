using System.IO;
using System.Linq;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Templates;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Rendering.Corpus;

public class LayoutCorpusTests
{
    public static TheoryData<string> Names() => [.. LayoutCorpus.Cases.Select(c => c.Name)];

    [Theory]
    [MemberData(nameof(Names))]
    public void Templates_MatchGolden(string name)
    {
        var c = LayoutCorpus.Cases.Single(x => x.Name == name);

        var (caption, text) = CorpusRig.RenderTemplates(c);

        Assert.Equal(File.ReadAllText(LayoutCorpus.GoldenPath(name, "caption")), caption);
        Assert.Equal(File.ReadAllText(LayoutCorpus.GoldenPath(name, "text")), text);
    }

    [Fact]
    public void Templates_CoverEveryMediaCase()
    {
        // A case whose key has no built-in template would only get the last-resort text.
        var media = LayoutCorpus.Cases.Where(c => c.Model.Kind == RenderKind.Media).ToList();

        Assert.NotEmpty(media);
        Assert.All(media, c => Assert.True(TemplateRenderer.HasBuiltIn(c.Model.EventKey), c.Name));
    }

    [Fact]
    public void Templates_CoverEveryAlertCase()
    {
        var alerts = LayoutCorpus.Cases.Where(c => c.Model.Kind == RenderKind.Alert).ToList();

        Assert.NotEmpty(alerts);
        Assert.All(alerts, c => Assert.True(TemplateRenderer.HasBuiltIn(c.Model.EventKey), c.Name));
    }
}
