using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Intake;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Intake;

public sealed class LibraryWatcherTests : IDisposable
{
    private readonly ILibraryManager _manager = Substitute.For<ILibraryManager>();
    private readonly IntakeQueues _queues = new(IntakeQueuesTests.Compatible, new FakeTimeProvider());
    private readonly PendingIdSet _pending = new();
    private readonly ListLogger<LibraryWatcher> _logger = new();
    private readonly LibraryWatcher _watcher;

    public LibraryWatcherTests() => _watcher = new LibraryWatcher(_manager, _queues, _pending, IntakeQueuesTests.Compatible, _logger);

    [Fact]
    public void Added_KeptMovie_IsQueued_AndMarkedPending()
    {
        var movie = new Movie { Id = Guid.NewGuid() };

        _watcher.OnItemAdded(null, new ItemChangeEventArgs { Item = movie });

        var intake = Assert.Single(_queues.TakeMedia(10));
        Assert.Equal((MediaIntakeKind.Added, movie.Id), (intake.Kind, intake.Item!.Id));
        Assert.True(_pending.Contains(movie.Id));
    }

    [Fact]
    public void Added_DroppedKind_QueuesNothing()
    {
        _watcher.AlbumOf = _ => Guid.Empty;
        _watcher.OnItemAdded(null, new ItemChangeEventArgs { Item = new Movie { Id = Guid.NewGuid(), ExtraType = ExtraType.Trailer } });
        _watcher.OnItemAdded(null, new ItemChangeEventArgs { Item = new MusicArtist { Id = Guid.NewGuid() } });
        _watcher.OnItemAdded(null, new ItemChangeEventArgs { Item = new Audio { Id = Guid.NewGuid() } });

        Assert.Equal(0, _queues.MediaCount);
        Assert.Equal(0, _pending.Count);
    }

    [Fact]
    public void Added_Series_QueuesSeriesAdded()
    {
        _watcher.OnItemAdded(null, new ItemChangeEventArgs { Item = new Series { Id = Guid.NewGuid() } });

        Assert.Equal(MediaIntakeKind.SeriesAdded, Assert.Single(_queues.TakeMedia(10)).Kind);
        Assert.Equal(0, _pending.Count);
    }

    [Fact]
    public void Added_TwoTracksOfOneAlbum_PendAlbumOnce()
    {
        var album = Guid.NewGuid();
        _watcher.AlbumOf = _ => album;

        _watcher.OnItemAdded(null, new ItemChangeEventArgs { Item = new Audio { Id = Guid.NewGuid() } });
        _watcher.OnItemAdded(null, new ItemChangeEventArgs { Item = new Audio { Id = Guid.NewGuid() } });

        Assert.All(_queues.TakeMedia(10), m => Assert.Equal((MediaIntakeKind.Added, album), (m.Kind, m.Item!.PendingId)));
        Assert.Equal(1, _pending.Count);
    }

    [Fact]
    public void Updated_ForwardedOnlyWhenPendingAndRefreshed()
    {
        var pendingRefreshed = new Movie { Id = Guid.NewGuid(), DateLastRefreshed = DateTime.UtcNow };
        var pendingNotRefreshed = new Movie { Id = Guid.NewGuid() };
        var notPending = new Movie { Id = Guid.NewGuid(), DateLastRefreshed = DateTime.UtcNow };
        _pending.Add(pendingRefreshed.Id);
        _pending.Add(pendingNotRefreshed.Id);

        foreach (var item in new[] { pendingRefreshed, pendingNotRefreshed, notPending })
        {
            _watcher.OnItemUpdated(null, new ItemChangeEventArgs { Item = item });
        }

        var intake = Assert.Single(_queues.TakeMedia(10));
        Assert.Equal((MediaIntakeKind.Updated, pendingRefreshed.Id), (intake.Kind, intake.Item!.PendingId));
    }

    [Fact]
    public void Updated_TrackOfAPendingAlbum_ForwardsTheAlbum()
    {
        var album = Guid.NewGuid();
        _pending.Add(album);
        _watcher.AlbumOf = _ => album;

        _watcher.OnItemUpdated(null, new ItemChangeEventArgs { Item = new Audio { Id = Guid.NewGuid(), DateLastRefreshed = DateTime.UtcNow } });

        Assert.Equal(album, Assert.Single(_queues.TakeMedia(10)).Item!.PendingId);
    }

    [Fact]
    public void Removed_AnnouncedKind_QueuesRemovedWithIdentities()
    {
        var movie = new Movie { Id = Guid.NewGuid(), ProviderIds = new() { ["Imdb"] = "tt1" } };

        _watcher.OnItemRemoved(null, new ItemChangeEventArgs { Item = movie });

        var intake = Assert.Single(_queues.TakeMedia(10));
        Assert.Equal((MediaIntakeKind.Removed, "imdb:tt1"), (intake.Kind, intake.Item!.Identities.Single()));
    }

    [Fact]
    public void Removed_Virtual_QueuesNothing()
    {
        _watcher.OnItemRemoved(null, new ItemChangeEventArgs { Item = new Episode { Id = Guid.NewGuid(), IsVirtualItem = true } });

        Assert.Equal(0, _queues.MediaCount);
    }

    [Fact]
    public void ThrowingItem_IsLoggedAndSkipped()
    {
        _watcher.AlbumOf = _ => throw new InvalidOperationException("db");

        _watcher.OnItemAdded(null, new ItemChangeEventArgs { Item = null! });
        _watcher.OnItemUpdated(null, new ItemChangeEventArgs { Item = null! });
        _watcher.OnItemRemoved(null, new ItemChangeEventArgs { Item = null! });
        _watcher.OnItemAdded(null, new ItemChangeEventArgs { Item = new Audio { Id = Guid.NewGuid() } });

        Assert.Equal(4, _logger.Count(LogLevel.Error));
        Assert.Equal(0, _queues.MediaCount);
    }

    [Fact]
    public async Task Start_Subscribes_AndStop_Unsubscribes()
    {
        await _watcher.StartAsync(TestContext.Current.CancellationToken);
        await Eventually.TrueAsync(() => _manager.ReceivedCalls().Count(c => c.GetMethodInfo().Name.StartsWith("add_", StringComparison.Ordinal)) == 3, "subscribed");

        await _watcher.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, _manager.ReceivedCalls().Count(c => c.GetMethodInfo().Name.StartsWith("remove_", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Incompatible_NeverSubscribes()
    {
        using var watcher = new LibraryWatcher(_manager, _queues, _pending, IntakeQueuesTests.Compatible with { IsCompatible = false }, _logger);

        await watcher.StartAsync(TestContext.Current.CancellationToken);
        await Eventually.SettleAsync();
        await watcher.StopAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(_manager.ReceivedCalls(), c => c.GetMethodInfo().Name.StartsWith("add_", StringComparison.Ordinal));
    }

    public void Dispose() => _watcher.Dispose();
}
