using System;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.Templates;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Templates;

public sealed class TemplateSampleTests : IDisposable
{
    private readonly BotTestHarness _h = new();
    private readonly TemplateSample _sample;

    public TemplateSampleTests()
    {
        var engine = new TemplateEngine(_h.Time);
        var models = new TemplateModelBuilder(new FakeLibrary(), new LinkBuilder(new FakeServerInfo()), _h.Settings, new Formatter(_h.Strings), _h.Time);
        var renderer = new TemplateRenderer(engine, models, new TemplateFlags(_h.State, _h.Settings, _h.Time), _h.Settings, _h.Strings, NullLogger<TemplateRenderer>.Instance);
        _sample = new TemplateSample(renderer, engine, models, _h.Settings, _h.Strings);
        _h.Settings.Update(null, c => c.PublicUrl = "https://jf.example.com");
    }

    public void Dispose() => _h.Dispose();

    [Fact]
    public void Buttons_FollowToggles()
    {
        var all = _sample.Build("media.movie", "{{ title }}");
        Assert.Equal(
            [("Open in Jellyfin", "https://jf.example.com/web/"), ("IMDb", "https://www.imdb.com/title/tt2543164/"), ("TMDb", "https://www.themoviedb.org/movie/329865")],
            all.Buttons.SelectMany(r => r).Select(b => (b.Text, b.Url)));

        _h.Settings.Update(null, c => c.Message.Change("media.movie", s => s.ShowProviderLinks = false));
        Assert.Equal(["Open in Jellyfin"], _sample.Build("media.movie", "x").Buttons.SelectMany(r => r).Select(b => b.Text));

        _h.Settings.Update(null, c => c.Message.Change("media.movie", s => s.ShowOpenInJellyfin = false));
        Assert.Empty(_sample.Build("media.movie", "x").Buttons);
    }

    [Fact]
    public void Episodes_UseTheTvLink_AndUpdatesGetReleaseNotes()
    {
        var episodes = _sample.Build("media.episodes", "x").Buttons.SelectMany(r => r).Single(b => b.Text == "TMDb");
        Assert.Contains("/tv/", episodes.Url, StringComparison.Ordinal);

        var update = _sample.Build("server.update_available", "x");
        Assert.Contains(update.Buttons.SelectMany(r => r), b => b.Text == "Release notes" && b.Url.StartsWith("https://github.com/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("security.login_failed")]
    [InlineData("server.started")]
    [InlineData("server.update_available")]
    [InlineData("media.digest")]
    public void AlertsAndDigest_NoOpenOrProviderButtons(string key)
    {
        // Real alerts and digests never carry these rows, so neither does their preview or test send.
        var buttons = _sample.Build(key, "x").Buttons.SelectMany(r => r).Select(b => b.Text).ToList();

        Assert.DoesNotContain("Open in Jellyfin", buttons);
        Assert.DoesNotContain("IMDb", buttons);
        Assert.DoesNotContain("TMDb", buttons);
    }

    [Fact]
    public void AlertWithoutReleaseNotes_HasNoButtons()
    {
        Assert.Empty(_sample.Build("security.login_failed", "x").Buttons);
        Assert.Empty(_sample.Build("server.started", "x").Buttons);
        Assert.Empty(_sample.Build("media.digest", "x").Buttons);
        Assert.Equal(["Release notes"], _sample.Build("server.update_available", "x").Buttons.SelectMany(r => r).Select(b => b.Text));
    }

    [Fact]
    public void OtherMedia_OpenButOnlyMovieAndEpisodesGetProviders()
    {
        Assert.Equal(["Open in Jellyfin"], _sample.Build("media.album", "x").Buttons.SelectMany(r => r).Select(b => b.Text));
        Assert.Equal(["Open in Jellyfin"], _sample.Build("media.book", "x").Buttons.SelectMany(r => r).Select(b => b.Text));
    }

    [Fact]
    public void NoPublicUrl_NoOpenButton()
    {
        _h.Settings.Update(null, c => c.PublicUrl = string.Empty);

        Assert.DoesNotContain(_sample.Build("media.movie", "x").Buttons.SelectMany(r => r), b => b.Text == "Open in Jellyfin");

        _h.Settings.Update(null, c => c.PublicUrl = "http://localhost");
        Assert.DoesNotContain(_sample.Build("media.movie", "x").Buttons.SelectMany(r => r), b => b.Text == "Open in Jellyfin");
    }

    [Theory]
    [InlineData("security.lockout")]
    [InlineData("server.started")]
    public void Validate_SecurityPosterOn_ReportsPoster(string key)
    {
        Assert.False(_sample.Build(key, "x").Poster);

        _h.Settings.Update(null, c => c.Message.Change(key, s => s.ShowPoster = true));

        var sample = _sample.Build(key, "x");
        Assert.True(sample.Poster && sample.Caption);
    }

    [Fact]
    public void PosterAndCaption_OnlyForMediaKeys_WithShowPoster()
    {
        var movie = _sample.Build("media.movie", "x");
        Assert.True(movie.Poster && movie.Caption);
        Assert.Equal(1024, movie.Limit);

        var digest = _sample.Build("media.digest", "x");
        Assert.False(digest.Poster || digest.Caption);
        Assert.Equal(4096, digest.Limit);
        Assert.False(_sample.Build("server.started", "x").Poster);

        _h.Settings.Update(null, c => c.Message.Change("media.movie", s => s.ShowPoster = false));
        Assert.False(_sample.Build("media.movie", "x").Poster);
    }

    [Fact]
    public void LongText_IsFittedButLengthIsUnfitted()
    {
        var sample = _sample.Build("media.movie", "{{ overview }} " + new string('a', 1200));

        Assert.NotNull(sample.Html);
        Assert.True(Telegram.Bot.Extensions.HtmlText.PlainLength(sample.Html!) <= 1024);
        Assert.True(sample.Length > 1200);
    }

    [Fact]
    public void BadTemplate_HasError()
    {
        var sample = _sample.Build("media.movie", "{{ nope }}");

        Assert.NotNull(sample.Error);
        Assert.Null(sample.Html);
    }
}
