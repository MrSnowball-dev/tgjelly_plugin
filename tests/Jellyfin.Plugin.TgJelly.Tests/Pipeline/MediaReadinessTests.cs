using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Intake;
using Jellyfin.Plugin.TgJelly.Library;
using Jellyfin.Plugin.TgJelly.Pipeline;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Pipeline;

public sealed class MediaReadinessTests : IDisposable
{
    private static readonly LibraryRef Movies = new(Guid.NewGuid(), "Movies", null);

    private readonly BotTestHarness _h = new();
    private readonly FakeLibrary _library = new();
    private readonly PendingIdSet _pending = new();
    private readonly ListLogger<MediaReadiness> _logger = new();
    private readonly MediaReadiness _readiness;

    public MediaReadinessTests() => _readiness = New(_h.State, _pending);

    [Fact]
    public void Added_ThenUpdated_IsReadyOnce()
    {
        var movie = AddMovie();

        Assert.Empty(_readiness.Process(Intake(MediaIntakeKind.Added, movie)));
        var ready = Assert.Single(_readiness.Process(Intake(MediaIntakeKind.Updated, movie)));

        Assert.Equal((movie.Id, Movies), (ready.Item.Id, ready.Libraries.Single()));
        Assert.False(ready.Upgraded);
        Assert.Empty(_readiness.Process(Intake(MediaIntakeKind.Updated, movie)));
        Assert.Equal(0, _readiness.PendingCount);
        Assert.False(_pending.Contains(movie.Id));
    }

    [Fact]
    public void HundredTracksOfOneAlbum_OnePendingAlbum()
    {
        var album = Guid.NewGuid();
        for (var i = 0; i < 100; i++)
        {
            _readiness.Process(new MediaIntake(MediaIntakeKind.Added, new ItemFacts(Guid.NewGuid(), MediaKind.Audio, album, false, false, false, false, false, DateTime.MinValue, []), Now));
        }

        Assert.Equal(1, _readiness.PendingCount);
    }

    [Fact]
    public void NoMetadata_AnnouncedAfterTimeout()
    {
        var movie = AddMovie();
        _readiness.Process(Intake(MediaIntakeKind.Added, movie));

        _h.Time.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1));
        Assert.Empty(_readiness.Tick());
        _h.Time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(movie.Id, Assert.Single(_readiness.Tick()).Item.Id);
    }

    [Fact]
    public void DeletedBeforeReady_DroppedQuietly()
    {
        var gone = new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Movie, Name = "Gone" };
        _readiness.Process(Intake(MediaIntakeKind.Added, gone));

        Assert.Empty(_readiness.Process(Intake(MediaIntakeKind.Updated, gone)));

        Assert.Equal(0, _readiness.PendingCount);
        Assert.DoesNotContain(_logger.Entries, e => e.Level > LogLevel.Information);
    }

    [Fact]
    public void Upgrade_LibraryHit_IsSuppressed()
    {
        var movie = AddMovie(identities: ["imdb:tt1"]);
        _library.OtherCopies.Add(movie.Id);

        Assert.Empty(Ready(movie));
    }

    [Fact]
    public void Upgrade_RemovedWithin48h_IsSuppressed()
    {
        _readiness.Process(new MediaIntake(MediaIntakeKind.Removed, new ItemFacts(Guid.NewGuid(), MediaKind.Movie, Guid.Empty, false, false, false, false, false, DateTime.MinValue, ["imdb:tt1"]), Now));
        _h.Time.Advance(TimeSpan.FromHours(47));
        Assert.Empty(Ready(AddMovie(identities: ["imdb:tt1"])));

        _h.Time.Advance(TimeSpan.FromHours(2));
        Assert.Single(Ready(AddMovie(identities: ["imdb:tt1"])));
    }

    [Fact]
    public void Upgrade_EmptyIdentitySet_IsNeverAnUpgrade()
    {
        var movie = AddMovie(identities: []);
        _library.OtherCopies.Add(movie.Id);

        Assert.Single(Ready(movie));
    }

    [Fact]
    public void Upgrade_AnnounceUpgradesOn_MarksUpgraded()
    {
        _h.Settings.Update(null, c => c.Message.AnnounceUpgrades = true);
        var movie = AddMovie(identities: ["imdb:tt1"]);
        _library.OtherCopies.Add(movie.Id);

        Assert.True(Assert.Single(Ready(movie)).Upgraded);
    }

    [Fact]
    public void NewSeries_FlagsEpisodesOfThatSeriesFor24h()
    {
        var series = Guid.NewGuid();
        _readiness.Process(new MediaIntake(MediaIntakeKind.SeriesAdded, new ItemFacts(series, MediaKind.Series, series, false, false, false, false, false, DateTime.MinValue, []), Now));

        Assert.True(Assert.Single(Ready(AddEpisode(series))).NewSeries);
        Assert.False(Assert.Single(Ready(AddEpisode(Guid.NewGuid()))).NewSeries);
        _h.Time.Advance(TimeSpan.FromHours(25));
        _readiness.Tick();
        Assert.False(Assert.Single(Ready(AddEpisode(series))).NewSeries);
    }

    [Fact]
    public void Restart_KeepsPendingItems()
    {
        var movie = AddMovie();
        _readiness.Process(Intake(MediaIntakeKind.Added, movie));
        _h.State.FlushAll();

        var state = new StateStore(_h.Paths, _h.Time, NullLogger<StateStore>.Instance);
        state.Load();
        var pending = new PendingIdSet();
        var readiness = New(state, pending);
        readiness.Load();

        Assert.True(pending.Contains(movie.Id));
        Assert.Equal(movie.Id, Assert.Single(readiness.Process(Intake(MediaIntakeKind.Updated, movie))).Item.Id);
    }

    [Fact]
    public void PendingCap_TreatsTheOldestAsReady()
    {
        _readiness.CapOverride = 2;
        var first = AddMovie();
        _readiness.Process(Intake(MediaIntakeKind.Added, first));
        _h.Time.Advance(TimeSpan.FromSeconds(1));
        _readiness.Process(Intake(MediaIntakeKind.Added, AddMovie()));
        _h.Time.Advance(TimeSpan.FromSeconds(1));

        var ready = _readiness.Process(Intake(MediaIntakeKind.Added, AddMovie()));

        Assert.Equal(first.Id, Assert.Single(ready).Item.Id);
        Assert.Equal(2, _readiness.PendingCount);
        Assert.Equal(1, _h.State.Read(s => s.Status.Counters.Evictions));
    }

    [Fact]
    public void TtlAndCaps_PruneRemovedAndNewSeries()
    {
        _readiness.CapOverride = 2;
        foreach (var identity in new[] { "imdb:a", "imdb:b", "imdb:c" })
        {
            _readiness.Process(new MediaIntake(MediaIntakeKind.Removed, new ItemFacts(Guid.NewGuid(), MediaKind.Movie, Guid.Empty, false, false, false, false, false, DateTime.MinValue, [identity]), Now));
            _h.Time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(["imdb:b", "imdb:c"], _h.State.ReadQueue(q => q.RemovedIdentities.Keys.Order().ToList()));
        Assert.Equal(1, _h.State.Read(s => s.Status.Counters.Evictions));

        _h.Time.Advance(TimeSpan.FromHours(49));
        _readiness.Tick();
        Assert.Empty(_h.State.ReadQueue(q => q.RemovedIdentities));
    }

    public void Dispose() => _h.Dispose();

    private DateTimeOffset Now => _h.Time.GetUtcNow();

    private MediaReadiness New(StateStore state, PendingIdSet pending) => new(state, _h.Settings, _library, pending, _h.Time, _logger);

    private IReadOnlyList<MediaReady> Ready(MediaItem item)
    {
        _readiness.Process(Intake(MediaIntakeKind.Added, item));
        return _readiness.Process(Intake(MediaIntakeKind.Updated, item));
    }

    private MediaItem AddMovie(IReadOnlyList<string>? identities = null)
        => _library.Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Movie, Name = "M", Identities = identities ?? [] }, Movies);

    private MediaItem AddEpisode(Guid series)
        => _library.Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Episode, Name = "E", SeriesId = series }, Movies);

    private MediaIntake Intake(MediaIntakeKind kind, MediaItem item)
        => new(kind, new ItemFacts(item.Id, item.Kind, item.Id, false, false, false, false, false, kind == MediaIntakeKind.Updated ? Now.UtcDateTime : DateTime.MinValue, item.Identities), Now);
}
