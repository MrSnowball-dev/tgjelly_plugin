using System;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Library;
using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Templates;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Scriban;
using Telegram.Bot.Extensions;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Templates;

public sealed class TemplateRendererTests : IDisposable
{
    private const string Key = "media.movie";
    private readonly BotTestHarness _h = new();
    private readonly FakeLibrary _library = new();
    private readonly ListLogger<TemplateRenderer> _logger = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public void Covers_KeysWithABuiltIn()
    {
        var renderer = Renderer();

        Assert.True(renderer.Covers("media.movie"));
        Assert.True(renderer.Covers("media.episodes"));
        Assert.False(renderer.Covers("media.nope"));
        Assert.Null(renderer.Render(new RenderModel { Kind = RenderKind.Media, EventKey = "media.nope" }, hasPoster: false));
    }

    [Fact]
    public void BuiltIn_NotKeyedNotFlagged()
    {
        var result = Renderer().Render(Movie(), hasPoster: true)!;

        Assert.Equal("<b>Arrival</b> (2016)\n\nA linguist works with the military to talk to alien visitors.", result.Text);
        Assert.Equal(result.Text, result.Caption);
        Assert.Null(result.TemplateKey);
        Assert.Null(StoredFlag());
    }

    [Fact]
    public void Edited_UsedAndKeyed()
    {
        Set("<b>New: {{ title }}</b>");

        var result = Renderer().Render(Movie(), hasPoster: true)!;

        Assert.Equal("<b>New: Arrival</b>", result.Caption);
        Assert.Equal("<b>New: Arrival</b>", result.Text);
        Assert.Equal(Key, result.TemplateKey);
        Assert.Null(StoredFlag());
    }

    [Fact]
    public void Edited_Blank_UsesBuiltIn()
    {
        var builtIn = Renderer().Render(Movie(), hasPoster: false)!;
        Set("  \n ");

        var result = Renderer().Render(Movie(), hasPoster: false)!;

        Assert.Equal(builtIn, result);
        Assert.Null(StoredFlag());
    }

    [Fact]
    public void EditedFails_FallsBackToBuiltInAndFlags()
    {
        var builtIn = Renderer().Render(Movie(), hasPoster: false)!;
        Set("line one\n{{ if }}");

        var result = Renderer().Render(Movie(), hasPoster: false)!;

        Assert.Equal(builtIn, result with { FallbackError = null });
        Assert.Null(result.TemplateKey);
        var flag = StoredFlag()!;
        Assert.Equal("error", flag.Kind);
        Assert.StartsWith("line 2, column ", flag.Error, StringComparison.Ordinal);
        Assert.Equal(TemplateText.Hash("line one\n{{ if }}"), flag.TextHash);
        Assert.Equal(flag.Error, result.FallbackError);
    }

    [Fact]
    public void EditedForbiddenTag_FallsBackAndFlagsTheTableText()
    {
        var builtIn = Renderer().Render(Movie(), hasPoster: false)!;
        Set("<h1>{{ title }}</h1>");

        var result = Renderer().Render(Movie(), hasPoster: false)!;

        Assert.Equal(builtIn, result with { FallbackError = null });
        Assert.Equal(_h.Strings.Get("templates.html.notAllowed", ("tag", "h1")), StoredFlag()!.Error);
    }

    [Theory]
    [InlineData("{{ if false }}x{{ end }}")]
    [InlineData("{{ if false }}x{{ end }}  \n  ")]
    [InlineData("<b> </b>")]
    [InlineData("{{ nope }}")]
    public void EditedEmptyOutput_FallsBack(string text)
    {
        var builtIn = Renderer().Render(Movie(), hasPoster: false)!;
        Set(text);

        var result = Renderer().Render(Movie(), hasPoster: false)!;

        Assert.Equal(builtIn, result with { FallbackError = null });
        var flag = StoredFlag()!;
        Assert.Equal("error", flag.Kind);
        Assert.Equal(_h.Strings.Get("templates.empty"), flag.Error);
    }

    [Fact]
    public void EditedTruncated_FlagsTruncated()
    {
        Set(new string('a', 2000));

        var result = Renderer().Render(Movie(), hasPoster: true)!;

        Assert.Equal(Key, result.TemplateKey);
        Assert.True(HtmlText.PlainLength(result.Caption) <= TemplateFitter.CaptionLimit - TemplateFitter.DelayedReserve);
        Assert.EndsWith("…", result.Caption, StringComparison.Ordinal);
        Assert.Equal(2000, result.Text.Length);
        var flag = StoredFlag()!;
        Assert.Equal("truncated", flag.Kind);
        Assert.Equal(string.Empty, flag.Error);
    }

    [Fact]
    public void EditedOverCaption_NoPoster_NotFlagged()
    {
        Set(new string('a', 2000));

        var result = Renderer().Render(Movie(), hasPoster: false)!;

        Assert.Equal(2000, result.Text.Length);
        Assert.Null(StoredFlag());
    }

    [Fact]
    public void EditedOverview_ShortenedByTheFitter_NotFlagged()
    {
        Set("<b>{{ title }}</b>\n{{ overview }}");

        var result = Renderer().Render(Movie(overview: new string('o', 3000)), hasPoster: true)!;

        Assert.Equal(Key, result.TemplateKey);
        Assert.EndsWith("o…", result.Caption, StringComparison.Ordinal);
        Assert.Null(StoredFlag());
    }

    [Fact]
    public void BuiltInTruncated_NotFlagged()
    {
        var result = Renderer().Render(Movie(name: new string('t', 3000)), hasPoster: true)!;

        Assert.True(HtmlText.PlainLength(result.Caption) <= TemplateFitter.CaptionLimit - TemplateFitter.DelayedReserve);
        Assert.Contains("…", result.Caption, StringComparison.Ordinal);
        Assert.Null(result.TemplateKey);
        Assert.Null(StoredFlag());
    }

    [Fact]
    public void BuiltInFails_LastResortText()
    {
        var builtIn = BuiltInTemplates.Get(Key, "en")!;
        var engine = new TemplateEngine(
            TimeProvider.System,
            TimeSpan.FromSeconds(2),
            text => text == builtIn ? throw new InvalidOperationException("parser broke") : Template.Parse(text));

        var result = Renderer(engine).Render(Movie(name: "Tom & Jerry"), hasPoster: true)!;

        Assert.Equal("<b>Media: Movies</b>\nTom &amp; Jerry", result.Caption);
        Assert.Equal(result.Caption, result.Text);
        Assert.Null(result.TemplateKey);
        Assert.Null(StoredFlag());
        var warning = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(Key, warning.Message, StringComparison.Ordinal);
        Assert.Contains("parser broke", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EditedAndBuiltInFail_FlagsTheEditedAndSendsLastResort()
    {
        var builtIn = BuiltInTemplates.Get(Key, "en")!;
        var engine = new TemplateEngine(
            TimeProvider.System,
            TimeSpan.FromSeconds(2),
            text => text == builtIn ? throw new InvalidOperationException("parser broke") : Template.Parse(text));
        Set("{{ if }}");

        var result = Renderer(engine).Render(Movie(), hasPoster: false)!;

        Assert.Equal("<b>Media: Movies</b>\nArrival", result.Text);
        Assert.Equal("error", StoredFlag()!.Kind);
    }

    [Fact]
    public void EditedPastTheFitDeadline_FallsBackToBuiltInAndFlagsTooSlow()
    {
        // Each render takes a second on the stepping clock. The built-in fits in 2 renders; this one needs many,
        // because its long literal never fits a caption and the fitter keeps trying shorter overviews.
        var engine = new TemplateEngine(new SteppingTime(TimeSpan.FromSeconds(1)));
        var builtIn = Renderer(engine).Render(Movie(), hasPoster: true)!;
        Set("{{ overview }}" + new string('a', 2000));

        var result = Renderer(engine).Render(Movie(), hasPoster: true)!;

        Assert.Equal(builtIn, result with { FallbackError = null });
        Assert.Null(result.TemplateKey);
        var flag = StoredFlag()!;
        Assert.Equal("error", flag.Kind);
        Assert.Equal(_h.Strings.Get("templates.tooSlow"), flag.Error);
    }

    [Fact]
    public void EditedWithinTheFitDeadline_IsUsed()
    {
        var engine = new TemplateEngine(new SteppingTime(TimeSpan.FromSeconds(1)));
        Set("<b>New: {{ title }}</b>");

        var result = Renderer(engine).Render(Movie(), hasPoster: true)!;

        Assert.Equal(Key, result.TemplateKey);
        Assert.Null(StoredFlag());
    }

    [Fact]
    public void BuiltInPastTheFitDeadline_LastResortText()
    {
        // A 3000-character overview makes the built-in search for the caption's overview length: many renders.
        var engine = new TemplateEngine(new SteppingTime(TimeSpan.FromSeconds(1)));

        var result = Renderer(engine).Render(Movie(overview: new string('o', 3000)), hasPoster: true)!;

        Assert.Equal("<b>Media: Movies</b>\nArrival", result.Text);
        Assert.Null(result.TemplateKey);
        var warning = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(_h.Strings.Get("templates.tooSlow"), warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowingModelBuilder_LastResortLabelOnly()
    {
        var model = Movie();
        _library.GetItemOverride = _ => throw new InvalidOperationException("library broke");

        var result = Renderer().Render(model, hasPoster: false)!;

        Assert.Equal("<b>Media: Movies</b>", result.Text);
        Assert.Equal(result.Text, result.Caption);
        Assert.Contains("library broke", Assert.Single(_logger.Entries, e => e.Level == LogLevel.Warning).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Digest_TextOnly_CaptionEmpty()
    {
        var item = _library.Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Movie, Name = "Dune", Year = 2021 });
        var group = new RenderModel { Kind = RenderKind.Media, EventKey = "media.movie", LibraryName = "Movies", ItemIds = [item.Id.ToString("N")] };

        var result = Renderer().Render(new RenderModel { Kind = RenderKind.Digest, EventKey = "media.digest", Groups = [group] }, hasPoster: false)!;

        Assert.Equal("🆕 <b>1 new item in Jellyfin</b>\n\n<b>Movies</b>: Dune (2021)", result.Text);
        Assert.Equal(string.Empty, result.Caption);
    }

    [Fact]
    public void Digest_LastResort_CaptionEmpty()
    {
        _library.GetItemOverride = _ => throw new InvalidOperationException("library broke");
        var group = new RenderModel { Kind = RenderKind.Media, EventKey = "media.movie", ItemIds = [Guid.NewGuid().ToString("N")] };

        var result = Renderer().Render(new RenderModel { Kind = RenderKind.Digest, EventKey = "media.digest", Groups = [group] }, hasPoster: false)!;

        Assert.Equal("<b>Digest</b>", result.Text);
        Assert.Equal(string.Empty, result.Caption);
    }

    [Fact]
    public void Plural_UsesTheConfiguredLanguage()
    {
        // Only English has a rule; another language falls back to it and to the English built-in.
        _h.Settings.Update(null, c => c.Language = "xx");
        var item = _library.Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.MusicAlbum, Name = "Kid A", AlbumArtist = "Radiohead", ChildCount = 1 });

        var result = Renderer().Render(new RenderModel { Kind = RenderKind.Media, EventKey = "media.album", ItemIds = [item.Id.ToString("N")] }, hasPoster: false)!;

        Assert.Equal("<b>Radiohead — Kid A</b>\n1 track", result.Text);
    }

    private TemplateRenderer Renderer(TemplateEngine? engine = null)
    {
        var formatter = new Formatter(_h.Strings);
        var models = new TemplateModelBuilder(_library, new LinkBuilder(new FakeServerInfo()), _h.Settings, formatter, _h.Time);
        return new TemplateRenderer(engine ?? new TemplateEngine(TimeProvider.System), models, new TemplateFlags(_h.State, _h.Settings, _h.Time), _h.Settings, _h.Strings, _logger);
    }

    private RenderModel Movie(string name = "Arrival", string? overview = "A linguist works with the military to talk to alien visitors.")
    {
        var item = _library.Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Movie, Name = name, Year = 2016, Overview = overview });
        return new RenderModel { Kind = RenderKind.Media, EventKey = Key, At = _h.Time.GetUtcNow(), ItemIds = [item.Id.ToString("N")] };
    }

    private TemplateFlag? StoredFlag()
        => _h.State.Read(s => s.Status.TemplateFlags.TryGetValue(Key, out var f) ? f : null);

    private void Set(string text, bool enabled = true)
        => Assert.Equal(
            SettingsUpdateStatus.Saved,
            _h.Settings.Update(null, c => c.Templates = [new TemplateOverride { EventKey = Key, Enabled = enabled, Text = text }]).Status);

    // A clock that moves on each time it is read, so a fit that renders many times runs out of time deterministically.
    private sealed class SteppingTime(TimeSpan step) : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Add(ref _ticks, step.Ticks);
    }
}
