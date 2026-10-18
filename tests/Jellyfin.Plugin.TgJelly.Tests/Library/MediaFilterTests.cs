using System;
using Jellyfin.Plugin.TgJelly.Library;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Library;

public class MediaFilterTests
{
    public static TheoryData<MediaKind, FilterVerdict> Kinds => new()
    {
        { MediaKind.Movie, FilterVerdict.Keep },
        { MediaKind.Episode, FilterVerdict.Keep },
        { MediaKind.MusicAlbum, FilterVerdict.Keep },
        { MediaKind.MusicVideo, FilterVerdict.Keep },
        { MediaKind.Book, FilterVerdict.Keep },
        { MediaKind.AudioBook, FilterVerdict.Keep },
        { MediaKind.Audio, FilterVerdict.Keep },
        { MediaKind.Series, FilterVerdict.SeriesContext },
        { MediaKind.Other, FilterVerdict.Drop },
    };

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Kind_DecidesTheVerdict(MediaKind kind, FilterVerdict expected)
        => Assert.Equal(expected, MediaFilter.Classify(Facts(kind)));

    [Fact]
    public void AudioWithoutAnAlbum_IsDropped()
        => Assert.Equal(FilterVerdict.Drop, MediaFilter.Classify(Facts(MediaKind.Audio) with { PendingId = Guid.Empty }));

    [Theory]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, false, false, true, false)]
    [InlineData(false, false, false, false, true)]
    public void ExcludedMovies_AreDropped(bool isVirtual, bool hasOwner, bool isExtra, bool isAlternate, bool isItemByName)
        => Assert.Equal(
            FilterVerdict.Drop,
            MediaFilter.Classify(Facts(MediaKind.Movie) with { IsVirtual = isVirtual, HasOwner = hasOwner, IsExtra = isExtra, IsAlternateVersion = isAlternate, IsItemByName = isItemByName }));

    [Fact]
    public void IsAnnounced_IsTrueForTheSevenKeptKinds()
    {
        Assert.All(new[] { MediaKind.Movie, MediaKind.Episode, MediaKind.MusicAlbum, MediaKind.Audio, MediaKind.MusicVideo, MediaKind.Book, MediaKind.AudioBook }, k => Assert.True(MediaFilter.IsAnnounced(k)));
        Assert.False(MediaFilter.IsAnnounced(MediaKind.Series));
        Assert.False(MediaFilter.IsAnnounced(MediaKind.Other));
    }

    private static ItemFacts Facts(MediaKind kind)
    {
        var id = Guid.NewGuid();
        return new ItemFacts(id, kind, id, false, false, false, false, false, DateTime.MinValue, []);
    }
}
