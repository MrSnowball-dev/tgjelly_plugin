using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Delivery;
using Jellyfin.Plugin.TgJelly.Library;
using Jellyfin.Plugin.TgJelly.Pipeline;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Pipeline;

public sealed class MediaAggregatorTests : IDisposable
{
    private static readonly LibraryRef Movies = new(Guid.NewGuid(), "Movies", "movies");
    private static readonly LibraryRef Shows = new(Guid.NewGuid(), "Shows", "tvshows");
    private readonly BotTestHarness _h = new();
    private readonly FakeScanState _scan = new();
    private readonly Outbox _outbox;
    private readonly MediaAggregator _aggregator;
    private readonly Destination _d1 = new() { Id = "d1" };
    private readonly Destination _d2 = new() { Id = "d2" };
    private readonly Guid _series = Guid.NewGuid();
    private readonly Guid _season = Guid.NewGuid();

    public MediaAggregatorTests()
    {
        _outbox = new Outbox(_h.State, _h.Strings, _h.Time);
        _aggregator = new MediaAggregator(_h.State, _h.Settings, _outbox, _scan, _h.Time);
    }

    private DateTimeOffset Now => _h.Time.GetUtcNow();

    [Fact]
    public void QuietWindow_ClosesTheGroup()
    {
        var episodes = Enumerable.Range(1, 3).Select(Episode).ToList();
        foreach (var e in episodes)
        {
            Assert.Empty(Add(e, _d1));
            Advance(10);
        }

        Advance(119 - 10);
        Assert.Empty(_aggregator.Tick());
        Assert.Equal(1, OpenCount());

        Advance(1);
        Assert.Empty(_aggregator.Tick());
        Assert.Equal(0, OpenCount());
        Assert.Equal(1, Buffered("d1"));

        Advance(19);
        Assert.Empty(_aggregator.Tick());
        Advance(1);
        var output = Assert.Single(_aggregator.Tick());
        Assert.Equal(("d1", MediaOutputKind.Group), (output.DestinationId, output.Kind));
        var group = Assert.Single(output.Groups);
        Assert.Equal(episodes.Select(e => e.Id.ToString("N")), group.Model.ItemIds);
        Assert.Equal(("media.episodes", "Shows", RenderKind.Media), (group.Model.EventKey, group.Model.LibraryName, group.Model.Kind));
        Assert.Equal(_season.ToString("N"), group.Model.ContainerId);
        Assert.Empty(_h.State.ReadQueue(q => q.ReleaseBuffers));
    }

    [Fact]
    public void SameItemTwice_IsListedOnce_AndFlagsMerge()
    {
        var e1 = Episode(1);
        Add(e1, _d1, upgraded: true);
        Add(e1, _d1, upgraded: true, newSeries: true);
        Add(Episode(2), _d1, upgraded: false);

        var group = Assert.Single(Assert.Single(_aggregator.ScanFinished()).Groups);

        Assert.Equal(2, group.Model.ItemIds.Count);
        Assert.True(group.Model.NewSeries);
        Assert.False(group.Model.Upgraded);
    }

    [Fact]
    public void MaxHold_ClosesABusyGroup()
    {
        var start = Now;
        for (var i = 1; Now - start < TimeSpan.FromMinutes(30); i++)
        {
            Add(Episode(i), _d1);
            Assert.Empty(_aggregator.Tick());
            Assert.Equal(1, OpenCount());
            Advance(60);
        }

        Assert.Empty(_aggregator.Tick());
        Assert.Equal(0, OpenCount());
        Assert.Equal(1, Buffered("d1"));
    }

    [Fact]
    public void ScanFinished_ClosesAndReleasesEverything()
    {
        Add(Movie(), _d1);
        Advance(120);
        _aggregator.Tick();
        Add(Movie(), _d1);
        Add(Movie(), _d2);

        var outputs = _aggregator.ScanFinished();

        Assert.Equal(3, outputs.Count);
        Assert.All(outputs, o => Assert.Equal(MediaOutputKind.Group, o.Kind));
        Assert.Equal(["d1", "d1", "d2"], outputs.Select(o => o.DestinationId).Order());
        Assert.Equal(0, OpenCount());
        Assert.Empty(_h.State.ReadQueue(q => q.ReleaseBuffers));
    }

    [Fact]
    public void ExactlyThreshold_SendsGroups_ThresholdPlusOne_SendsADigest()
    {
        for (var i = 0; i < 10; i++)
        {
            Add(Movie(), _d1);
        }

        var groups = _aggregator.ScanFinished();
        Assert.Equal(10, groups.Count);
        Assert.All(groups, o => Assert.Equal(MediaOutputKind.Group, o.Kind));

        for (var i = 0; i < 11; i++)
        {
            Add(Movie(), _d1);
        }

        var digest = Assert.Single(_aggregator.ScanFinished());
        Assert.Equal(MediaOutputKind.Digest, digest.Kind);
        Assert.Equal(11, digest.Groups.Count);
    }

    [Fact]
    public void GroupsAreReleasedInFirstAtOrder()
    {
        var first = Movie();
        var second = Movie();
        Add(first, _d1);
        Advance(5);
        Add(second, _d1);

        var outputs = _aggregator.ScanFinished();

        Assert.Equal([first.Id.ToString("N"), second.Id.ToString("N")], outputs.Select(o => o.Groups[0].Model.ItemIds[0]));
    }

    [Fact]
    public void HeldWhileScanRunning_UntilScanFinished()
    {
        Add(Movie(), _d1);
        Add(Movie(), _d1);
        Advance(120);
        _scan.IsScanRunning = true;
        Assert.Empty(_aggregator.Tick());
        Assert.Equal(2, Buffered("d1"));

        for (var minute = 0; minute < 25; minute++)
        {
            Advance(60);
            Assert.Empty(_aggregator.Tick());
        }

        Assert.Equal(2, _aggregator.ScanFinished().Count);
    }

    [Fact]
    public void LongScan_ReleasesEvery2Hours()
    {
        _scan.IsScanRunning = true;
        var start = Now;
        var releases = new List<(TimeSpan At, MediaOutput Output)>();
        for (var minute = 0; minute <= 300; minute++)
        {
            if (minute % 10 == 0)
            {
                Add(Movie(), _d1);
            }

            releases.AddRange(_aggregator.Tick().Select(o => (Now - start, o)));
            Advance(60);
        }

        Assert.Equal(2, releases.Count);
        Assert.All(releases, r => Assert.Equal(MediaOutputKind.Digest, r.Output.Kind));
        Assert.InRange(releases[0].At, TimeSpan.FromMinutes(120), TimeSpan.FromMinutes(130));
        Assert.InRange(releases[1].At, TimeSpan.FromMinutes(240), TimeSpan.FromMinutes(260));
    }

    [Fact]
    public void BufferMaxHold_WithoutScan()
    {
        var start = Now;
        MediaOutput? release = null;
        var releasedAt = TimeSpan.Zero;
        for (var seconds = 0; release is null && seconds < 3600; seconds += 10)
        {
            Add(Movie(), _d1);
            release = _aggregator.Tick().SingleOrDefault();
            releasedAt = Now - start;
            Advance(10);
        }

        Assert.NotNull(release);
        Assert.Equal(MediaOutputKind.Digest, release.Kind);
        Assert.InRange(releasedAt, TimeSpan.FromSeconds(120 + 1800), TimeSpan.FromSeconds(120 + 1800 + 10));
    }

    [Fact]
    public void LateItem_WithinLateEditHours_EmitsLateEdit()
    {
        var sent = SendOneGroup(complete: true);
        Advance(5 * 3600);
        var e2 = Episode(2);

        var output = Assert.Single(Add(e2, _d1, newSeries: true));

        Assert.Equal(("d1", MediaOutputKind.LateEdit), (output.DestinationId, output.Kind));
        var group = Assert.Single(output.Groups);
        Assert.Equal(sent.Key, group.Key);
        Assert.Equal([.. sent.Model.ItemIds, e2.Id.ToString("N")], group.Model.ItemIds);
        Assert.True(group.Model.NewSeries);
        Assert.Equal(0, OpenCount());
    }

    [Fact]
    public void LateItem_After6h_StartsANewGroup()
    {
        SendOneGroup(complete: true);
        Advance(7 * 3600);

        Assert.Empty(Add(Episode(2), _d1));
        Assert.Equal(1, OpenCount());
    }

    [Fact]
    public void LateEditHoursZero_NeverEdits()
    {
        Assert.Equal(SettingsUpdateStatus.Saved, _h.Settings.Update(null, c => c.Batching.LateEditHours = 0).Status);
        SendOneGroup(complete: true);
        Advance(300);

        Assert.Empty(Add(Episode(2), _d1));
        Assert.Equal(1, OpenCount());
    }

    [Fact]
    public void LateItem_WhileTheSendIsStillQueued_EmitsLateEdit()
    {
        SendOneGroup(complete: false);
        Advance(7 * 3600);

        Assert.Equal(MediaOutputKind.LateEdit, Assert.Single(Add(Episode(2), _d1)).Kind);
    }

    [Fact]
    public void DigestedGroups_AreNeverEdited()
    {
        Add(Episode(1), _d1);
        for (var i = 0; i < 10; i++)
        {
            Add(Movie(), _d1);
        }

        var digest = Assert.Single(_aggregator.ScanFinished());
        Assert.Equal(MediaOutputKind.Digest, digest.Kind);
        var entry = new OutboxEntry
        {
            EntryId = Guid.NewGuid().ToString("N"), DestinationId = "d1", EventKey = "media.digest", Op = OutboxOp.Send,
            Model = new RenderModel { Kind = RenderKind.Digest, EventKey = "media.digest", Groups = digest.Groups.Select(g => g.Model).ToList() },
            QueuedAt = Now,
        };
        _outbox.Enqueue(entry);
        _outbox.Complete(entry, new SentMessage { MessageId = 1, SentAt = Now });

        Assert.Empty(Add(Episode(2), _d1));
        Assert.Equal(1, OpenCount());
    }

    [Fact]
    public void TwoDestinations_KeepSeparateGroups()
    {
        Add(Episode(1), _d1, _d2);
        Add(Episode(2), _d1);

        var outputs = _aggregator.ScanFinished().OrderBy(o => o.DestinationId).ToList();

        Assert.Equal(["d1", "d2"], outputs.Select(o => o.DestinationId));
        Assert.Equal([2, 1], outputs.Select(o => Assert.Single(o.Groups).Model.ItemIds.Count));
    }

    [Fact]
    public void OpenGroupsCap_ClosesTheOldestIntoTheBuffer()
    {
        _aggregator.OpenGroupsCapOverride = 2;
        var oldest = Movie();
        Add(oldest, _d1);
        Advance(1);
        Add(Movie(), _d1);
        Advance(1);
        Add(Movie(), _d2);
        Add(Movie(), _d1);

        Assert.Equal(2, _h.State.ReadQueue(q => q.OpenGroups.Count(g => g.DestinationId == "d1")));
        Assert.Equal(1, Buffered("d1"));
        Assert.Equal(oldest.Id.ToString("N"), _h.State.ReadQueue(q => q.ReleaseBuffers["d1"].Groups[0].Model.ItemIds[0]));
    }

    [Fact]
    public void BufferCap_ReleasesAtOnce()
    {
        _aggregator.BufferCapOverride = 3;
        _scan.IsScanRunning = true;
        Add(Movie(), _d1);
        Add(Movie(), _d1);
        Advance(60);
        Add(Movie(), _d1);
        Advance(60);
        Assert.Empty(_aggregator.Tick());
        Assert.Equal(2, Buffered("d1"));

        Advance(60);
        var outputs = _aggregator.Tick();

        Assert.Equal(3, outputs.Count);
        Assert.Empty(_h.State.ReadQueue(q => q.ReleaseBuffers));
    }

    [Fact]
    public void BigScan_3000Movies_GivesOneDigestPerDestination()
    {
        _scan.IsScanRunning = true;
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 3000; i++)
        {
            Assert.Empty(Add(Movie(), _d1, _d2));
        }

        var outputs = _aggregator.ScanFinished();
        watch.Stop();

        Assert.Equal(2, outputs.Count);
        Assert.All(outputs, o => Assert.Equal((MediaOutputKind.Digest, 3000), (o.Kind, o.Groups.Count)));
        Assert.Equal(["d1", "d2"], outputs.Select(o => o.DestinationId).Order());
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void IdleTicks_DoNotRewriteQueueJson()
    {
        Add(Movie(), _d1);
        _aggregator.ScanFinished();
        _h.State.FlushAll();
        System.IO.File.Delete(_h.Paths.Queue);

        for (var i = 0; i < 3; i++)
        {
            Advance(11);
            Assert.Empty(_aggregator.Tick());
            _h.State.FlushDue();
        }

        Assert.False(System.IO.File.Exists(_h.Paths.Queue));
    }

    [Fact]
    public void OutputsAreCopies()
    {
        Add(Movie(), _d1);
        var output = Assert.Single(_aggregator.ScanFinished());

        output.Groups[0].Model.ItemIds.Clear();
        Add(Movie(), _d1);

        Assert.Single(Assert.Single(_aggregator.ScanFinished()).Groups[0].Model.ItemIds);
    }

    public void Dispose() => _h.Dispose();

    private MediaGroupState SendOneGroup(bool complete)
    {
        Add(Episode(1), _d1);
        var group = Assert.Single(Assert.Single(_aggregator.ScanFinished()).Groups);
        var entry = new OutboxEntry
        {
            EntryId = Guid.NewGuid().ToString("N"), DestinationId = "d1", EventKey = group.Model.EventKey, GroupKey = group.Key,
            Op = OutboxOp.Send, SentKey = group.Key, Model = group.Model, QueuedAt = Now,
        };
        _outbox.Enqueue(entry);
        if (complete)
        {
            _outbox.Complete(entry, new SentMessage { MessageId = 1, SentAt = Now });
        }

        return group;
    }

    private IReadOnlyList<MediaOutput> Add(MediaItem item, params Destination[] destinations)
        => Add(item, destinations, upgraded: false, newSeries: false);

    private IReadOnlyList<MediaOutput> Add(MediaItem item, Destination destination, bool upgraded = false, bool newSeries = false)
        => Add(item, [destination], upgraded, newSeries);

    private IReadOnlyList<MediaOutput> Add(MediaItem item, Destination[] destinations, bool upgraded, bool newSeries)
    {
        var library = item.Kind == MediaKind.Episode ? Shows : Movies;
        return _aggregator.Add(new MediaReady(item, [library], upgraded, newSeries, Now), destinations);
    }

    private MediaItem Movie() => new() { Id = Guid.NewGuid(), Kind = MediaKind.Movie, Name = "Movie" };

    private MediaItem Episode(int number) => new()
    {
        Id = Guid.NewGuid(), Kind = MediaKind.Episode, Name = $"Episode {number}", SeriesId = _series, SeasonId = _season,
        SeasonNumber = 1, EpisodeNumber = number,
    };

    private int OpenCount() => _h.State.ReadQueue(q => q.OpenGroups.Count);

    private int Buffered(string destinationId)
        => _h.State.ReadQueue(q => q.ReleaseBuffers.TryGetValue(destinationId, out var b) ? b.Groups.Count : 0);

    private void Advance(int seconds) => _h.Time.Advance(TimeSpan.FromSeconds(seconds));
}
