using System;
using Jellyfin.Plugin.TgJelly.Compat;
using Jellyfin.Plugin.TgJelly.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Compat;

public class JellyfinItemsTests
{
    private static readonly Func<Audio, Guid> NoAlbum = _ => throw new InvalidOperationException("albumOf must not be called");

    [Fact]
    public void AudioBook_IsMostDerived_AndNeverLooksUpAnAlbum()
    {
        var id = Guid.NewGuid();
        var facts = JellyfinItems.Describe(new AudioBook { Id = id }, NoAlbum);
        Assert.Equal((MediaKind.AudioBook, id), (facts.Kind, facts.PendingId));
    }

    [Fact]
    public void Audio_PendsItsAlbum()
    {
        var album = Guid.NewGuid();
        var facts = JellyfinItems.Describe(new Audio { Id = Guid.NewGuid() }, _ => album);
        Assert.Equal((MediaKind.Audio, album), (facts.Kind, facts.PendingId));
    }

    [Fact]
    public void Flags()
    {
        Assert.True(JellyfinItems.Describe(new Movie { Id = Guid.NewGuid(), ExtraType = ExtraType.Trailer }, NoAlbum).IsExtra);
        Assert.True(JellyfinItems.Describe(new Movie { Id = Guid.NewGuid(), OwnerId = Guid.NewGuid() }, NoAlbum).HasOwner);
        Assert.True(JellyfinItems.Describe(new Episode { Id = Guid.NewGuid(), IsVirtualItem = true }, NoAlbum).IsVirtual);
        Assert.True(JellyfinItems.Describe(new MusicArtist { Id = Guid.NewGuid() }, NoAlbum).IsItemByName);
    }

    [Fact]
    public void Kinds()
    {
        Assert.Equal(MediaKind.Series, JellyfinItems.Describe(new Series { Id = Guid.NewGuid() }, NoAlbum).Kind);
        Assert.Equal(MediaKind.Other, JellyfinItems.Describe(new Folder { Id = Guid.NewGuid() }, NoAlbum).Kind);
        Assert.Equal(MediaKind.Movie, JellyfinItems.Describe(new Movie { Id = Guid.NewGuid() }, NoAlbum).Kind);
        Assert.Equal(MediaKind.MusicVideo, JellyfinItems.Describe(new MusicVideo { Id = Guid.NewGuid() }, NoAlbum).Kind);
        Assert.Equal(MediaKind.MusicAlbum, JellyfinItems.Describe(new MusicAlbum { Id = Guid.NewGuid() }, NoAlbum).Kind);
        Assert.Equal(MediaKind.Book, JellyfinItems.Describe(new Book { Id = Guid.NewGuid() }, NoAlbum).Kind);
        Assert.Equal(MediaKind.Episode, JellyfinItems.Describe(new Episode { Id = Guid.NewGuid() }, NoAlbum).Kind);
    }

    [Fact]
    public void Episode_IdentitiesIncludeSeriesSeasonAndNumber()
    {
        var series = Guid.NewGuid();
        var facts = JellyfinItems.Describe(new Episode { Id = Guid.NewGuid(), SeriesId = series, ParentIndexNumber = 1, IndexNumber = 3 }, NoAlbum);
        Assert.Equal([$"series:{series:N}:S01E03"], facts.Identities);
    }

    [Fact]
    public void PrimaryVersionSet_IsAnAlternateVersion()
    {
        var movie = new Movie { Id = Guid.NewGuid() };
        Assert.False(JellyfinItems.Describe(movie, NoAlbum).IsAlternateVersion);
        movie.SetPrimaryVersionId(Guid.NewGuid().ToString("N"));
        Assert.True(JellyfinItems.Describe(movie, NoAlbum).IsAlternateVersion);
    }

    // Jellyfin 10.11 stores PrimaryVersionId as a string and 12 as a Guid?; one build reads both.
    [Fact]
    public void IsAlternateVersionValue_BothShapes()
    {
        Assert.False(JellyfinItems.IsAlternateVersionValue(null));
        Assert.False(JellyfinItems.IsAlternateVersionValue(string.Empty));
        Assert.True(JellyfinItems.IsAlternateVersionValue("abc"));
        Assert.False(JellyfinItems.IsAlternateVersionValue(Guid.Empty));
        Assert.False(JellyfinItems.IsAlternateVersionValue((Guid?)null));
        Assert.True(JellyfinItems.IsAlternateVersionValue(Guid.NewGuid()));
    }
}
