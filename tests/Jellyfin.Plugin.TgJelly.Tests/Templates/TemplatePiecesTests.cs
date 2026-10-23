using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Library;
using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Templates;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot.Extensions;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Templates;

// The hover link between template and preview (design §6.5): a template's top-level pieces, and the preview-only render
// that marks each piece's output. The sent message never sees the markers.
public sealed class TemplatePiecesTests : IDisposable
{
    private static readonly char[] Markers = [TemplateMarkers.Start, TemplateMarkers.IndexEnd, TemplateMarkers.End];

    private readonly BotTestHarness _h = new();
    private readonly FakeLibrary _library = new();
    private readonly TemplateEngine _engine;
    private readonly TemplateModelBuilder _models;
    private readonly TemplateRenderer _renderer;
    private readonly TemplateSample _sample;

    public TemplatePiecesTests()
    {
        _engine = new TemplateEngine(_h.Time);
        _models = new TemplateModelBuilder(_library, new LinkBuilder(new FakeServerInfo()), _h.Settings, new Formatter(_h.Strings), _h.Time);
        _renderer = new TemplateRenderer(_engine, _models, new TemplateFlags(_h.State, _h.Settings, _h.Time), _h.Settings, _h.Strings, NullLogger<TemplateRenderer>.Instance);
        _sample = new TemplateSample(_renderer, _engine, _models, _h.Settings, _h.Strings);
        _h.Settings.Update(null, c => c.PublicUrl = "https://jf.example.com");
    }

    public void Dispose() => _h.Dispose();

    public static TheoryData<string> Templates() => new()
    {
        "<b>{{ title }}</b> ({{ year }})",
        "Hi {{ title }}!\n{{- if year }}\nYear: {{ year }}\n{{ else }}none{{ end -}}\n\nend",
        "{{~ title ~}}\n  and {{ year -}}\n\n\n tail",
        "{{ for g in genres }}[{{ g }}]{{ end }} {{ if nope_field }}x{{ end }}",
        "{{ title }}{{ year }}{{ title; year }}",
        "plain text only",
        string.Empty,
        "{{ if year }}{{ year }}{{ end }}",
        "<a href=\"{{ title }}\">{{ raw \"<i>x</i>\" }}</a> &amp; {{ plural 2 \"a\" \"b\" }}",
    };

    [Fact]
    public void Pieces_TwoFieldsOneLine_AreSeparatePieces()
    {
        const string text = "<b>{{ title }}</b> ({{ year }})";

        var pieces = _engine.Pieces(text);

        Assert.Equal(["<b>", "{{ title }}", "</b> (", "{{ year }}", ")"], pieces.Select(p => text.Substring(p.Start, p.Length)));
        Assert.Equal([0, 1, 2, 3, 4], pieces.Select(p => p.Index));
    }

    [Fact]
    public void Pieces_IfBlock_IsOnePiece()
    {
        const string text = "A {{ if year }}({{ year }}){{ else }}-{{ end }} B\n{{- title -}}\n";

        var pieces = _engine.Pieces(text);

        // The whitespace a {{- or -}} trims belongs to no piece.
        Assert.Equal(["A ", "{{ if year }}({{ year }}){{ else }}-{{ end }}", " B", "{{- title -}}"], pieces.Select(p => text.Substring(p.Start, p.Length)));
    }

    [Fact]
    public void Pieces_ParseError_None()
    {
        Assert.Empty(_engine.Pieces("{{ if }}"));
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public void MarkedRender_StrippedEqualsUnmarked(string text)
    {
        var model = TemplateFieldCatalog.SampleFull("media.movie");

        var plain = _engine.Render("media.movie", text, model, strict: false);
        var marked = _engine.RenderMarked(text, model);

        Assert.True(plain.Ok, plain.Error?.Message);
        Assert.True(marked.Ok, marked.Error?.Message);
        Assert.Equal(plain.Output, TemplateMarkers.Strip(marked.Output!));
        var pieces = _engine.Pieces(text);
        Assert.Equal(pieces.Count, marked.Output!.Count(c => c == TemplateMarkers.Start));
        Assert.Equal(pieces.Count, marked.Output!.Count(c => c == TemplateMarkers.End));
    }

    [Fact]
    public void MarkedRender_EachPieceWrapsItsOwnOutput()
    {
        var model = new Dictionary<string, object?> { ["title"] = "Dune", ["year"] = 2021 };

        var marked = _engine.RenderMarked("<b>{{ title }}</b> ({{ year }})", model).Output!;

        Assert.Equal("\uE0000\uE001<b>\uE002\uE0001\uE001Dune\uE002\uE0002\uE001</b> (\uE002\uE0003\uE0012021\uE002\uE0004\uE001)\uE002", marked);
    }

    [Theory]
    [InlineData("media.movie")]
    [InlineData("media.episodes")]
    [InlineData("media.album")]
    [InlineData("media.digest")]
    [InlineData("security.login_failed")]
    [InlineData("server.update_available")]
    public void MarkedRender_BuiltIns_StrippedEqualsSample(string key)
    {
        var builtIn = BuiltInTemplates.Get(key, "en")!;

        var sample = _sample.Build(key, builtIn);

        Assert.Null(sample.Error);
        Assert.Contains(TemplateMarkers.Start, sample.MarkedHtml!);
        Assert.Equal(sample.Html, TemplateMarkers.Strip(sample.MarkedHtml!));
    }

    [Fact]
    public void MarkedRender_Shortened_StrippedEqualsFitted()
    {
        // The overview shrinks first, then the whole text is cut: the visible part still maps (design §6.5).
        foreach (var text in new[] { "{{ title }} {{ overview }}", "<b>{{ title }}</b> " + new string('a', 1200) + " {{ year }}" })
        {
            var sample = _sample.Build("media.movie", text);

            Assert.Null(sample.Error);
            Assert.True(sample.Length > 0);
            Assert.Contains(TemplateMarkers.Start, sample.MarkedHtml!);
            Assert.Equal(sample.Html, TemplateMarkers.Strip(sample.MarkedHtml!));
        }

        var cut = _sample.Build("media.movie", "<b>{{ title }}</b> " + new string('a', 1200) + " {{ year }}");
        Assert.True(HtmlText.PlainLength(cut.Html!) <= TemplateFitter.CaptionLimit);
        Assert.Equal(cut.MarkedHtml!.Count(c => c == TemplateMarkers.Start), cut.MarkedHtml!.Count(c => c == TemplateMarkers.End));
    }

    [Fact]
    public void Align_CutHtml_KeepsMarkersOfTheVisiblePart()
    {
        var marked = "\uE0000\uE001<b>Dune</b>\uE002\uE0001\uE001 abcdef\uE002\uE0002\uE001 tail\uE002";

        var aligned = TemplateMarkers.Align(marked, "<b>Dune</b> abc…");

        Assert.Equal("\uE0000\uE001<b>Dune</b>\uE002\uE0001\uE001 abc\uE002…", aligned);
        Assert.Equal("<b>Dune</b> abc…", TemplateMarkers.Strip(aligned));
    }

    [Fact]
    public void SentMessage_NeverHasMarkers()
    {
        const string text = "<b>{{ title }}</b> ({{ year }}) {{ if overview }}{{ overview }}{{ end }}";
        _h.Settings.Update(null, c => c.Templates = [new TemplateOverride { EventKey = "media.movie", Enabled = true, Text = text }]);
        var item = _library.Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Movie, Name = "Arrival", Year = 2016, Overview = "Aliens." });
        var model = new RenderModel { Kind = RenderKind.Media, EventKey = "media.movie", At = _h.Time.GetUtcNow(), ItemIds = [item.Id.ToString("N")] };

        var sent = _renderer.Render(model, hasPoster: true)!;
        var sample = _sample.Build("media.movie", text);

        Assert.Equal("<b>Arrival</b> (2016) Aliens.", sent.Caption);
        foreach (var output in new[] { sent.Caption, sent.Text, sample.Html!, sample.TextHtml! })
        {
            Assert.Equal(-1, output.IndexOfAny(Markers));
        }

        Assert.Contains(TemplateMarkers.Start, sample.MarkedHtml!);
    }
}
