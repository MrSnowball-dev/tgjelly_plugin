using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Intake;
using Jellyfin.Plugin.TgJelly.Library;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Pipeline;

public sealed class NotificationPipelineTests : IDisposable
{
    private static readonly LibraryRef Movies = new(Guid.NewGuid(), "Movies", "movies");
    private static readonly LibraryRef Shows = new(Guid.NewGuid(), "Shows", "tvshows");
    private static readonly Guid Season1 = Guid.NewGuid();
    private static readonly Guid Season2 = Guid.NewGuid();
    private readonly BotTestHarness _h = new();
    private readonly FakeLibrary _library = new();
    private readonly FakeScanState _scan = new();
    private readonly PipelineRig _rig;

    public NotificationPipelineTests()
    {
        _rig = new PipelineRig(_h, _library, _scan);
        _rig.Pipeline.Start();
    }

    [Fact]
    public void MovieAdded_Ready_Released_IsOneOutboxEntryWithPoster()
    {
        _h.AddDestination(-1001, preset: PresetKind.NewMedia);
        var poster = Path.Combine(_h.Dir.Path, "poster.jpg");
        File.WriteAllBytes(poster, [1, 2, 3]);
        var movie = _library.Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Movie, Name = "Arrival", Primary = new ImageRef(poster, true, DateTime.UnixEpoch) }, Movies);

        _rig.Added(movie);
        Assert.Equal(1, _rig.Pipeline.RunOnce());
        _rig.Updated(movie);
        RunFor(_rig, seconds: 150);

        var entry = Assert.Single(_rig.Outbox.Snapshot());
        Assert.Equal((OutboxOp.Send, "media.movie", $"movie:{movie.Id:N}", $"movie:{movie.Id:N}"), (entry.Op, entry.EventKey, entry.SentKey, entry.GroupKey));
        Assert.Equal(poster, entry.Message.PosterPath);
        Assert.Equal([movie.Id.ToString("N")], entry.Model!.ItemIds);
        Assert.Contains("<b>Arrival</b>", entry.Message.Caption, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryFilter_SkipsOtherLibraries()
    {
        var d = _h.AddDestination(-1001, preset: PresetKind.NewMedia);
        Assert.Equal(SettingsUpdateStatus.Saved, _h.Settings.Update(null, c => c.Destinations[0].LibraryIds = [Movies.Id.ToString("N")]).Status);
        var movie = _library.Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Movie, Name = "In" }, Movies);
        var other = _library.Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Movie, Name = "Out" }, Shows);

        Ready(_rig, movie, other);
        RunFor(_rig, seconds: 150);

        var entry = Assert.Single(_rig.Outbox.Snapshot());
        Assert.Equal((d.Id, $"movie:{movie.Id:N}"), (entry.DestinationId, entry.SentKey));
    }

    [Fact]
    public void LoginFailedTwice_IsSendThenEditMergedIntoTheQueuedSend()
    {
        _h.AddDestination(-1001, preset: PresetKind.AdminAlerts);

        _rig.Queues.Publish("security.login_failed", Failed("1.1.1.1"));
        _rig.Queues.Publish("security.login_failed", Failed("1.1.1.2"));
        _rig.Pipeline.RunOnce();

        var entry = Assert.Single(_rig.Outbox.Snapshot());
        Assert.Equal((OutboxOp.Send, "login_failed:ALICE", "security.login_failed"), (entry.Op, entry.SentKey, entry.GroupKey));
        Assert.Contains("×2", entry.Message.Text, StringComparison.Ordinal);
        Assert.Equal("2", entry.Model!.Values[EventFields.Attempts]);
    }

    [Fact]
    public void ServerStarted_GoesOnlyToSubscribers()
    {
        _h.AddDestination(-1001, preset: PresetKind.NewMedia);
        var admin = _h.AddDestination(-1002, preset: PresetKind.AdminAlerts);

        _rig.Queues.Publish("server.started", new Dictionary<string, string> { [EventFields.Version] = "12.2.0", [EventFields.Server] = "Home" });
        _rig.Pipeline.RunOnce();

        var entry = Assert.Single(_rig.Outbox.Snapshot());
        Assert.Equal((admin.Id, "server.started"), (entry.DestinationId, entry.EventKey));
        Assert.StartsWith("✅ Jellyfin is up", entry.Message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderFailure_IsRecorded_AndOtherOutputsStillGo()
    {
        _h.AddDestination(-1001, preset: PresetKind.NewMedia);
        var good = _library.Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Movie, Name = "Good" }, Movies);
        var bad = _library.Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Movie, Name = "Bad" }, Movies);
        Ready(_rig, good, bad);
        _library.GetItemOverride = id => id == bad.Id ? throw new InvalidOperationException("library broke") : _library.Items.GetValueOrDefault(id);

        RunFor(_rig, seconds: 150);

        var entry = Assert.Single(_rig.Outbox.Snapshot());
        Assert.Equal($"movie:{good.Id:N}", entry.SentKey);
        Assert.Contains(_h.State.LastErrorsSnapshot(), e => e.Source == "pipeline" && e.Message.Contains("library broke", StringComparison.Ordinal));
        Assert.Equal(1, _rig.Logger.Count(LogLevel.Error));
    }

    [Fact]
    public void IntakeDrops_AreCounted()
    {
        for (var i = 0; i <= IntakeQueues.OtherCapacity; i++)
        {
            _rig.Queues.Publish("server.started", new Dictionary<string, string>());
        }

        _rig.Pipeline.RunOnce();

        Assert.Equal(1, _h.State.Read(s => s.Status.Counters.DroppedIntake));
    }

    [Fact]
    public void ScanFinished_ReleasesAtOnce()
    {
        _h.AddDestination(-1001, preset: PresetKind.NewMedia);
        _scan.IsScanRunning = true;
        var movie = _library.Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Movie, Name = "Arrival" }, Movies);
        Ready(_rig, movie);
        Assert.Empty(_rig.Outbox.Snapshot());

        _scan.IsScanRunning = false;
        _rig.Queues.PublishMedia(MediaIntakeKind.ScanFinished, null);
        _rig.Pipeline.RunOnce();

        Assert.Single(_rig.Outbox.Snapshot());
    }

    [Fact]
    public void RestartMidBatch_AnnouncesEachGroupOnce()
    {
        _h.AddDestination(-1001, preset: PresetKind.NewMedia);
        var movie = _library.Add(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Movie, Name = "Buffered" }, Movies);
        Ready(_rig, movie);
        _h.Time.Advance(TimeSpan.FromSeconds(121));
        _rig.Pipeline.RunOnce();
        Assert.Equal(1, _h.State.ReadQueue(q => q.ReleaseBuffers.Values.Sum(b => b.Groups.Count)));

        var series = Guid.NewGuid();
        var season1 = Enumerable.Range(1, 3).Select(i => Episode(series, Season1, i)).ToList();
        var season2 = Episode(series, Season2, 1);
        foreach (var e in season1)
        {
            _rig.Added(e);
        }

        _rig.Updated(season1[0]);
        _rig.Added(season2);
        _rig.Updated(season2);
        _rig.Pipeline.RunOnce();
        Assert.Equal(2, _h.State.ReadQueue(q => q.OpenGroups.Count));
        _h.State.FlushAll();

        var state = new StateStore(_h.Paths, _h.Time, NullLogger<StateStore>.Instance);
        state.Load();
        var restarted = new PipelineRig(_h, _library, _scan, state);
        restarted.Pipeline.Start();
        restarted.Updated(season1[1]);
        restarted.Updated(season1[2]);
        RunFor(restarted, seconds: 300);

        var entries = restarted.Outbox.Snapshot();
        Assert.All(entries, e => Assert.Equal(OutboxOp.Send, e.Op));
        Assert.Equal(3, entries.Count);
        Assert.Equal(3, entries.Select(e => e.SentKey).Distinct().Count());
        var ids = entries.SelectMany(e => e.Model!.ItemIds).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(
            new[] { movie.Id, season2.Id }.Concat(season1.Select(e => e.Id)).Select(id => id.ToString("N")).Order(),
            ids.Order());
    }

    public void Dispose() => _h.Dispose();

    private static Dictionary<string, string> Failed(string ip) => new()
    {
        [EventFields.User] = "alice", [EventFields.UserKey] = "ALICE", [EventFields.Ip] = ip, [EventFields.Client] = "Infuse",
    };

    private MediaItem Episode(Guid series, Guid season, int number) => _library.Add(
        new MediaItem
        {
            Id = Guid.NewGuid(), Kind = MediaKind.Episode, Name = $"E{number}", SeriesId = series, SeriesName = "Severance",
            SeasonId = season, SeasonNumber = season == Season1 ? 1 : 2, EpisodeNumber = number,
        },
        Shows);

    private void Ready(PipelineRig rig, params MediaItem[] items)
    {
        foreach (var item in items)
        {
            rig.Added(item);
            rig.Updated(item);
        }

        rig.Pipeline.RunOnce();
    }

    private void RunFor(PipelineRig rig, int seconds)
    {
        for (var i = 0; i < seconds; i++)
        {
            rig.Pipeline.RunOnce();
            _h.Time.Advance(TimeSpan.FromSeconds(1));
        }

        rig.Pipeline.RunOnce();
    }
}
