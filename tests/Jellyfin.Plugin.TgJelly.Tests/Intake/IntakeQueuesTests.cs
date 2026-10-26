using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Intake;
using Jellyfin.Plugin.TgJelly.Library;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Intake;

public class IntakeQueuesTests
{
    private static readonly IReadOnlyDictionary<string, string> NoValues = new Dictionary<string, string>();

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 18, 10, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Full_EvictsUpdatedBeforeAdded()
    {
        var queues = new IntakeQueues(_time);
        for (var i = 0; i < IntakeQueues.MediaCapacity - 1; i++)
        {
            Assert.True(queues.PublishMedia(MediaIntakeKind.Added, Facts()));
        }

        Assert.True(queues.PublishMedia(MediaIntakeKind.Updated, Facts()));
        Assert.True(queues.PublishMedia(MediaIntakeKind.Added, Facts()));

        Assert.Equal(IntakeQueues.MediaCapacity, queues.MediaCount);
        Assert.DoesNotContain(queues.TakeMedia(int.MaxValue), m => m.Kind == MediaIntakeKind.Updated);
        Assert.Equal(1, queues.TakeDropped());
        Assert.Equal(0, queues.TakeDropped());
    }

    [Fact]
    public void Full_WithoutUpdated_DropsTheIncomingUpdated()
    {
        var queues = new IntakeQueues(_time);
        for (var i = 0; i < IntakeQueues.MediaCapacity; i++)
        {
            queues.PublishMedia(MediaIntakeKind.Added, Facts());
        }

        Assert.False(queues.PublishMedia(MediaIntakeKind.Updated, Facts()));

        Assert.All(queues.TakeMedia(int.MaxValue), m => Assert.Equal(MediaIntakeKind.Added, m.Kind));
        Assert.Equal(1, queues.TakeDropped());
    }

    [Fact]
    public void Full_OnlyAdded_DropsTheOldestAdded_NeverAScanFinished()
    {
        var queues = new IntakeQueues(_time);
        queues.PublishMedia(MediaIntakeKind.ScanFinished, null);
        var oldest = Facts();
        queues.PublishMedia(MediaIntakeKind.Added, oldest);
        for (var i = 0; i < IntakeQueues.MediaCapacity - 2; i++)
        {
            queues.PublishMedia(MediaIntakeKind.Added, Facts());
        }

        var newest = Facts();
        Assert.True(queues.PublishMedia(MediaIntakeKind.Added, newest));

        var all = queues.TakeMedia(int.MaxValue);
        Assert.Equal(MediaIntakeKind.ScanFinished, all[0].Kind);
        Assert.DoesNotContain(all, m => m.Item == oldest);
        Assert.Same(newest, all[^1].Item);
    }

    [Fact]
    public void Other_Full_DropsTheNewest()
    {
        var queues = new IntakeQueues(_time);
        for (var i = 0; i < IntakeQueues.OtherCapacity; i++)
        {
            Assert.True(queues.Publish("server.started", NoValues));
        }

        Assert.False(queues.Publish("server.task_failed", NoValues));

        Assert.All(queues.TakeOther(int.MaxValue), e => Assert.Equal("server.started", e.EventKey));
        Assert.Equal(1, queues.TakeDropped());
    }


    [Fact]
    public void Publish_StampsTheClock_AndTakeIsFifo()
    {
        var queues = new IntakeQueues(_time);
        queues.Publish("server.started", new Dictionary<string, string> { ["version"] = "12.2.0" });
        _time.Advance(TimeSpan.FromSeconds(1));
        queues.Publish("server.task_failed", NoValues);

        var taken = queues.TakeOther(1);
        var e = Assert.Single(taken);
        Assert.Equal(("server.started", _time.GetUtcNow() - TimeSpan.FromSeconds(1), "12.2.0"), (e.EventKey, e.At, e.Values["version"]));
        Assert.Equal(1, queues.OtherCount);
    }

    [Fact]
    public async Task WaitAsync_WakesOnPublish()
    {
        var queues = new IntakeQueues(_time);
        var wait = queues.WaitAsync(TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        Assert.False(wait.IsCompleted);

        queues.PublishMedia(MediaIntakeKind.ScanFinished, null);

        await wait.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    private static ItemFacts Facts()
    {
        var id = Guid.NewGuid();
        return new ItemFacts(id, MediaKind.Movie, id, false, false, false, false, false, DateTime.MinValue, []);
    }
}
