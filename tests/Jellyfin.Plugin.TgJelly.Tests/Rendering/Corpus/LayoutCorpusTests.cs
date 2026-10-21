using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Rendering.Corpus;

public class LayoutCorpusTests
{
    public static TheoryData<string> Names() => [.. LayoutCorpus.Cases.Select(c => c.Name)];

    [Fact]
    public void Write_WhenRequested()
    {
        if (Environment.GetEnvironmentVariable("TGJELLY_WRITE_GOLDEN") != "1")
        {
            return;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Jellyfin.Plugin.TgJelly.Tests.csproj")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var target = Path.Combine(dir.FullName, "Rendering", "Golden", "Corpus");
        Directory.CreateDirectory(target);
        var utf8 = new UTF8Encoding(false);
        foreach (var c in LayoutCorpus.Cases)
        {
            var (caption, text) = CorpusRig.RenderV02(c);
            File.WriteAllText(Path.Combine(target, $"{c.Name}.caption.txt"), caption.Replace("\r\n", "\n", StringComparison.Ordinal), utf8);
            File.WriteAllText(Path.Combine(target, $"{c.Name}.text.txt"), text.Replace("\r\n", "\n", StringComparison.Ordinal), utf8);
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void V02_MatchesGolden(string name)
    {
        var c = LayoutCorpus.Cases.Single(x => x.Name == name);

        var (caption, text) = CorpusRig.RenderV02(c);

        Assert.Equal(File.ReadAllText(LayoutCorpus.GoldenPath(name, "caption")), caption);
        Assert.Equal(File.ReadAllText(LayoutCorpus.GoldenPath(name, "text")), text);
    }
}
