using System;
using Jellyfin.Plugin.TgJelly.Library;
using Jellyfin.Plugin.TgJelly.Pipeline;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Pipeline;

public class GroupKeysTests
{
    private static readonly Guid Id = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Parent = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Series = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Season = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Theory]
    [InlineData(MediaKind.Movie, "movie:11111111111111111111111111111111", "media.movie", "11111111111111111111111111111111")]
    [InlineData(MediaKind.MusicVideo, "musicvideo:11111111111111111111111111111111", "media.musicvideo", "11111111111111111111111111111111")]
    [InlineData(MediaKind.Book, "book:11111111111111111111111111111111", "media.book", "11111111111111111111111111111111")]
    [InlineData(MediaKind.AudioBook, "audiobook:22222222222222222222222222222222", "media.book", "22222222222222222222222222222222")]
    [InlineData(MediaKind.MusicAlbum, "album:11111111111111111111111111111111", "media.album", "11111111111111111111111111111111")]
    [InlineData(MediaKind.Episode, "episodes:33333333333333333333333333333333:44444444444444444444444444444444", "media.episodes", "44444444444444444444444444444444")]
    public void OneKeyPerKind(MediaKind kind, string key, string eventKey, string containerId)
    {
        var item = new MediaItem { Id = Id, Kind = kind, ParentId = Parent, SeriesId = Series, SeasonId = Season, SeasonNumber = 2 };

        Assert.Equal((key, eventKey, containerId), GroupKeys.For(item));
    }

    [Fact]
    public void EpisodeWithoutASeasonId_UsesTheSeasonNumber_AndTheSeries()
    {
        var item = new MediaItem { Id = Id, Kind = MediaKind.Episode, SeriesId = Series, SeasonNumber = 2 };

        Assert.Equal(
            ("episodes:33333333333333333333333333333333:s2", "media.episodes", "33333333333333333333333333333333"),
            GroupKeys.For(item));
    }

    [Theory]
    [InlineData(MediaKind.Other)]
    [InlineData(MediaKind.Series)]
    [InlineData(MediaKind.Audio)]
    public void NotAnnounced_IsNotGrouped(MediaKind kind)
    {
        Assert.False(GroupKeys.IsGrouped(kind));
        Assert.Throws<ArgumentOutOfRangeException>(() => GroupKeys.For(new MediaItem { Id = Id, Kind = kind }));
    }
}
