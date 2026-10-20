using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Templates;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Templates;

public sealed class TemplateFieldCatalogTests
{
    public static TheoryData<string> Keys()
    {
        var data = new TheoryData<string>();
        foreach (var key in EventCatalog.AllKeys)
        {
            data.Add(key);
        }

        return data;
    }

    [Fact]
    public void EveryCatalogKey_HasFields()
    {
        foreach (var key in EventCatalog.AllKeys)
        {
            var fields = TemplateFieldCatalog.For(key);
            Assert.NotEmpty(fields);
            Assert.Equal(fields.Count, fields.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count());
            Assert.All(fields, f => Assert.Contains(f.Type, new[] { "string", "number", "bool", "string[]", "episode[]" }));
            Assert.All(fields, f => Assert.NotEmpty(f.Description));
        }
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void CommonFields_OnEveryKey(string key)
    {
        var names = TemplateFieldCatalog.For(key).Select(f => f.Name).ToList();

        Assert.Equal(["event", "at_unix", "at_text"], names.Take(3));
        Assert.Equal(TemplateFieldCatalog.IsMedia(key), key.StartsWith("media.", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void SampleFull_RendersEveryTemplateFieldStrictly(string key)
    {
        var template = new StringBuilder();
        foreach (var field in TemplateFieldCatalog.For(key))
        {
            template.Append(field.Type switch
            {
                "string[]" => $"{{{{ for x in {field.Name} }}}}{{{{ x }}}}{{{{ end }}}}",
                "episode[]" => $"{{{{ for x in {field.Name} }}}}{{{{ x.number }}}}{{{{ x.title }}}}{{{{ end }}}}",
                _ => $"{{{{ {field.Name} }}}}",
            }).Append('|');
        }

        var engine = new TemplateEngine(TimeProvider.System);
        Assert.Empty(engine.Parse(template.ToString()));
        var result = engine.Render(key, template.ToString(), TemplateFieldCatalog.SampleFull(key), strict: true);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal(TemplateFieldCatalog.For(key).Select(f => f.Name).Order(StringComparer.Ordinal), TemplateFieldCatalog.SampleFull(key).Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void SampleSparse_OptionalFieldsNull(string key)
    {
        var sparse = TemplateFieldCatalog.SampleSparse(key);

        foreach (var field in TemplateFieldCatalog.For(key))
        {
            Assert.True(sparse.ContainsKey(field.Name));
            if (field.Type.EndsWith("[]", StringComparison.Ordinal))
            {
                Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<object?>>(sparse[field.Name]));
            }
            else if (field.Optional)
            {
                Assert.Null(sparse[field.Name]);
            }
            else
            {
                Assert.NotNull(sparse[field.Name]);
            }
        }
    }

    [Fact]
    public void Samples_AreFreshCopies()
    {
        var full = TemplateFieldCatalog.SampleFull("media.episodes");
        var list = Assert.IsAssignableFrom<IList<object?>>(full["episodes"]);
        ((IDictionary<string, object?>)list[0]!)["title"] = "changed";
        list.Clear();
        Assert.IsAssignableFrom<IList<object?>>(TemplateFieldCatalog.SampleSparse("media.episodes")["episodes"]).Add("x");

        var next = Assert.IsAssignableFrom<IReadOnlyList<object?>>(TemplateFieldCatalog.SampleFull("media.episodes")["episodes"]);
        Assert.Equal(3, next.Count);
        Assert.Equal("Hello, Ms. Cobel", ((IReadOnlyDictionary<string, object?>)next[0]!)["title"]);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<object?>>(TemplateFieldCatalog.SampleSparse("media.episodes")["episodes"]));
    }
}
