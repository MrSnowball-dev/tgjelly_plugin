using System;
using System.Collections.Generic;
using System.Diagnostics;
using Jellyfin.Plugin.TgJelly.Templates;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Templates;

public sealed class TemplateEngineTests
{
    private static readonly IReadOnlyDictionary<string, object?> Empty = new Dictionary<string, object?>();

    private static TemplateEngine Engine() => new(TimeProvider.System);

    private static IReadOnlyDictionary<string, object?> Model(string key, object? value) =>
        new Dictionary<string, object?> { [key] = value };

    [Fact]
    public void Escapes_ExpressionResults()
    {
        var result = Engine().Render("k", "{{ x }}", Model("x", "<b>&"), strict: true);
        Assert.True(result.Ok);
        Assert.Equal("&lt;b&gt;&amp;", result.Output);
    }

    [Fact]
    public void Raw_IsNotEscaped()
    {
        var result = Engine().Render("k", "{{ raw x }}", Model("x", "<b>&"), strict: true);
        Assert.Equal("<b>&", result.Output);
    }

    [Fact]
    public void Literal_IsNotEscaped()
    {
        var result = Engine().Render("k", "<b>{{ x }}</b>", Model("x", "<i>"), strict: true);
        Assert.Equal("<b>&lt;i&gt;</b>", result.Output);
    }

    [Fact]
    public void ParseErrors_Are1Based()
    {
        var errors = Engine().Parse("a\n{{ if }}");
        Assert.NotEmpty(errors);
        Assert.Equal(2, errors[0].Line);
        Assert.True(errors[0].Column >= 1);
    }

    [Fact]
    public void Parse_ValidTemplate_HasNoErrors() => Assert.Empty(Engine().Parse("hello {{ x }}"));

    [Fact]
    public void LoopLimit_Trips()
    {
        var result = Engine().Render("k", "{{ for i in 1..100000 }}x{{ end }}", Empty, strict: false);
        Assert.False(result.Ok);
        Assert.Contains("LoopLimit", result.Error!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.Output);
    }

    [Fact]
    public void OutputLimit_Trips()
    {
        var result = Engine().Render("k", "{{ for i in 1..900 }}{{ \"x\" | string.pad_right 100 }}{{ end }}", Empty, strict: false);
        Assert.False(result.Ok);
        Assert.Null(result.Output);
        Assert.Contains("OutputLimit", result.Error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Recursion_Trips()
    {
        var result = Engine().Render("k", "{{ func f; f; end; f }}", Empty, strict: false);
        Assert.False(result.Ok);
        Assert.Null(result.Output);
        Assert.Contains("recursive depth limit", result.Error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Timeout_Trips()
    {
        var engine = new TemplateEngine(TimeProvider.System, TimeSpan.FromMilliseconds(50));
        const string Text = "{{ for a in 1..999 }}{{ for b in 1..999 }}{{ for c in 1..999 }}{{ for d in 1..999 }}{{ end }}{{ end }}{{ end }}{{ end }}";
        var stopwatch = Stopwatch.StartNew();
        var result = engine.Render("k", Text, Empty, strict: false);
        Assert.False(result.Ok);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public void ModelKeyNamedRaw_DoesNotReplaceTheFunction()
    {
        var result = Engine().Render("k", "{{ raw x }}", new Dictionary<string, object?> { ["raw"] = "no", ["x"] = "<b>" }, strict: false);
        Assert.Equal("<b>", result.Output);
    }

    [Fact]
    public void Strict_ReportsUnknownField_WithPosition()
    {
        var result = Engine().Render("k", "a\n{{ nope }}", Empty, strict: true);
        Assert.False(result.Ok);
        Assert.Equal(2, result.Error!.Line);
        Assert.True(result.Error.Column >= 1);
    }

    [Fact]
    public void NotStrict_UnknownFieldIsEmpty()
    {
        var result = Engine().Render("k", "a{{ nope }}b", Empty, strict: false);
        Assert.True(result.Ok);
        Assert.Equal("ab", result.Output);
    }

    [Fact]
    public void ArraysAndObjects_Render()
    {
        var episodes = new List<object?>
        {
            new Dictionary<string, object?> { ["number"] = 1, ["title"] = "A" },
            new Dictionary<string, object?> { ["number"] = 2, ["title"] = "B" },
        };
        var result = Engine().Render("k", "{{ for e in episodes }}{{ e.number }}{{ e.title }};{{ end }}", Model("episodes", episodes), strict: true);
        Assert.Equal("1A;2B;", result.Output);
    }

    [Fact]
    public void SameText_IsParsedOnce()
    {
        var engine = Engine();
        engine.Render("k", "{{ x }}", Model("x", 1), strict: true);
        engine.Render("k", "{{ x }}", Model("x", 2), strict: true);
        Assert.Equal(1, engine.CachedCount);
        engine.Render("k", "{{ x }} ", Model("x", 2), strict: true);
        Assert.Equal(2, engine.CachedCount);
    }

    [Fact]
    public void Hash_IsStable16LowerHex()
    {
        var hash = TemplateText.Hash("abc");
        Assert.Equal("ba7816bf8f01cfea", hash);
    }
}
