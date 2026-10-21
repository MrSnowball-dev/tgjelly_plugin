using System;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Bot;

public sealed class BotMemoryTests : IDisposable
{
    private readonly BotTestHarness _h = new();
    private readonly BotMemory _memory;

    public BotMemoryTests() => _memory = new BotMemory(_h.State, _h.Time);

    public void Dispose() => _h.Dispose();

    [Fact]
    public void Flow_RoundTrips_AndExpiresAfter24h()
    {
        var flow = new FlowState { Screen = "add", WizardMessageId = 5 };
        flow.Params["kind"] = "channel";
        _memory.SaveFlow(42, flow);

        var read = _memory.GetFlow(42)!;
        Assert.Equal(("add", 5, "channel"), (read.Screen, read.WizardMessageId, read.Params["kind"]));
        Assert.Equal(_h.Time.GetUtcNow(), read.UpdatedAt);
        read.Screen = "changed";
        Assert.Equal("add", _memory.GetFlow(42)!.Screen);

        _h.Time.Advance(BotMemory.FlowTtl - TimeSpan.FromSeconds(1));
        Assert.NotNull(_memory.GetFlow(42));
        _h.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(_memory.GetFlow(42));
        _memory.Prune();
        Assert.Empty(_h.State.Read(s => s.Flows));

        _memory.SaveFlow(42, flow);
        _memory.ClearFlow(42);
        Assert.Null(_memory.GetFlow(42));
    }

    [Fact]
    public void MigrateChat_MovesDetectedAndFlows_MergingKeepsTheEarliest()
    {
        _memory.RecordDetected(-1001, "Old", ChatKind.Group);
        _h.Time.Advance(TimeSpan.FromMinutes(1));
        _memory.RecordDetected(-1009, "New", ChatKind.Supergroup);
        var flow = new FlowState { Screen = "where" };
        flow.Params["chatId"] = "-1001";
        _memory.SaveFlow(1, flow);

        _memory.MigrateChat(-1001, -1009);

        var detected = Assert.Single(_h.State.Read(s => s.DetectedChats));
        Assert.Equal((-1009L, "New", ChatKind.Supergroup), (detected.ChatId, detected.Title, detected.Kind));
        Assert.Equal(_h.Time.GetUtcNow() - TimeSpan.FromMinutes(1), detected.DetectedAt);
        Assert.Equal("-1009", _memory.GetFlow(1)!.Params["chatId"]);

        _memory.MigrateChat(-5, -6);
        Assert.Single(_h.State.Read(s => s.DetectedChats));
    }

    [Fact]
    public void Flows_Cap200_EvictsLeastRecentlyUpdated_AndCounts()
    {
        for (var i = 1; i <= BotMemory.FlowsCap; i++)
        {
            _memory.SaveFlow(i, new FlowState { Screen = "s" });
            _h.Time.Advance(TimeSpan.FromSeconds(1));
        }

        _memory.SaveFlow(1, new FlowState { Screen = "again" });
        _memory.SaveFlow(1000, new FlowState { Screen = "new" });

        Assert.Equal(BotMemory.FlowsCap, _h.State.Read(s => s.Flows.Count));
        Assert.Null(_memory.GetFlow(2));
        Assert.NotNull(_memory.GetFlow(1));
        Assert.NotNull(_memory.GetFlow(1000));
        Assert.Equal(1, _h.State.Read(s => s.Status.Counters.Evictions));
    }

    [Fact]
    public void Managers_90Days_Cap50()
    {
        _memory.TouchManager(1);
        _h.Time.Advance(TimeSpan.FromDays(1));
        _memory.TouchManager(2);
        Assert.Equal([2L, 1L], _memory.RecentManagers());

        _h.Time.Advance(BotMemory.ManagerTtl - TimeSpan.FromDays(1));
        Assert.Equal([2L], _memory.RecentManagers());
        _memory.Prune();
        Assert.Single(_h.State.Read(s => s.Managers));

        _memory.RemoveManager(2);
        Assert.Empty(_memory.RecentManagers());

        for (var i = 1; i <= BotMemory.ManagersCap + 2; i++)
        {
            _memory.TouchManager(i);
            _h.Time.Advance(TimeSpan.FromSeconds(1));
        }

        var recent = _memory.RecentManagers();
        Assert.Equal(BotMemory.ManagersCap, recent.Count);
        Assert.DoesNotContain(1L, recent);
        Assert.DoesNotContain(2L, recent);
        Assert.Equal(2, _h.State.Read(s => s.Status.Counters.Evictions));
    }

    [Fact]
    public void Detected_HiddenFor30s_AndNeverForADestination()
    {
        _memory.RecordDetected(-100, "A", ChatKind.Supergroup);
        _memory.RecordDetected(-200, "B", ChatKind.Channel);
        Assert.Empty(_memory.VisibleDetected([]));

        _h.Time.Advance(BotMemory.DetectedGrace);
        Assert.Equal([-100L, -200L], _memory.VisibleDetected([]).Select(d => d.ChatId));
        Assert.Equal([-200L], _memory.VisibleDetected([-100L]).Select(d => d.ChatId));

        _memory.RemoveDetected(-100);
        Assert.Equal([-200L], _memory.VisibleDetected([]).Select(d => d.ChatId));

        for (var i = 1; i <= BotMemory.DetectedCap; i++)
        {
            _memory.RecordDetected(i, "c", ChatKind.Group);
            _h.Time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(BotMemory.DetectedCap, _h.State.Read(s => s.DetectedChats.Count));
        Assert.DoesNotContain(-200L, _h.State.Read(s => s.DetectedChats.Select(d => d.ChatId).ToList()));
        Assert.Equal(1, _h.State.Read(s => s.Status.Counters.Evictions));
    }

    [Fact]
    public void Detected_Upsert_KeepsFirstSeen()
    {
        var first = _h.Time.GetUtcNow();
        _memory.RecordDetected(-100, "Old", ChatKind.Group);
        _h.Time.Advance(TimeSpan.FromMinutes(5));
        _memory.RecordDetected(-100, "New", ChatKind.Supergroup);

        var d = Assert.Single(_memory.VisibleDetected([]));
        Assert.Equal(("New", ChatKind.Supergroup, first), (d.Title, d.Kind, d.DetectedAt));
    }

    [Fact]
    public void Failures_7Days()
    {
        Assert.False(_memory.HasRecentFailure());
        _memory.RecordFailure(SetupFailureKind.CheckFailed);
        Assert.True(_memory.HasRecentFailure());

        _h.Time.Advance(BotMemory.FailureTtl - TimeSpan.FromSeconds(1));
        Assert.True(_memory.HasRecentFailure());
        _h.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(_memory.HasRecentFailure());
        _memory.Prune();
        Assert.Empty(_h.State.Read(s => s.SetupFailures));

        for (var i = 0; i < BotMemory.FailuresCap + 3; i++)
        {
            _memory.RecordFailure(SetupFailureKind.TopicFailed);
        }

        Assert.Equal(BotMemory.FailuresCap, _h.State.Read(s => s.SetupFailures.Count));
        Assert.Equal(3, _h.State.Read(s => s.Status.Counters.Evictions));
    }

    [Fact]
    public void CorruptKeys_DoNotStopPruneOrRecentManagers()
    {
        var now = _h.Time.GetUtcNow();
        _memory.TouchManager(5);
        _h.State.Update(s =>
        {
            s.Managers["not-a-number"] = now;
            s.Flows["bad"] = new FlowState { Screen = "add", PromptSentAt = now.AddHours(-1) };
        });

        Assert.Equal([5L], _memory.RecentManagers());
        Assert.Empty(_memory.Prune());
    }

    [Fact]
    public void Prune_ExpiresPromptsAfter10Min_AndRecordsAFailure_Once()
    {
        _memory.SaveFlow(7, new FlowState { Screen = "add", PromptSentAt = _h.Time.GetUtcNow(), WizardMessageId = 4 });
        _memory.SaveFlow(8, new FlowState { Screen = "home" });

        _h.Time.Advance(BotMemory.PromptTtl - TimeSpan.FromSeconds(1));
        Assert.Empty(_memory.Prune());
        Assert.False(_memory.HasRecentFailure());

        _h.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal([7L], _memory.Prune());
        var flow = _memory.GetFlow(7)!;
        Assert.Equal((null, 4), (flow.PromptSentAt, flow.WizardMessageId));
        Assert.Equal([SetupFailureKind.PromptExpired], _h.State.Read(s => s.SetupFailures.Select(f => f.Kind).ToList()));

        Assert.Empty(_memory.Prune());
        Assert.Single(_h.State.Read(s => s.SetupFailures));
    }
}
