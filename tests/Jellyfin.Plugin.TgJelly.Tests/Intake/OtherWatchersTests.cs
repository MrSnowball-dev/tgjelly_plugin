using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Intake;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using MediaBrowser.Controller;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Intake;

public class OtherWatchersTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 18, 10, 0, 0, TimeSpan.Zero));
    private readonly IntakeQueues _queues;

    public OtherWatchersTests() => _queues = new IntakeQueues(_time);

    [Fact]
    public void TaskWatcher_FailedTask_IsTaskFailed_CompletedIsNothing()
    {
        var tasks = Substitute.For<ITaskManager>();
        var watcher = new TaskWatcher(tasks, _queues, new ListLogger<TaskWatcher>());

        watcher.OnTaskCompleted(null, new TaskCompletionEventArgs(Substitute.For<IScheduledTaskWorker>(), new TaskResult { Name = "Extract chapter images", Key = "x", Status = TaskCompletionStatus.Failed, ErrorMessage = "ffmpeg exited with 1" }));
        watcher.OnTaskCompleted(null, new TaskCompletionEventArgs(Substitute.For<IScheduledTaskWorker>(), new TaskResult { Name = "Other", Key = "y", Status = TaskCompletionStatus.Completed }));
        watcher.OnTaskCompleted(null, new TaskCompletionEventArgs(Substitute.For<IScheduledTaskWorker>(), new TaskResult { Name = "Null texts", Status = TaskCompletionStatus.Failed }));

        var events = _queues.TakeOther(10);
        Assert.Equal(2, events.Count);
        Assert.Equal(("server.task_failed", "Extract chapter images", "ffmpeg exited with 1"), (events[0].EventKey, events[0].Values["task"], events[0].Values["error"]));
        Assert.Equal(string.Empty, events[1].Values["error"]);
        Assert.Equal(0, _queues.MediaCount);
    }

    [Fact]
    public void TaskWatcher_RefreshLibrary_IsScanFinished()
    {
        var watcher = new TaskWatcher(Substitute.For<ITaskManager>(), _queues, new ListLogger<TaskWatcher>());

        watcher.OnTaskCompleted(null, new TaskCompletionEventArgs(Substitute.For<IScheduledTaskWorker>(), new TaskResult { Name = "Scan Media Library", Key = "RefreshLibrary", Status = TaskCompletionStatus.Completed }));

        Assert.Equal(MediaIntakeKind.ScanFinished, Assert.Single(_queues.TakeMedia(10)).Kind);
    }

    [Fact]
    public void RestartWatcher_PublishesOnlyWhenARestartIsPending()
    {
        var host = Substitute.For<IServerApplicationHost>();
        var watcher = new RestartWatcher(host, _queues, new ListLogger<RestartWatcher>());

        watcher.OnPendingRestartChanged(null, EventArgs.Empty);
        host.HasPendingRestart.Returns(true);
        watcher.OnPendingRestartChanged(null, EventArgs.Empty);

        var e = Assert.Single(_queues.TakeOther(10));
        Assert.Equal("server.restart_pending", e.EventKey);
        Assert.Empty(e.Values);
    }

    [Fact]
    public async Task StartupWatcher_EmitsServerStartedOnce_AfterStartupCompletes()
    {
        var startup = new FakeStartup { HasCompleted = false };
        using var watcher = new StartupWatcher(startup, new FakeServerInfo(), _queues, _time, new ListLogger<StartupWatcher>());
        await watcher.StartAsync(TestContext.Current.CancellationToken);

        for (var i = 0; i < 3; i++)
        {
            await Eventually.SettleAsync();
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(0, _queues.OtherCount);
        startup.HasCompleted = true;
        await Eventually.AdvanceUntilAsync(_time, TimeSpan.FromSeconds(1), () => _queues.OtherCount == 1, "server.started");
        for (var i = 0; i < 10; i++)
        {
            await Eventually.SettleAsync();
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        await watcher.StopAsync(TestContext.Current.CancellationToken);
        var e = Assert.Single(_queues.TakeOther(10));
        Assert.Equal(("server.started", "12.2.0", "Home"), (e.EventKey, e.Values["version"], e.Values["server"]));
    }


    [Fact]
    public async Task ScanFinishedTask_PublishesScanFinished()
    {
        var progress = new Progress<double>();
        await new ScanFinishedTask(_queues).Run(progress, TestContext.Current.CancellationToken);

        Assert.Equal(MediaIntakeKind.ScanFinished, Assert.Single(_queues.TakeMedia(10)).Kind);
    }

    [Fact]
    public async Task PendingIdSet_ResetReplaces_AndIsThreadSafe()
    {
        var set = new PendingIdSet();
        set.Add(Guid.NewGuid());
        var keep = Guid.NewGuid();
        set.Reset([keep]);
        Assert.Equal(1, set.Count);
        Assert.True(set.Contains(keep));

        await Task.WhenAll(
            Task.Run(() => { for (var i = 0; i < 10_000; i++) set.Add(Guid.NewGuid()); }, TestContext.Current.CancellationToken),
            Task.Run(() => { for (var i = 0; i < 10_000; i++) _ = set.Contains(keep); }, TestContext.Current.CancellationToken));
        set.Remove(keep);
        Assert.False(set.Contains(keep));
    }
}
