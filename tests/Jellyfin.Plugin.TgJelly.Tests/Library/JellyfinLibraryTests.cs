using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.TgJelly.Library;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Library;

public class JellyfinLibraryTests
{
    private readonly ILibraryManager _manager = Substitute.For<ILibraryManager>();
    private readonly ListLogger<JellyfinLibrary> _logger = new();
    private readonly JellyfinLibrary _library;

    public JellyfinLibraryTests() => _library = new JellyfinLibrary(_manager, _logger);

    [Fact]
    public void GetItem_Movie_MapsStoredFields()
    {
        var date = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var movie = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "Arrival",
            ProductionYear = 2016,
            Overview = "A linguist.",
            CommunityRating = 7.9f,
            RunTimeTicks = TimeSpan.FromMinutes(116).Ticks,
            Genres = ["Drama", "Sci-Fi"],
            ProviderIds = new Dictionary<string, string> { ["Imdb"] = "tt2543164", ["Tmdb"] = "329865" },
            ImageInfos = [new ItemImageInfo { Type = ImageType.Primary, Path = "/p.jpg", DateModified = date }],
        };
        _manager.GetItemById(movie.Id).Returns(movie);

        var item = _library.GetItem(movie.Id)!;

        Assert.Equal((MediaKind.Movie, "Arrival", 2016, "A linguist.", 7.9f, TimeSpan.FromMinutes(116)), (item.Kind, item.Name, item.Year, item.Overview, item.CommunityRating, item.RunTime));
        Assert.Equal(["Drama", "Sci-Fi"], item.Genres);
        Assert.Equal("tt2543164", item.ProviderIds["Imdb"]);
        Assert.Equal(new ImageRef("/p.jpg", true, date), item.Primary);
        Assert.Equal(["imdb:tt2543164", "tmdb:329865"], item.Identities);
    }

    [Fact]
    public void GetItem_Episode_MapsSeriesAndSeason()
    {
        var episode = new Episode { Id = Guid.NewGuid(), Name = "Hello", SeriesId = Guid.NewGuid(), SeriesName = "Severance", SeasonId = Guid.NewGuid(), ParentIndexNumber = 2, IndexNumber = 1 };
        _manager.GetItemById(episode.Id).Returns(episode);

        var item = _library.GetItem(episode.Id)!;

        Assert.Equal((MediaKind.Episode, episode.SeriesId, "Severance", episode.SeasonId, 2, 1), (item.Kind, item.SeriesId, item.SeriesName, item.SeasonId, item.SeasonNumber, item.EpisodeNumber));
        Assert.Null(item.Primary);
    }

    [Fact]
    public void GetItem_Album_CountsTracks()
    {
        var album = new MusicAlbum { Id = Guid.NewGuid(), Name = "Album" };
        _manager.GetItemById(album.Id).Returns(album);
        InternalItemsQuery? query = null;
        _manager.GetCount(Arg.Do<InternalItemsQuery>(q => query = q)).Returns(12);

        var item = _library.GetItem(album.Id)!;

        Assert.Equal(12, item.ChildCount);
        Assert.Equal([album.Id], query!.AncestorIds);
        Assert.Equal([BaseItemKind.Audio], query.IncludeItemTypes);
        Assert.Equal((false, true), (query.IsVirtualItem, query.Recursive));
    }

    [Fact]
    public void GetItem_ParentName_ComesFromTheParent()
    {
        var folder = new Folder { Id = Guid.NewGuid(), Name = "The Hobbit" };
        var part = new AudioBook { Id = Guid.NewGuid(), Name = "Part 1", ParentId = folder.Id };
        _manager.GetItemById(part.Id).Returns(part);
        _manager.GetItemById(folder.Id).Returns(folder);

        var item = _library.GetItem(part.Id)!;

        Assert.Equal((MediaKind.AudioBook, folder.Id, "The Hobbit"), (item.Kind, item.ParentId, item.ParentName));
    }

    [Fact]
    public void GetItem_Missing_ReturnsNull() => Assert.Null(_library.GetItem(Guid.NewGuid()));

    [Fact]
    public void GetItem_Throws_ReturnsNull_AndLogs()
    {
        var id = Guid.NewGuid();
        _manager.GetItemById(id).Throws(new InvalidOperationException("db"));

        Assert.Null(_library.GetItem(id));
        Assert.Equal(1, _logger.Count(Microsoft.Extensions.Logging.LogLevel.Warning));
    }

    [Fact]
    public void HasOtherCopy_QueriesProviderIds_SameType_ExcludingSelf()
    {
        var item = Item(MediaKind.Movie, new Dictionary<string, string> { ["Imdb"] = "tt1", ["Tmdb"] = "2", ["Zap2It"] = "z" });
        var queries = new List<InternalItemsQuery>();
        _manager.GetCount(Arg.Do<InternalItemsQuery>(queries.Add)).Returns(1);

        Assert.True(_library.HasOtherCopy(item));

        var q = Assert.Single(queries);
        Assert.Equal([BaseItemKind.Movie], q.IncludeItemTypes);
        Assert.Equal(new Dictionary<string, string> { ["Imdb"] = "tt1", ["Tmdb"] = "2" }, q.HasAnyProviderId);
        Assert.Equal((false, true), (q.IsVirtualItem, q.Recursive));
        Assert.Equal([item.Id], q.ExcludeItemIds);
    }

    [Fact]
    public void HasOtherCopy_Episode_AlsoQueriesSeasonAndNumber()
    {
        var series = Guid.NewGuid();
        var item = Item(MediaKind.Episode, new Dictionary<string, string> { ["Tvdb"] = "9" }, series, 2, 5);
        var queries = new List<InternalItemsQuery>();
        _manager.GetCount(Arg.Do<InternalItemsQuery>(queries.Add)).Returns(0);

        Assert.False(_library.HasOtherCopy(item));

        Assert.Equal(2, queries.Count);
        var numbered = queries.Single(q => q.IndexNumber is not null);
        Assert.Equal([series], numbered.AncestorIds);
        Assert.Equal((2, 5), (numbered.ParentIndexNumber, numbered.IndexNumber));
        Assert.Equal([BaseItemKind.Episode], numbered.IncludeItemTypes);
        Assert.Equal([item.Id], numbered.ExcludeItemIds);
    }

    [Fact]
    public void HasOtherCopy_EmptyIdentities_NeverQueries()
    {
        Assert.False(_library.HasOtherCopy(Item(MediaKind.Movie, new Dictionary<string, string>())));
        _manager.DidNotReceive().GetCount(Arg.Any<InternalItemsQuery>());
    }

    [Fact]
    public void GetVirtualFolders_MapsItemIdNameAndType()
    {
        var id = Guid.NewGuid();
        _manager.GetVirtualFolders().Returns([
            new VirtualFolderInfo { ItemId = id.ToString("N"), Name = "Movies", CollectionType = MediaBrowser.Model.Entities.CollectionTypeOptions.movies },
            new VirtualFolderInfo { ItemId = "not-a-guid", Name = "Broken" },
        ]);

        Assert.Equal([new LibraryRef(id, "Movies", "movies")], _library.GetVirtualFolders());
    }

    private static MediaItem Item(MediaKind kind, Dictionary<string, string> providerIds, Guid series = default, int? season = null, int? episode = null) => new()
    {
        Id = Guid.NewGuid(),
        Kind = kind,
        Name = "x",
        ProviderIds = providerIds,
        SeriesId = series,
        SeasonNumber = season,
        EpisodeNumber = episode,
        Identities = Identities.Of(kind, providerIds, series, season, episode),
    };
}
