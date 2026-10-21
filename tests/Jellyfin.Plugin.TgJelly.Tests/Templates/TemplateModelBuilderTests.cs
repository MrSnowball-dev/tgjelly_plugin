using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Library;
using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Templates;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Templates;

public sealed class TemplateModelBuilderTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 18, 9, 30, 0, TimeSpan.Zero);
    private readonly BotTestHarness _h = new();
    private readonly FakeLibrary _library = new();
    private readonly TemplateModelBuilder _builder;

    public TemplateModelBuilderTests()
    {
        Assert.Equal(SettingsUpdateStatus.Saved, _h.Settings.Update(null, c => c.PublicUrl = "https://jf.example.com").Status);
        _builder = new TemplateModelBuilder(_library, new LinkBuilder(new FakeServerInfo()), _h.Settings, new Formatter(_h.Strings), _h.Time);
    }

    [Fact]
    public void CommonFields_AreSet()
    {
        var model = V(Alert("security.user_created", ("user", "ann")));

        Assert.Equal("security.user_created", model["event"]);
        Assert.Equal(At.ToUnixTimeSeconds(), model["at_unix"]);
        Assert.Equal("09:30 UTC", model["at_text"]);
    }

    [Fact]
    public void Movie_FromMediaItem()
    {
        var movie = _library.Add(new MediaItem
        {
            Id = Guid.NewGuid(), Kind = MediaKind.Movie, Name = "Arrival", Year = 2016, Overview = "Aliens.", CommunityRating = 7.9f,
            RunTime = new TimeSpan(2, 14, 0), Genres = ["Drama", "Sci-Fi"],
            ProviderIds = new Dictionary<string, string> { ["imdb"] = "tt2543164", ["Tmdb"] = "329865" },
        });
        var group = Media("media.movie", movie);
        group.Upgraded = true;
        group.LibraryName = "Movies";

        var model = V(group);

        Assert.Equal("Arrival", model["title"]);
        Assert.Equal(2016L, model["year"]);
        Assert.Equal("Aliens.", model["overview"]);
        Assert.Equal(7.9, model["rating"]);
        Assert.Equal(134L, model["runtime_minutes"]);
        Assert.Equal(new object?[] { "Drama", "Sci-Fi" }, Assert.IsAssignableFrom<IReadOnlyList<object?>>(model["genres"]));
        Assert.Equal("Movies", model["library"]);
        Assert.Equal("tt2543164", model["imdb_id"]);
        Assert.Equal("329865", model["tmdb_id"]);
        Assert.Equal(true, model["upgraded"]);
        Assert.Equal($"https://jf.example.com/web/#/details?id={movie.Id:N}&serverId=a1b2c3", model["jellyfin_url"]);

        Assert.Equal(SettingsUpdateStatus.Saved, _h.Settings.Update(null, c => c.PublicUrl = string.Empty).Status);
        Assert.Null(V(group)["jellyfin_url"]);
    }

    [Fact]
    public void Episodes_SortedByNumber_WithTitles()
    {
        var series = _library.Add(new MediaItem
        {
            Id = Guid.NewGuid(), Kind = MediaKind.Series, Name = "Severance",
            ProviderIds = new Dictionary<string, string> { ["Imdb"] = "tt11280740", ["Tmdb"] = "95396" },
        });
        MediaItem Ep(int n, string t) => _library.Add(new MediaItem
        {
            Id = Guid.NewGuid(), Kind = MediaKind.Episode, Name = t, SeriesId = series.Id, SeriesName = series.Name,
            SeasonNumber = 0, EpisodeNumber = n,
        });
        var group = Media("media.episodes", Ep(3, "Three"), Ep(1, "One"), Ep(2, "Two"));
        group.NewSeries = true;

        var model = V(group);

        Assert.Equal("Severance", model["series"]);
        Assert.Equal(0L, model["season"]);
        Assert.Equal(3L, model["count"]);
        Assert.Equal(true, model["new_series"]);
        Assert.Equal("tt11280740", model["imdb_id"]);
        Assert.Equal("95396", model["tmdb_id"]);
        var episodes = Assert.IsAssignableFrom<IReadOnlyList<object?>>(model["episodes"]).Cast<IReadOnlyDictionary<string, object?>>().ToList();
        Assert.Equal([1L, 2L, 3L], episodes.Select(e => e["number"]));
        Assert.Equal(["One", "Two", "Three"], episodes.Select(e => e["title"]));
        Assert.Contains($"id={series.Id:N}", (string)model["jellyfin_url"]!, StringComparison.Ordinal);
    }

    [Fact]
    public void Album_FromMediaItem()
    {
        var album = _library.Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.MusicAlbum, Name = "Discovery", AlbumArtist = "Daft Punk", Year = 2001, ChildCount = 14 });

        var model = V(Media("media.album", album));

        Assert.Equal("Daft Punk", model["artist"]);
        Assert.Equal("Discovery", model["album"]);
        Assert.Equal(2001L, model["year"]);
        Assert.Equal(14L, model["tracks"]);
    }

    [Fact]
    public void Audiobook_PartsCount()
    {
        var folder = _library.Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Other, Name = "The Hobbit (folder)" });
        MediaItem Part(string n) => _library.Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.AudioBook, Name = n, ParentId = folder.Id, ParentName = "The Hobbit" });
        var group = Media("media.book", Part("1"), Part("2"), Part("3"));
        group.ContainerId = folder.Id.ToString("N");

        var model = V(group);

        Assert.Equal("The Hobbit", model["title"]);
        Assert.Equal(3L, model["parts"]);
        Assert.Contains($"id={folder.Id:N}", (string)model["jellyfin_url"]!, StringComparison.Ordinal);
    }

    [Fact]
    public void Book_HasNoParts()
    {
        var book = _library.Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Book, Name = "Dune", Year = 1965 });

        var model = V(Media("media.book", book));

        Assert.Equal("Dune", model["title"]);
        Assert.Equal(0L, model["parts"]);
    }

    [Fact]
    public void LoginFailed_FromValues()
    {
        var model = V(Alert(
            "security.login_failed", ("user", "ann"), ("attempts", "5"), ("ip_count", "2"), ("ip", "10.0.0.7"), ("client", "Web"), ("device", "Firefox"), ("locked", "true")));

        Assert.Equal("10.0.0.7", model["last_ip"]);
        Assert.Equal(2L, model["ip_count"]);
        Assert.Equal(5L, model["attempts"]);
        Assert.Equal(true, model["locked"]);
        Assert.Equal(At.ToUnixTimeSeconds(), model["last_attempt_unix"]);
        Assert.Equal("09:30 UTC", model["last_attempt_text"]);
        Assert.Equal(["event", "at_unix", "at_text", "at_html", "user", "attempts", "attempts_text", "ip_count", "ip_count_text", "last_ip", "ip", "client", "device", "last_attempt_unix", "last_attempt_text", "locked"], model.Keys);
    }

    [Fact]
    public void BadNumber_BecomesNull()
    {
        var model = V(Alert("security.lockout", ("user", "ann"), ("attempts", "many")));

        Assert.Null(model["attempts"]);
    }

    [Fact]
    public void RestartPending_ReasonsAreLinesAsIs()
    {
        var alert = Alert("server.restart_pending");
        alert.Lines = ["Plugin A&B <beta> installed", "A &amp; B"];

        var model = V(alert);

        Assert.Equal(new object?[] { "Plugin A&B <beta> installed", "A &amp; B" }, Assert.IsAssignableFrom<IReadOnlyList<object?>>(model["reasons"]));
    }

    [Fact]
    public void MissingItems_GiveNulls_NotThrow()
    {
        foreach (var key in EventCatalog.AllKeys.Where(TemplateFieldCatalog.IsMedia))
        {
            var model = V(new RenderModel { Kind = RenderKind.Media, EventKey = key, At = At, ItemIds = [Guid.NewGuid().ToString("N"), "bad"] });

            Assert.Equal(TemplateFieldCatalog.For(key).Select(f => f.Name), model.Keys);
        }

        var movie = V(new RenderModel { Kind = RenderKind.Media, EventKey = "media.movie", At = At });
        Assert.Null(movie["title"]);
        Assert.Null(movie["jellyfin_url"]);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<object?>>(movie["genres"]));
        var episodes = V(new RenderModel { Kind = RenderKind.Media, EventKey = "media.episodes", At = At });
        Assert.Equal(0L, episodes["count"]);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<object?>>(episodes["episodes"]));
    }

    [Fact]
    public void EveryKey_ModelHasExactlyTheCatalogFields()
    {
        foreach (var key in EventCatalog.AllKeys.Where(k => !TemplateFieldCatalog.IsMedia(k)))
        {
            var model = V(Alert(key));

            Assert.Equal(TemplateFieldCatalog.For(key).Select(f => f.Name), model.Keys);
        }
    }

    [Fact]
    public void Build_V02FieldNamesStillPresent()
    {
        var movie = new[] { "title", "year", "overview", "rating", "runtime_minutes", "genres", "library", "imdb_id", "tmdb_id", "jellyfin_url", "upgraded" };
        var v02 = new Dictionary<string, string[]>
        {
            ["media.movie"] = movie,
            ["media.musicvideo"] = movie,
            ["media.episodes"] = ["series", "season", "count", "episodes", "new_series", "library", "imdb_id", "tmdb_id", "jellyfin_url", "upgraded"],
            ["media.album"] = ["artist", "album", "year", "tracks", "library", "jellyfin_url", "upgraded"],
            ["media.book"] = ["title", "year", "overview", "parts", "library", "jellyfin_url", "upgraded"],
            ["security.login_failed"] = ["user", "attempts", "ip_count", "last_ip", "client", "device", "last_attempt_unix", "last_attempt_text", "locked"],
            ["security.lockout"] = ["user", "attempts"],
            ["security.login_succeeded"] = ["user", "client", "device", "ip"],
            ["security.user_created"] = ["user"],
            ["security.user_deleted"] = ["user"],
            ["security.password_changed"] = ["user"],
            ["server.started"] = ["version", "server"],
            ["server.update_available"] = ["version", "current", "url"],
            ["server.plugin_installed"] = ["plugin", "version"],
            ["server.plugin_uninstalled"] = ["plugin", "version"],
            ["server.plugin_updated"] = ["plugin", "version", "changelog"],
            ["server.plugin_install_failed"] = ["plugin", "version", "error"],
            ["server.task_failed"] = ["task", "error"],
            ["server.restart_pending"] = ["reasons"],
        };
        foreach (var key in EventCatalog.AllKeys)
        {
            var model = V(TemplateFieldCatalog.IsMedia(key) ? Media(key) : Alert(key));
            foreach (var name in new[] { "event", "at_unix", "at_text" }.Concat(v02[key]))
            {
                Assert.True(model.ContainsKey(name), key + "." + name);
            }
        }
    }

    [Fact]
    public void Build_ShowRatingGenresOff_EmptiesRatingAndGenres()
    {
        var movie = _library.Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Movie, Name = "Arrival", CommunityRating = 7.9f, Genres = ["Drama"] });
        Assert.Equal("7.9", V(Media("media.movie", movie))["rating_text"]);
        Assert.Equal(SettingsUpdateStatus.Saved, _h.Settings.Update(null, c => c.Message.ShowRatingGenres = false).Status);

        var model = V(Media("media.movie", movie));

        Assert.Null(model["rating"]);
        Assert.Null(model["rating_text"]);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<object?>>(model["genres"]));
    }

    [Fact]
    public void Build_ShowOverviewOff_NullOverview()
    {
        var movie = _library.Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Movie, Name = "Arrival", Overview = "Aliens." });
        Assert.Equal("Aliens.", V(Media("media.movie", movie))["overview"]);
        Assert.Equal(SettingsUpdateStatus.Saved, _h.Settings.Update(null, c => c.Message.ShowOverview = false).Status);

        Assert.Null(V(Media("media.movie", movie))["overview"]);
    }

    [Fact]
    public void Build_AtHtml_IsRawTgTime()
    {
        var model = V(Alert("security.user_created"));

        var raw = Assert.IsType<RawHtml>(model["at_html"]);
        Assert.Equal(new Formatter(_h.Strings).Time(At, _h.Time.GetUtcNow()), raw.Html);
        Assert.StartsWith("<tg-time unix=\"" + At.ToUnixTimeSeconds(), raw.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_EpisodeNumberText_TwoDigitsOrNull()
    {
        MediaItem Ep(int? n, string t) => _library.Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Episode, Name = t, SeriesName = "S", EpisodeNumber = n });

        var model = V(Media("media.episodes", Ep(1, "A"), Ep(12, "B"), Ep(null, "C")));

        var episodes = Assert.IsAssignableFrom<IReadOnlyList<object?>>(model["episodes"]).Cast<IReadOnlyDictionary<string, object?>>().ToList();
        Assert.Equal(["01", "12", null], episodes.Select(e => e["number_text"]));
        Assert.Equal("3", model["count_text"]);
        Assert.Equal(0L, model["episodes_more"]);
    }

    [Fact]
    public void Build_PluginUpdated_WhitespaceChangelog_IsNull()
    {
        Assert.Null(V(Alert("server.plugin_updated", ("plugin", "P"), ("changelog", "  \n "))) ["changelog"]);
        Assert.Equal("Fixed.", V(Alert("server.plugin_updated", ("plugin", "P"), ("changelog", "  Fixed.\n")))["changelog"]);
        Assert.Null(V(Alert("server.task_failed", ("task", "T"), ("error", " ")))["error"]);
    }

    [Fact]
    public void Build_Book_HasRatingGenresNoRuntime()
    {
        var book = _library.Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Book, Name = "Dune", CommunityRating = 8.04f, Genres = ["SF", "Classic"] });

        var model = V(Media("media.book", book));

        Assert.Equal(8.0, model["rating"]);
        Assert.Equal("8.0", model["rating_text"]);
        Assert.Equal(new object?[] { "SF", "Classic" }, Assert.IsAssignableFrom<IReadOnlyList<object?>>(model["genres"]));
        Assert.Equal("0", model["parts_text"]);
        Assert.False(model.ContainsKey("runtime_minutes"));
        Assert.False(model.ContainsKey("runtime_text"));
    }

    [Fact]
    public void Build_NumberTexts_UseGrouping()
    {
        var album = _library.Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.MusicAlbum, Name = "X", ChildCount = 1234 });
        Assert.Equal("1,234", V(Media("media.album", album))["tracks_text"]);
        var failed = V(Alert("security.login_failed", ("attempts", "1500"), ("ip_count", "2")));
        Assert.Equal("1,500", failed["attempts_text"]);
        Assert.Equal("2", failed["ip_count_text"]);
        Assert.Null(V(Alert("security.lockout", ("attempts", "x")))["attempts_text"]);
    }

    [Fact]
    public void Build_RuntimeText()
    {
        var movie = _library.Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Movie, Name = "A", RunTime = new TimeSpan(1, 56, 0) });

        Assert.Equal("1h 56m", V(Media("media.movie", movie))["runtime_text"]);
    }

    [Fact]
    public void Sample_AppliesToggles()
    {
        Assert.Equal("7.9", _builder.Sample("media.movie").Values["rating_text"]);
        Assert.Equal(SettingsUpdateStatus.Saved, _h.Settings.Update(null, c =>
        {
            c.Message.ShowRatingGenres = false;
            c.Message.ShowOverview = false;
        }).Status);

        var sample = _builder.Sample("media.movie").Values;

        Assert.Null(sample["rating"]);
        Assert.Null(sample["rating_text"]);
        Assert.Null(sample["overview"]);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<object?>>(sample["genres"]));
        Assert.Equal("Arrival", sample["title"]);
        Assert.NotNull(_builder.Sample("media.digest").Values["libraries"]);
    }

    [Fact]
    public void Shrink_Table()
    {
        Assert.Equal(("overview", null, null), TemplateFieldCatalog.Shrink("media.movie"));
        Assert.Equal(("overview", null, null), TemplateFieldCatalog.Shrink("media.musicvideo"));
        Assert.Equal(("overview", null, null), TemplateFieldCatalog.Shrink("media.book"));
        Assert.Equal((null, "episodes", "episodes_more"), TemplateFieldCatalog.Shrink("media.episodes"));
        Assert.Equal(("changelog", null, null), TemplateFieldCatalog.Shrink("server.plugin_updated"));
        Assert.Equal(("error", null, null), TemplateFieldCatalog.Shrink("server.plugin_install_failed"));
        Assert.Equal(("error", null, null), TemplateFieldCatalog.Shrink("server.task_failed"));
        Assert.Equal((null, "reasons", "reasons_more"), TemplateFieldCatalog.Shrink("server.restart_pending"));
        Assert.Equal((null, "libraries", "more"), TemplateFieldCatalog.Shrink("media.digest"));
        Assert.Equal((null, null, null), TemplateFieldCatalog.Shrink("media.album"));
        Assert.Equal((null, null, null), TemplateFieldCatalog.Shrink("security.lockout"));
    }

    [Fact]
    public void Build_ReturnsShrinkableModel()
    {
        var model = _builder.Build(Alert("server.restart_pending", ("x", "y")));

        Assert.Equal(0, model.ListCount);
    }

    public void Dispose() => _h.Dispose();

    private IReadOnlyDictionary<string, object?> V(RenderModel model) => _builder.Build(model).Values;

    private static RenderModel Alert(string key, params (string Name, string Value)[] values) => new()
    {
        Kind = RenderKind.Alert,
        EventKey = key,
        At = At,
        Values = values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal),
    };

    private static RenderModel Media(string key, params MediaItem[] items) => new()
    {
        Kind = RenderKind.Media,
        EventKey = key,
        At = At,
        ItemIds = items.Select(i => i.Id.ToString("N")).ToList(),
    };
}
