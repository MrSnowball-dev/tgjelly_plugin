using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Log;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Log;

public sealed class EventLogTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly DataPaths _paths;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));

    public EventLogTests() => _paths = new DataPaths(_dir.Path);

    private DateTimeOffset Now => _time.GetUtcNow();

    [Fact]
    public async Task EventLog_Upsert_UpdatesInPlace()
    {
        using var log = NewLog();
        await log.Loaded;

        log.Upsert(Row("m1", Now, LogResult.Waiting));
        _time.Advance(TimeSpan.FromMinutes(2));
        log.Upsert(Row("m1", Now - TimeSpan.FromMinutes(2), LogResult.Sent) with { UpdatedAt = Now });
        log.Upsert(Row("m2", Now, LogResult.Skipped));

        var page = log.Query(new LogQuery());
        Assert.Equal(["m2", "m1"], page.Items.Select(i => i.Id));
        var m1 = page.Items[1];
        Assert.Equal((LogResult.Sent, Now), (m1.Result, m1.UpdatedAt));
        Assert.Equal(Now, page.LastSentAt);
        Assert.Equal(Now, page.LastEventAt);

        log.Flush();
        using var reloaded = NewLog();
        await reloaded.Loaded;
        Assert.Equal(["m2", "m1"], reloaded.Query(new LogQuery()).Items.Select(i => i.Id));
        Assert.False(reloaded.Query(new LogQuery()).StartedEmpty);
    }

    [Fact]
    public async Task EventLog_Retention_7DaysAnd5000()
    {
        using var log = NewLog();
        await log.Loaded;
        log.Upsert(Row("old", Now - TimeSpan.FromDays(7) - TimeSpan.FromMinutes(1), LogResult.Sent));
        log.Upsert(Row("week", Now - TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1), LogResult.Sent));
        for (var i = 0; i < EventLog.MaxRows; i++)
        {
            log.Upsert(Row("r" + i, Now + TimeSpan.FromSeconds(i), LogResult.Sent));
        }

        var all = log.Query(new LogQuery { Limit = 10_000 }).Items;
        Assert.Equal(EventLog.MaxRows, all.Count);
        Assert.DoesNotContain(all, i => i.Id is "old" or "week");
        Assert.Equal("r4999", all[0].Id);
        Assert.Equal("r0", all[^1].Id);

        log.Upsert(Row("newest", Now + TimeSpan.FromDays(1), LogResult.Sent));
        all = log.Query(new LogQuery { Limit = 10_000 }).Items;
        Assert.Equal(EventLog.MaxRows, all.Count);
        Assert.DoesNotContain(all, i => i.Id == "r0");

        _time.Advance(TimeSpan.FromDays(7) + TimeSpan.FromSeconds(30));
        Assert.DoesNotContain(log.Query(new LogQuery { Limit = 10_000 }).Items, i => i.Id is "r0" or "r1" or "r29");
    }

    [Theory]
    [InlineData("this is not json\n")]
    [InlineData("{\"tgjellyEventLog\":99}\n")]
    [InlineData("{\"tgjellyEventLog\":1}\n{\"id\":\n")]
    public async Task EventLog_CorruptFile_StartsEmpty_SendingUnaffected(string contents)
    {
        Directory.CreateDirectory(_paths.Root);
        await File.WriteAllTextAsync(_paths.EventLog, contents, TestContext.Current.CancellationToken);
        using var h = new BotTestHarness();
        using var log = new EventLog(new DataPaths(_dir.Path), h.Time, NullLogger<EventLog>.Instance);
        var rig = new LogRig(h, log);
        var d = h.AddDestination(-1001, preset: Jellyfin.Plugin.TgJelly.Configuration.PresetKind.AdminAlerts);
        await log.Loaded;

        rig.Pipeline.Queues.Publish("security.login_failed", new System.Collections.Generic.Dictionary<string, string> { ["user"] = "alice", ["user_key"] = "ALICE", ["ip"] = "1.1.1.1" });
        rig.Pipeline.Pipeline.RunOnce();
        await rig.DeliverAllAsync();

        Assert.Single(h.Api.CallsTo("sendMessage"));
        Assert.True(File.Exists(_paths.EventLog + ".corrupt"));
        Assert.Equal(contents, await File.ReadAllTextAsync(_paths.EventLog + ".corrupt", TestContext.Current.CancellationToken));
        var page = log.Query(new LogQuery());
        Assert.True(page.StartedEmpty);
        var row = Assert.Single(page.Items);
        Assert.Equal((LogResult.Sent, d.Id), (row.Result, row.DestinationId));

        log.Flush();
        using var reloaded = NewLog();
        await reloaded.Loaded;
        Assert.Single(reloaded.Query(new LogQuery()).Items);
    }

    [Fact]
    public async Task EventLog_MissingFile_StartsEmpty()
    {
        using var log = NewLog();
        await log.Loaded;

        Assert.True(log.Query(new LogQuery()).StartedEmpty);
        Assert.False(File.Exists(_paths.EventLog + ".corrupt"));
    }

    [Fact]
    public async Task EventLog_WriteNeverBlocksCaller()
    {
        using var release = new ManualResetEventSlim(false);
        using var log = NewLog();
        var writes = 0;
        log.WriteDelay = TimeSpan.Zero;
        log.WriteFile = (path, text) =>
        {
            Interlocked.Increment(ref writes);
            release.Wait(TimeSpan.FromSeconds(30));
            AtomicFile.WriteAllText(path, text);
        };
        await log.Loaded;

        log.Upsert(Row("first", Now, LogResult.Sent));
        await Eventually.TrueAsync(() => Volatile.Read(ref writes) == 1, "the writer started its first write");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
        {
            log.Upsert(Row("r" + i, Now, LogResult.Sent));
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), watch.Elapsed.ToString());
        Assert.Equal(1001, log.Query(new LogQuery { Limit = 5000 }).Items.Count);

        release.Set();
        log.Flush();
        Assert.Equal(1002, (await File.ReadAllLinesAsync(_paths.EventLog, TestContext.Current.CancellationToken)).Length);
    }

    [Fact]
    public async Task EventLog_Paging_TiedTimestamps_NoRowSkipped()
    {
        using var log = NewLog();
        await log.Loaded;
        foreach (var id in new[] { "a", "b|1", "c:2", "d", "e" })
        {
            log.Upsert(Row(id, Now, LogResult.Sent));
        }

        var seen = new System.Collections.Generic.List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = log.Query(new LogQuery { Limit = 2, Before = cursor });
            seen.AddRange(page.Items.Select(i => i.Id));
            cursor = page.Next;
            pages++;
        }
        while (cursor is not null && pages < 10);

        Assert.Equal(["e", "d", "c:2", "b|1", "a"], seen);
        Assert.Equal(3, pages);
    }

    [Fact]
    public async Task EventLog_UnexpectedReadError_StartsEmptyAndKeepsWorking()
    {
        Directory.CreateDirectory(_paths.Root);
        await File.WriteAllTextAsync(_paths.EventLog, "{\"tgjellyEventLog\":1}\n", TestContext.Current.CancellationToken);
        using var log = NewLog();
        log.ReadFile = _ => throw new InvalidOperationException("boom");
        await log.Loaded;

        log.Upsert(Row("m1", Now, LogResult.Sent));
        var page = log.Query(new LogQuery());

        Assert.True(page.StartedEmpty);
        Assert.Single(page.Items);
        log.Flush();
        Assert.Equal(2, (await File.ReadAllLinesAsync(_paths.EventLog, TestContext.Current.CancellationToken)).Length);
    }

    [Fact]
    public async Task EventLog_UnexpectedWriteError_KeepsRowsAndRetries()
    {
        using var log = NewLog();
        log.WriteDelay = TimeSpan.FromHours(1);
        log.WriteFile = (_, _) => throw new InvalidOperationException("boom");
        await log.Loaded;

        log.Upsert(Row("m1", Now, LogResult.Sent));
        log.Flush();
        Assert.False(File.Exists(_paths.EventLog));

        log.WriteFile = (path, text) => AtomicFile.WriteAllText(path, text);
        log.Flush();
        Assert.Equal(2, (await File.ReadAllLinesAsync(_paths.EventLog, TestContext.Current.CancellationToken)).Length);
    }

    public void Dispose() => _dir.Dispose();

    private static LogEntry Row(string id, DateTimeOffset at, LogResult result) => new()
    {
        Id = id,
        At = at,
        UpdatedAt = at,
        EventKey = "media.movie",
        Title = "Arrival",
        DestinationId = "d1",
        DestinationTitle = "Movies chat",
        Result = result,
    };

    private EventLog NewLog() => new(_paths, _time, NullLogger<EventLog>.Instance);
}
