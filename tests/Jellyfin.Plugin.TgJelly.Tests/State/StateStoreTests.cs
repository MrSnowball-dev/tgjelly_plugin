using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.State;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.State;

public class StateStoreTests
{
    private static readonly DateTimeOffset Restored = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BeforeLoad_ReadsDefaults_AndRefusesUpdates()
    {
        using var dir = new TempDir();
        var store = NewStore(dir, new FakeTimeProvider());

        Assert.False(store.IsLoaded);
        Assert.Null(store.Read(s => s.Status.BackupRestoredAt));
        Assert.Throws<InvalidOperationException>(() => store.Update(s => s.Status.BackupRestoredAt = Restored));
    }

    [Fact]
    public void Update_IsWrittenOnNextFlush_AndSurvivesReload()
    {
        using var dir = new TempDir();
        var store = NewStore(dir, new FakeTimeProvider());
        store.Load();

        store.Update(s => s.Status.BackupRestoredAt = Restored);
        store.FlushDue();

        var reloaded = NewStore(dir, new FakeTimeProvider());
        reloaded.Load();
        Assert.Equal(Restored, reloaded.Read(s => s.Status.BackupRestoredAt));
        Assert.Contains("\"backupRestoredAt\"", File.ReadAllText(new DataPaths(dir.Path).State), StringComparison.Ordinal);
    }

    [Fact]
    public void FlushDue_WithoutChanges_WritesNothing()
    {
        using var dir = new TempDir();
        var store = NewStore(dir, new FakeTimeProvider());
        store.Load();

        store.FlushDue();

        Assert.False(File.Exists(new DataPaths(dir.Path).State));
        Assert.False(File.Exists(new DataPaths(dir.Path).Queue));
    }

    [Fact]
    public void Queue_IsWrittenAtMostEvery10Seconds_UnlessUrgent()
    {
        using var dir = new TempDir();
        var queuePath = new DataPaths(dir.Path).Queue;
        var time = new FakeTimeProvider();
        var store = NewStore(dir, time);
        store.Load();

        store.UpdateQueue(q => q.Version = 1);
        store.FlushDue();
        Assert.True(File.Exists(queuePath));

        File.Delete(queuePath);
        store.UpdateQueue(q => q.Version = 1);
        time.Advance(TimeSpan.FromSeconds(5));
        store.FlushDue();
        Assert.False(File.Exists(queuePath));

        time.Advance(TimeSpan.FromSeconds(5));
        store.FlushDue();
        Assert.True(File.Exists(queuePath));

        File.Delete(queuePath);
        time.Advance(TimeSpan.FromSeconds(1));
        store.UpdateQueue(q => q.Version = 1, urgent: true);
        store.FlushDue();
        Assert.True(File.Exists(queuePath));
    }

    [Fact]
    public void FlushAll_WritesPendingQueueImmediately()
    {
        using var dir = new TempDir();
        var queuePath = new DataPaths(dir.Path).Queue;
        var store = NewStore(dir, new FakeTimeProvider());
        store.Load();
        store.UpdateQueue(q => q.Version = 1);
        store.FlushDue();
        File.Delete(queuePath);

        store.UpdateQueue(q => q.Version = 1);
        store.FlushAll();

        Assert.True(File.Exists(queuePath));
    }

    [Fact]
    public void CorruptStateFile_IsQuarantined_AndReportedAsWarning()
    {
        using var dir = new TempDir();
        var paths = new DataPaths(dir.Path);
        Directory.CreateDirectory(paths.Root);
        File.WriteAllText(paths.State, "{definitely not json");
        var store = NewStore(dir, new FakeTimeProvider(Restored));

        store.Load();

        Assert.True(store.IsLoaded);
        var warning = Assert.Single(store.Read(s => s.Status.LoadWarnings.ToArray()));
        Assert.Equal("state.json was unreadable and has been moved to state.bad-20261016090000.json.", warning);
        Assert.True(File.Exists(Path.Combine(paths.Root, "state.bad-20261016090000.json")));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void FailedWrite_KeepsTheChange_AndWritesItOnceTheFolderIsWritableAgain()
    {
        // Review Focus 1: a read-only data folder makes both writes fail; neither change may be lost.
        Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.UserName == "root", "needs Unix permissions as a normal user");
        using var dir = new TempDir();
        var paths = new DataPaths(dir.Path);
        Directory.CreateDirectory(paths.Root);
        var store = NewStore(dir, new FakeTimeProvider());
        store.Load();
        store.Update(s => s.Status.BackupRestoredAt = Restored);
        store.UpdateQueue(q => q.Version = 1, urgent: true);

        File.SetUnixFileMode(paths.Root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.ThrowsAny<UnauthorizedAccessException>(store.FlushDue);
        }
        finally
        {
            File.SetUnixFileMode(paths.Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        store.FlushDue();
        Assert.Contains("backupRestoredAt", File.ReadAllText(paths.State), StringComparison.Ordinal);
        Assert.True(File.Exists(paths.Queue));
    }

    [Fact]
    public void OnlyTheStateWriteFails_TheQueueIsStillWritten_AndStateFollowsLater()
    {
        using var dir = new TempDir();
        var paths = new DataPaths(dir.Path);
        Directory.CreateDirectory(paths.State); // a folder where state.json should be: that write fails
        var store = NewStore(dir, new FakeTimeProvider());
        store.Load();
        store.Update(s => s.Status.BackupRestoredAt = Restored);
        store.UpdateQueue(q => q.Version = 1, urgent: true);

        Assert.NotNull(Record.Exception(store.FlushDue));
        Assert.True(File.Exists(paths.Queue));

        Directory.Delete(paths.State);
        store.FlushDue();
        Assert.Contains("backupRestoredAt", File.ReadAllText(paths.State), StringComparison.Ordinal);
    }

    [Fact]
    public void LoadWarnings_AreCapped_KeepingTheNewest()
    {
        using var dir = new TempDir();
        var paths = new DataPaths(dir.Path);
        Directory.CreateDirectory(paths.Root);
        var old = new StateDocument();
        for (var i = 0; i < StateStore.MaxLoadWarnings; i++)
        {
            old.Status.LoadWarnings.Add("old " + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        File.WriteAllText(paths.State, System.Text.Json.JsonSerializer.Serialize(old, JsonDefaults.Options));
        File.WriteAllText(paths.Queue, "{broken");
        var store = NewStore(dir, new FakeTimeProvider(Restored));

        store.Load();

        var warnings = store.Read(s => s.Status.LoadWarnings.ToArray());
        Assert.Equal(StateStore.MaxLoadWarnings, warnings.Length);
        Assert.Equal("old 1", warnings[0]);
        Assert.StartsWith("queue.json was unreadable", warnings[^1], StringComparison.Ordinal);
    }

    private static StateStore NewStore(TempDir dir, FakeTimeProvider time)
        => new(new DataPaths(dir.Path), time, NullLogger<StateStore>.Instance);

    [Fact]
    public void NullForANonNullableSection_IsTreatedAsUnreadable()
    {
        using var dir = new TempDir();
        var paths = new DataPaths(dir.Path);
        Directory.CreateDirectory(paths.Root);
        File.WriteAllText(paths.State, "{\"version\": 1, \"status\": null}");
        var store = NewStore(dir, new FakeTimeProvider(Restored));

        store.Load();

        Assert.NotNull(store.Read(s => s.Status));
        Assert.Single(store.Read(s => s.Status.LoadWarnings.ToArray()));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void UnreadableStateFile_IsALoadWarning_NotAnException()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.UserName == "root", "needs Unix permissions as a normal user");
        using var dir = new TempDir();
        var paths = new DataPaths(dir.Path);
        var first = NewStore(dir, new FakeTimeProvider());
        first.Load();
        first.Update(s => s.Status.BackupRestoredAt = Restored);
        first.FlushAll();
        File.SetUnixFileMode(paths.State, UnixFileMode.None);
        try
        {
            var store = NewStore(dir, new FakeTimeProvider());
            store.Load();

            Assert.True(store.IsLoaded);
            var warning = Assert.Single(store.Read(s => s.Status.LoadWarnings.ToArray()));
            Assert.Contains("state.json", warning, StringComparison.Ordinal);
            Assert.True(File.Exists(paths.State));
            Assert.Empty(Directory.GetFiles(paths.Root, "*.bad-*"));
        }
        finally
        {
            File.SetUnixFileMode(paths.State, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void UnreadableQueueFile_IsNeverOverwritten_AndLoadsOnceReadable()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.UserName == "root", "needs Unix permissions as a normal user");
        using var dir = new TempDir();
        var paths = new DataPaths(dir.Path);
        var time = new FakeTimeProvider(Restored);
        var first = NewStore(dir, time);
        first.Load();
        first.UpdateQueue(q => q.Outbox.Add(new OutboxEntry { EntryId = "e1", DestinationId = "d1", EventKey = "server.started" }));
        first.FlushAll();
        var original = File.ReadAllBytes(paths.Queue);
        File.SetUnixFileMode(paths.Queue, UnixFileMode.None);
        var store = NewStore(dir, time);
        try
        {
            store.Load();
            Assert.Contains(store.Read(s => s.Status.LoadWarnings.ToArray()), w => w.Contains("queue.json", StringComparison.Ordinal) && w.Contains("can't be read", StringComparison.Ordinal));

            store.UpdateQueue(q => q.Outbox.Clear());
            store.FlushAll();
            time.Advance(TimeSpan.FromSeconds(1));
            store.FlushDue();
        }
        finally
        {
            File.SetUnixFileMode(paths.Queue, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        Assert.Equal(original, File.ReadAllBytes(paths.Queue));

        time.Advance(StateStore.BlockedRetryInterval);
        store.FlushDue();

        Assert.Equal("e1", store.ReadQueue(q => q.Outbox.Single().EntryId));
        Assert.Contains("queue.json is readable again; changes made while it couldn't be read were not kept.", store.Read(s => s.Status.LoadWarnings.ToArray()));
    }
}
