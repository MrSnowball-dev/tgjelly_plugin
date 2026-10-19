using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Library;
using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Telegram.Bot.Extensions;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Rendering;

public sealed class MediaLayoutsTests : IDisposable
{
    private static readonly DateTime Modified = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly BotTestHarness _h = new();
    private readonly FakeLibrary _library = new();
    private readonly FakeServerInfo _server = new();
    private readonly MediaLayouts _layouts;
    private int _files;

    public MediaLayoutsTests()
    {
        SetPublicUrl("https://jf.example.com");
        _layouts = new MediaLayouts(new Formatter(_h.Strings), new LinkBuilder(_server), _library, _h.Settings);
    }

    [Fact]
    public void Movie_MatchesGolden_CaptionAndText()
    {
        var movie = Add(Movie());

        var message = _layouts.Render(Group("Movies", movie))!;

        Assert.Equal(Golden("movie.caption.txt"), message.Caption);
        Assert.Equal(Golden("movie.text.txt"), message.Text);
        Assert.Equal(movie.Primary!.Path, message.PosterPath);
        Assert.Equal($"{movie.Id:N}:{Modified.Ticks}", message.PosterCacheKey);
    }

    [Fact]
    public void Movie_Buttons()
    {
        var movie = Add(Movie());

        var buttons = _layouts.Render(Group("Movies", movie))!.Buttons;

        Assert.Equal(2, buttons.Count);
        Assert.Equal([("Open in Jellyfin", $"https://jf.example.com/web/#/details?id={movie.Id:N}&serverId=a1b2c3")], Row(buttons[0]));
        Assert.Equal(
            [("IMDb", "https://www.imdb.com/title/tt2543164/"), ("TMDb", "https://www.themoviedb.org/movie/329865")],
            Row(buttons[1]));
    }

    [Theory]
    [InlineData(nameof(MessageOptions.ShowPoster))]
    [InlineData(nameof(MessageOptions.ShowOverview))]
    [InlineData(nameof(MessageOptions.ShowRatingGenres))]
    [InlineData(nameof(MessageOptions.ShowOpenInJellyfin))]
    [InlineData(nameof(MessageOptions.ShowProviderLinks))]
    public void Toggles_Off_RemoveTheirParts(string toggle)
    {
        Assert.True(_h.Settings.Update(null, c => typeof(MessageOptions).GetProperty(toggle)!.SetValue(c.Message, false)).Status == SettingsUpdateStatus.Saved);
        var movie = Add(Movie());

        var message = _layouts.Render(Group("Movies", movie))!;

        var labels = message.Buttons.Select(r => r[0].Text).ToList();
        switch (toggle)
        {
            case nameof(MessageOptions.ShowPoster):
                Assert.Null(message.PosterPath);
                Assert.Null(message.PosterCacheKey);
                break;
            case nameof(MessageOptions.ShowOverview):
                Assert.DoesNotContain("linguist", message.Caption, StringComparison.Ordinal);
                Assert.DoesNotContain("linguist", message.Text, StringComparison.Ordinal);
                break;
            case nameof(MessageOptions.ShowRatingGenres):
                Assert.Equal("<b>Arrival</b> (2016)\n1h 56m", message.Caption.Split("\n\n")[0]);
                break;
            case nameof(MessageOptions.ShowOpenInJellyfin):
                Assert.Equal(["IMDb"], labels);
                break;
            default:
                Assert.Equal(["Open in Jellyfin"], labels);
                break;
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://localhost:8096")]
    [InlineData("jf.example.com")]
    public void InvalidPublicUrl_NoOpenButton(string url)
    {
        SetPublicUrl(url);
        var movie = Add(Movie());

        var buttons = _layouts.Render(Group("Movies", movie))!.Buttons;

        Assert.Equal(["IMDb"], buttons.Select(r => r[0].Text));
    }

    [Fact]
    public void Episodes_MatchesGolden_PosterFallsBackSeasonSeriesEpisode()
    {
        var series = Add(new MediaItem
        {
            Id = Guid.NewGuid(), Kind = MediaKind.Series, Name = "Severance", Primary = Poster(),
            ProviderIds = new Dictionary<string, string> { ["Imdb"] = "tt11280740", ["Tmdb"] = "95396" },
        });
        var season = Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Other, Name = "Season 2", Primary = Poster() });
        var e3 = Add(Episode(series, season, 3, "Who Is Alive?"));
        var e1 = Add(Episode(series, season, 1, "Hello, Ms. Cobel"));
        var e2 = Add(Episode(series, season, 2, "Goodbye, Mrs. Selvig"));
        var group = Group("Shows", e3, e1, e2);
        group.NewSeries = true;

        var message = _layouts.Render(group)!;

        Assert.Equal(Golden("episodes.caption.txt"), message.Caption);
        Assert.Equal(Golden("episodes.caption.txt"), message.Text);
        Assert.Equal(season.Primary!.Path, message.PosterPath);
        Assert.Equal($"https://jf.example.com/web/#/details?id={series.Id:N}&serverId=a1b2c3", message.Buttons[0][0].Url);
        Assert.Equal(
            [("IMDb", "https://www.imdb.com/title/tt11280740/"), ("TMDb", "https://www.themoviedb.org/tv/95396")],
            Row(message.Buttons[1]));

        _library.Items[season.Id] = Copy(season, new ImageRef(season.Primary.Path, IsLocal: false, Modified));
        Assert.Equal(series.Primary!.Path, _layouts.Render(group)!.PosterPath);

        _library.Items[series.Id] = Copy(series, null);
        Assert.Equal(e1.Primary!.Path, _layouts.Render(group)!.PosterPath);
    }

    [Fact]
    public void Episodes_SeasonZero_IsSpecials()
    {
        var series = Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Series, Name = "Severance" });
        var season = Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Other, Name = "Specials" });
        var special = Add(Episode(series, season, 1, "Behind the scenes", seasonNumber: 0));
        var unnumbered = Add(Episode(series, season, null, "Bonus"));

        var caption = _layouts.Render(Group("Shows", unnumbered, special))!.Caption;

        Assert.StartsWith("<b>Severance</b> · Specials · 2 new episodes\n\nE01 Behind the scenes\nBonus\n\n", caption, StringComparison.Ordinal);
    }

    [Fact]
    public void Album_TrackCount_FromChildCount()
    {
        var album = Add(new MediaItem
        {
            Id = Guid.NewGuid(), Kind = MediaKind.MusicAlbum, Name = "OK Computer", AlbumArtist = "Radiohead", Year = 1997,
            ChildCount = 12, Primary = Poster(),
        });

        var message = _layouts.Render(Group("Music", album))!;

        Assert.Equal(Golden("album.caption.txt"), message.Caption);
        Assert.Equal(album.Primary!.Path, message.PosterPath);
        Assert.Equal([$"https://jf.example.com/web/#/details?id={album.Id:N}&serverId=a1b2c3"], message.Buttons.Select(r => r[0].Url));
    }

    [Fact]
    public void Audiobook_CountsParts_AndUsesFolderName()
    {
        var folder = Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Other, Name = "The Hobbit (folder)", Primary = Poster() });
        var parts = Enumerable.Range(1, 3)
            .Select(i => Add(new MediaItem
            {
                Id = Guid.NewGuid(), Kind = MediaKind.AudioBook, Name = $"Part {i}", ParentId = folder.Id, ParentName = "The Hobbit",
                Primary = i == 1 ? new ImageRef("https://remote/cover.jpg", IsLocal: false, Modified) : Poster(),
            }))
            .ToArray();
        var group = Group("Audiobooks", parts);
        group.ContainerId = folder.Id.ToString("N");

        var message = _layouts.Render(group)!;

        Assert.Equal(Golden("audiobook.caption.txt"), message.Caption);
        Assert.Equal(folder.Primary!.Path, message.PosterPath);
        Assert.Equal([$"https://jf.example.com/web/#/details?id={folder.Id:N}&serverId=a1b2c3"], message.Buttons.Select(r => r[0].Url));
    }

    [Fact]
    public void Upgraded_AddsTheLine()
    {
        var group = Group("Movies", Add(Movie()));
        group.Upgraded = true;

        var message = _layouts.Render(group)!;

        Assert.EndsWith("\n\n♻️ Upgraded\n📁 Movies", message.Caption, StringComparison.Ordinal);
        Assert.EndsWith("\n\n♻️ Upgraded\n📁 Movies", message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AllItemsGone_ReturnsNull()
    {
        var movie = Movie();

        Assert.Null(_layouts.Render(Group("Movies", movie)));
    }

    [Fact]
    public void SomeItemsGone_RendersTheRest()
    {
        var series = Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Series, Name = "Severance" });
        var season = Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Other });
        var e1 = Add(Episode(series, season, 1, "Hello, Ms. Cobel"));
        var e2 = Episode(series, season, 2, "Goodbye, Mrs. Selvig");
        var e3 = Add(Episode(series, season, 3, "Who Is Alive?"));

        var caption = _layouts.Render(Group("Shows", e1, e2, e3))!.Caption;

        Assert.StartsWith("<b>Severance</b> · Season 2 · 2 new episodes\n\nE01 Hello, Ms. Cobel\nE03 Who Is Alive?\n\n", caption, StringComparison.Ordinal);
    }

    [Fact]
    public void HostileTitle_IsEscaped_AndFits()
    {
        var movie = Add(Copy(Movie(), Poster(), name: "<b>&</b>" + string.Concat(Enumerable.Repeat("😀", 3000))));

        var message = _layouts.Render(Group("Movies", movie))!;

        Assert.Contains("&lt;b&gt;&amp;&lt;/b&gt;", message.Caption, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;&amp;&lt;/b&gt;", message.Text, StringComparison.Ordinal);
        Assert.InRange(HtmlText.PlainLength(message.Caption), 1, 984);
        Assert.InRange(HtmlText.PlainLength(message.Text), 1, 4056);
    }

    [Fact]
    public void NonBmpCaption_AtExactly1024()
    {
        var exact = string.Concat(Enumerable.Repeat("😀", 984));
        var fits = Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Movie, Name = exact });
        var over = Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Movie, Name = exact + "😀" });

        var fitted = _layouts.Render(Group(string.Empty, fits))!.Caption;
        var truncated = _layouts.Render(Group(string.Empty, over))!.Caption;

        Assert.Equal("<b>" + exact + "</b>", fitted);
        Assert.InRange(HtmlText.PlainLength(truncated), 1, 984);
        Assert.Contains("…", truncated, StringComparison.Ordinal);
    }

    [Fact]
    public void Digest_MatchesGolden()
    {
        var series = Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Series, Name = "Severance" });
        var season = Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Other });
        var bear = Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Series, Name = "The Bear" });
        var bearSeason = Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Other });
        var severance = Group("Shows", Enumerable.Range(1, 8).Select(i => Add(Episode(series, season, i, $"Episode {i}"))).ToArray());
        var single = Group("Shows", Add(Episode(bear, bearSeason, 5, "Ice Chips", seasonNumber: 3)));
        (string Title, int? Year)[] movies =
        [
            ("Arrival", 2016), ("Dune", 2021), ("Heat", 1995), ("Alien", 1979), ("Up", 2009), ("Her", 2013), ("Drive", 2011),
            ("Prisoners", 2013), ("Coco", 2017), ("Primer", null),
        ];
        var groups = movies.Select(m => Group("Movies", Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Movie, Name = m.Title, Year = m.Year }))).ToList();
        groups.Insert(1, severance);
        groups.Add(single);

        var message = _layouts.RenderDigest(Digest(groups));

        Assert.Equal(Golden("digest.text.txt"), message.Text);
        Assert.Null(message.PosterPath);
        Assert.Empty(message.Buttons);
    }

    [Fact]
    public void Digest_SkipsGoneGroups_AndUsesOtherWithoutALibrary()
    {
        var kept = Group(string.Empty, Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Movie, Name = "A & B" }));
        var gone = Group("Movies", new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Movie, Name = "Gone" });

        var text = _layouts.RenderDigest(Digest([gone, kept])).Text;

        Assert.Equal("🆕 <b>1 new item in Jellyfin</b>\n\n<b>Other</b>: A &amp; B", text);
    }

    [Fact]
    public void Digest_Overflow_EndsWithAndNMore()
    {
        var groups = Enumerable.Range(1, 600)
            .Select(i => Group("Movies", Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Movie, Name = $"Movie number {i:000}", Year = 2000 })))
            .ToList();

        var text = _layouts.RenderDigest(Digest(groups)).Text;

        Assert.InRange(HtmlText.PlainLength(text), 1, 4056);
        var lines = text.Split('\n');
        Assert.StartsWith("🆕 <b>600 new items in Jellyfin</b>", text, StringComparison.Ordinal);
        var shown = lines[^2].Split(", ").Length;
        Assert.Equal($"…and {600 - shown} more", lines[^1]);
        Assert.EndsWith($"Movie number {shown:000} (2000)", lines[^2], StringComparison.Ordinal);
        Assert.True(HtmlText.PlainLength(text) + ", Movie number 000 (2000)".Length > 4056, "one more entry would have fit");
    }

    public void Dispose()
    {
        _h.Dispose();
    }

    private static string Golden(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Rendering", "Golden", name)).TrimEnd('\n');

    private static List<(string, string)> Row(List<OutboxButton> row) => row.Select(b => (b.Text, b.Url)).ToList();

    private static RenderModel Group(string library, params MediaItem[] items) => new()
    {
        Kind = RenderKind.Media,
        EventKey = "media.movie",
        LibraryName = library,
        ItemIds = items.Select(i => i.Id.ToString("N")).ToList(),
    };

    private static RenderModel Digest(List<RenderModel> groups) => new() { Kind = RenderKind.Digest, EventKey = "media.digest", Groups = groups };

    private MediaItem Episode(MediaItem series, MediaItem season, int? number, string title, int seasonNumber = 2) => new()
    {
        Id = Guid.NewGuid(), Kind = MediaKind.Episode, Name = title, SeriesId = series.Id, SeriesName = series.Name, SeasonId = season.Id,
        SeasonNumber = seasonNumber, EpisodeNumber = number, Primary = Poster(),
    };

    private static MediaItem Copy(MediaItem item, ImageRef? primary, string? name = null) => new()
    {
        Id = item.Id, Kind = item.Kind, Name = name ?? item.Name, Year = item.Year, Overview = item.Overview,
        CommunityRating = item.CommunityRating, RunTime = item.RunTime, Genres = item.Genres, ProviderIds = item.ProviderIds,
        Primary = primary,
    };

    private MediaItem Movie() => new()
    {
        Id = Guid.NewGuid(),
        Kind = MediaKind.Movie,
        Name = "Arrival",
        Year = 2016,
        Overview = "A linguist works with the military to talk to alien visitors.",
        CommunityRating = 7.9f,
        RunTime = TimeSpan.FromMinutes(116),
        Genres = ["Drama", "Sci-Fi"],
        ProviderIds = new Dictionary<string, string> { ["Imdb"] = "tt2543164", ["Tmdb"] = "329865" },
        Primary = Poster(),
    };

    private MediaItem Add(MediaItem item) => _library.Add(item);

    private ImageRef Poster()
    {
        var path = Path.Combine(_h.Dir.Path, $"poster{++_files}.jpg");
        File.WriteAllBytes(path, [1, 2, 3]);
        return new ImageRef(path, true, Modified);
    }

    private void SetPublicUrl(string url)
        => Assert.Equal(SettingsUpdateStatus.Saved, _h.Settings.Update(null, c => c.PublicUrl = url).Status);
}
