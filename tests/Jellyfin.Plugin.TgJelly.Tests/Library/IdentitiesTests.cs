using System;
using System.Collections.Generic;
using Jellyfin.Plugin.TgJelly.Library;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Library;

public class IdentitiesTests
{
    [Fact]
    public void Movie_ProviderIds()
        => Assert.Equal(["imdb:tt0111161", "tmdb:278"], Identities.Of(MediaKind.Movie, new Dictionary<string, string> { ["Tmdb"] = "278", ["Imdb"] = "tt0111161" }, Guid.Empty, null, null));

    [Fact]
    public void Album_MusicBrainz()
        => Assert.Equal(["musicbrainz:abc"], Identities.Of(MediaKind.MusicAlbum, new Dictionary<string, string> { ["MusicBrainzAlbum"] = "abc" }, Guid.Empty, null, null));

    [Fact]
    public void Episode_AddsSeriesSeasonAndNumber()
    {
        var series = Guid.NewGuid();
        Assert.Equal(["tvdb:9", $"series:{series:N}:S02E05"], Identities.Of(MediaKind.Episode, new Dictionary<string, string> { ["Tvdb"] = "9" }, series, 2, 5));
    }

    [Fact]
    public void Episode_WithoutNumbers_OnlyProviderIds()
        => Assert.Equal(["tvdb:9"], Identities.Of(MediaKind.Episode, new Dictionary<string, string> { ["Tvdb"] = "9" }, Guid.NewGuid(), null, 5));

    [Fact]
    public void NullOrEmpty_IsEmpty()
    {
        Assert.Empty(Identities.Of(MediaKind.Movie, null, Guid.Empty, null, null));
        Assert.Empty(Identities.Of(MediaKind.Movie, new Dictionary<string, string>(), Guid.Empty, null, null));
    }

    [Fact]
    public void Keys_CaseInsensitive_EmptyValuesSkipped_InFixedOrder()
        => Assert.Equal(
            ["imdb:tt1", "tvdb:3", "musicbrainz:m"],
            Identities.Of(MediaKind.Movie, new Dictionary<string, string> { ["musicbrainzalbum"] = "m", ["TVDB"] = "3", ["tmdb"] = " ", ["imdb"] = "tt1" }, Guid.Empty, null, null));
}
