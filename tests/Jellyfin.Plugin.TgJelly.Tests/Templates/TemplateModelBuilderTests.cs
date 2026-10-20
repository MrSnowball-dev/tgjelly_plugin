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
        var model = _builder.Build(Alert("security.user_created", ("user", "ann")));

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

        var model = _builder.Build(group);

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
        Assert.Null(_builder.Build(group)["jellyfin_url"]);
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

        var model = _builder.Build(group);

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

        var model = _builder.Build(Media("media.album", album));

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

        var model = _builder.Build(group);

        Assert.Equal("The Hobbit", model["title"]);
        Assert.Equal(3L, model["parts"]);
        Assert.Contains($"id={folder.Id:N}", (string)model["jellyfin_url"]!, StringComparison.Ordinal);
    }

    [Fact]
    public void Book_HasNoParts()
    {
        var book = _library.Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Book, Name = "Dune", Year = 1965 });

        var model = _builder.Build(Media("media.book", book));

        Assert.Equal("Dune", model["title"]);
        Assert.Equal(0L, model["parts"]);
    }

    [Fact]
    public void LoginFailed_FromValues()
    {
        var model = _builder.Build(Alert(
            "security.login_failed", ("user", "ann"), ("attempts", "5"), ("ip_count", "2"), ("ip", "10.0.0.7"), ("client", "Web"), ("device", "Firefox"), ("locked", "true")));

        Assert.Equal("10.0.0.7", model["last_ip"]);
        Assert.Equal(2L, model["ip_count"]);
        Assert.Equal(5L, model["attempts"]);
        Assert.Equal(true, model["locked"]);
        Assert.Equal(At.ToUnixTimeSeconds(), model["last_attempt_unix"]);
        Assert.Equal("09:30 UTC", model["last_attempt_text"]);
        Assert.Equal(["event", "at_unix", "at_text", "user", "attempts", "ip_count", "last_ip", "client", "device", "last_attempt_unix", "last_attempt_text", "locked"], model.Keys);
    }

    [Fact]
    public void BadNumber_BecomesNull()
    {
        var model = _builder.Build(Alert("security.lockout", ("user", "ann"), ("attempts", "many")));

        Assert.Null(model["attempts"]);
    }

    [Fact]
    public void RestartPending_ReasonsAreLinesAsIs()
    {
        var alert = Alert("server.restart_pending");
        alert.Lines = ["Plugin A&B <beta> installed", "A &amp; B"];

        var model = _builder.Build(alert);

        Assert.Equal(new object?[] { "Plugin A&B <beta> installed", "A &amp; B" }, Assert.IsAssignableFrom<IReadOnlyList<object?>>(model["reasons"]));
    }

    [Fact]
    public void MissingItems_GiveNulls_NotThrow()
    {
        foreach (var key in EventCatalog.AllKeys.Where(TemplateFieldCatalog.IsMedia))
        {
            var model = _builder.Build(new RenderModel { Kind = RenderKind.Media, EventKey = key, At = At, ItemIds = [Guid.NewGuid().ToString("N"), "bad"] });

            Assert.Equal(TemplateFieldCatalog.For(key).Select(f => f.Name), model.Keys);
        }

        var movie = _builder.Build(new RenderModel { Kind = RenderKind.Media, EventKey = "media.movie", At = At });
        Assert.Null(movie["title"]);
        Assert.Null(movie["jellyfin_url"]);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<object?>>(movie["genres"]));
        var episodes = _builder.Build(new RenderModel { Kind = RenderKind.Media, EventKey = "media.episodes", At = At });
        Assert.Equal(0L, episodes["count"]);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<object?>>(episodes["episodes"]));
    }

    [Fact]
    public void EveryKey_ModelHasExactlyTheCatalogFields()
    {
        foreach (var key in EventCatalog.AllKeys.Where(k => !TemplateFieldCatalog.IsMedia(k)))
        {
            var model = _builder.Build(Alert(key));

            Assert.Equal(TemplateFieldCatalog.For(key).Select(f => f.Name), model.Keys);
        }
    }

    public void Dispose() => _h.Dispose();

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
