using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Delivery;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Delivery;

public sealed class OutboxTests : IDisposable
{
    private readonly BotTestHarness _h = new();
    private readonly Outbox _outbox;
    private readonly PacingGate _pacing;

    public OutboxTests()
    {
        _outbox = new Outbox(_h.State, _h.Strings, _h.Time);
        _pacing = new PacingGate(_h.Time);
    }

    private DateTimeOffset Now => _h.Time.GetUtcNow();

    [Theory]
    [InlineData(1, 0.0, 1.6)]
    [InlineData(1, 1.0, 2.4)]
    [InlineData(3, 0.5, 8.0)]
    [InlineData(12, 0.0, 240)]
    [InlineData(12, 1.0, 360)]
    public void Backoff_DoublesFrom2s_CapsAt5min_WithJitter(int attempt, double random, double seconds)
        => Assert.Equal(seconds, Outbox.Backoff(attempt, random).TotalSeconds, precision: 3);

    [Fact]
    public void MediaCap_DropsTheOldestMedia_AndCountsIt()
    {
        var first = Entry("d1", "media.movie", 0);
        _outbox.Enqueue(first);
        for (var i = 1; i <= Outbox.MediaCap; i++)
        {
            _outbox.Enqueue(Entry("d1", "media.movie", i));
        }

        Assert.Equal(Outbox.MediaCap, _outbox.Count);
        Assert.DoesNotContain(_outbox.Snapshot(), e => e.EntryId == first.EntryId);
        Assert.Equal(1, _h.State.Read(s => s.Status.Counters.DroppedOutbox));
    }

    [Fact]
    public void AlertCap_FoldsTheRestIntoOneRollingSummary_UpdatedInPlace()
    {
        for (var i = 0; i < 60; i++)
        {
            _outbox.Enqueue(Entry("d1", "security.login_failed", i));
        }

        var entries = _outbox.Snapshot();
        Assert.Equal(Outbox.AlertCap, entries.Count(e => e.Summary == 0));
        var summary = Assert.Single(entries, e => e.Summary > 0);
        Assert.Equal(10, summary.Summary);
        Assert.Equal(_h.Strings.Plural("outbox.summary", 10), summary.Message.Text);
        Assert.Equal(0, _h.State.Read(s => s.Status.Counters.DroppedOutbox));
    }

    [Fact]
    public void Alerts_AreWrittenToQueueJsonWithinASecond()
    {
        var entry = Entry("d1", "server.task_failed", 0);
        _outbox.Enqueue(entry);

        _h.State.FlushDue();

        Assert.Contains(entry.EntryId, File.ReadAllText(_h.Paths.Queue), StringComparison.Ordinal);
    }

    [Fact]
    public void Edit_ReplacesAQueuedEditForTheSameTarget()
    {
        for (var i = 0; i < Outbox.AlertCap; i++)
        {
            _outbox.Enqueue(Entry("d1", "security.login_failed", i));
        }

        _outbox.Enqueue(Edit("d1", "security.login_failed", "g1", "first"));
        _outbox.Enqueue(Edit("d1", "security.login_failed", "g1", "second"));

        var edits = _outbox.Snapshot().Where(e => e.Op == OutboxOp.Edit).ToList();
        Assert.Equal("second", Assert.Single(edits).Message.Text);
        Assert.DoesNotContain(_outbox.Snapshot(), e => e.Summary > 0);
    }

    [Fact]
    public void Pick_AlertsGoBeforeMedia_ThenOldestFirst()
    {
        var media = Entry("d1", "media.movie", 0);
        var older = Entry("d1", "server.started", 1);
        var newer = Entry("d1", "security.lockout", 2);
        _outbox.Enqueue(media);
        _outbox.Enqueue(newer);
        _outbox.Enqueue(older);

        Assert.Equal(older.EntryId, _outbox.Pick(Views(("d1", true)), _pacing, Now).Entry!.EntryId);
    }

    [Fact]
    public void Pick_SkipsHeldDestinations()
    {
        _outbox.Enqueue(Entry("d1", "media.movie", 0));

        var pick = _outbox.Pick(Views(("d1", false)), _pacing, Now);

        Assert.Null(pick.Entry);
        Assert.Null(pick.WakeAt);
    }

    [Fact]
    public void Pick_RespectsNotBefore_AndReportsWhenToWake()
    {
        var entry = Entry("d1", "media.movie", 0);
        _outbox.Enqueue(entry);
        _outbox.Retry(entry.EntryId, Now + TimeSpan.FromSeconds(30), countAttempt: true);

        var pick = _outbox.Pick(Views(("d1", true)), _pacing, Now);

        Assert.Null(pick.Entry);
        Assert.Equal(Now + TimeSpan.FromSeconds(30), pick.WakeAt);
        Assert.Equal(1, _outbox.Snapshot().Single().Attempts);
        Assert.NotNull(_outbox.Pick(Views(("d1", true)), _pacing, Now + TimeSpan.FromSeconds(30)).Entry);
    }

    [Fact]
    public void Pick_WaitsForAnUnsentReplyTarget_ButNotForAMissingOne()
    {
        var target = Entry("d1", "security.lockout", 0);
        target.SentKey = "k";
        var reply = Entry("d1", "security.login_failed", -1); // older, but must wait for its target
        reply.ReplyToKey = "k";
        _outbox.Enqueue(reply);
        _outbox.Enqueue(target);

        Assert.Equal(target.EntryId, _outbox.Pick(Views(("d1", true)), _pacing, Now).Entry!.EntryId);

        _outbox.Complete(target, null);
        Assert.Equal(reply.EntryId, _outbox.Pick(Views(("d1", true)), _pacing, Now).Entry!.EntryId);
    }

    [Fact]
    public void Pick_RespectsPacing_AndReportsWhenToWake()
    {
        _outbox.Enqueue(Entry("d1", "media.movie", 0));
        _pacing.RecordSend(-1001, Now);

        var pick = _outbox.Pick(Views(("d1", true)), _pacing, Now);

        Assert.Null(pick.Entry);
        Assert.Equal(Now + TimeSpan.FromSeconds(1), pick.WakeAt);
    }

    [Fact]
    public void PurgeExpired_DropsEntriesOlderThan24h_AndCountsThem()
    {
        _outbox.Enqueue(Entry("d1", "media.movie", 0));
        _outbox.Enqueue(Entry("d1", "media.movie", 3600));

        Assert.Equal(1, _outbox.PurgeExpired(Now + Outbox.Ttl));
        Assert.Equal(1, _outbox.Count);
        Assert.Equal(1, _h.State.Read(s => s.Status.Counters.DroppedOutbox));
    }

    [Fact]
    public void Complete_RecordsTheSentMessage_AndEvictsBeyond5000()
    {
        var first = Entry("d1", "media.movie", 0);
        first.SentKey = "g0";
        _outbox.Enqueue(first);
        _outbox.Complete(first, Sent(1, 0));
        Assert.True(_outbox.TryGetSent("d1", "g0", out var sent));
        Assert.Equal(1, sent.MessageId);

        for (var i = 1; i <= Outbox.SentMessagesCap; i++)
        {
            var e = Entry("d1", "media.movie", i);
            e.SentKey = "g" + i;
            _outbox.Complete(e, Sent(i + 1, i));
        }

        Assert.False(_outbox.TryGetSent("d1", "g0", out _));
        Assert.Equal(1, _h.State.Read(s => s.Status.Counters.Evictions));
    }

    [Fact]
    public void FileIdCache_ExpiresAfter24h()
    {
        _outbox.CacheFileId("item|2026", "file-1", Now);

        Assert.True(_outbox.TryGetFileId("item|2026", Now + TimeSpan.FromHours(23), out var id));
        Assert.Equal("file-1", id);
        Assert.False(_outbox.TryGetFileId("item|2026", Now + Outbox.FileIdTtl, out _));
    }

    [Fact]
    public void RemoveDestination_DropsItsEntriesAndSentMessages_WithoutCounting()
    {
        var gone = Entry("d1", "media.movie", 0);
        gone.SentKey = "g";
        _outbox.Complete(gone, Sent(5, 0));
        _outbox.Enqueue(Entry("d1", "media.movie", 1));
        _outbox.Enqueue(Entry("d2", "media.movie", 2));

        _outbox.RemoveDestination("d1");

        Assert.Equal(["d2"], _outbox.Snapshot().Select(e => e.DestinationId).ToArray());
        Assert.False(_outbox.TryGetSent("d1", "g", out _));
        Assert.Equal(0, _h.State.Read(s => s.Status.Counters.DroppedOutbox));
    }

    [Fact]
    public void Repoint_MovesEntriesAndSentMessages()
    {
        var sentOne = Entry("d1", "media.movie", 0);
        sentOne.SentKey = "g";
        _outbox.Complete(sentOne, Sent(5, 0));
        _outbox.Enqueue(Entry("d1", "media.movie", 1));

        _outbox.Repoint("d1", "d2");

        Assert.Equal("d2", _outbox.Snapshot().Single().DestinationId);
        Assert.True(_outbox.TryGetSent("d2", "g", out _));
    }

    public void Dispose() => _h.Dispose();

    private static Dictionary<string, DestinationView> Views(params (string Id, bool Deliverable)[] views)
        => views.ToDictionary(v => v.Id, v => new DestinationView(-1001, ChatKind.Supergroup, v.Deliverable));

    private OutboxEntry Entry(string destinationId, string eventKey, int secondsAfterNow) => new()
    {
        EntryId = Guid.NewGuid().ToString("N"),
        DestinationId = destinationId,
        EventKey = eventKey,
        Op = OutboxOp.Send,
        Message = new OutboxMessage { Text = eventKey, Caption = eventKey },
        QueuedAt = Now + TimeSpan.FromSeconds(secondsAfterNow),
    };

    private OutboxEntry Edit(string destinationId, string eventKey, string targetKey, string text)
    {
        var e = Entry(destinationId, eventKey, 0);
        e.Op = OutboxOp.Edit;
        e.TargetKey = targetKey;
        e.Message.Text = text;
        return e;
    }

    private SentMessage Sent(int messageId, int secondsAfterNow)
        => new() { MessageId = messageId, Kind = SentKind.Text, SentAt = Now + TimeSpan.FromSeconds(secondsAfterNow), EventKey = "media.movie" };

    [Fact]
    public void Pick_HoldsAnEditWhileItsTargetIsStillQueued()
    {
        var send = Entry("d1", "media.episodes", 0);
        send.SentKey = "g1";
        _outbox.Enqueue(send);
        _outbox.Retry(send.EntryId, Now + TimeSpan.FromSeconds(2), countAttempt: true);
        _outbox.Enqueue(Edit("d1", "media.episodes", "g1", "updated"));

        var pick = _outbox.Pick(Views(("d1", true)), _pacing, Now);

        Assert.Null(pick.Entry);
        Assert.Equal(send.EntryId, _outbox.Pick(Views(("d1", true)), _pacing, Now + TimeSpan.FromSeconds(2)).Entry!.EntryId);
    }
}
