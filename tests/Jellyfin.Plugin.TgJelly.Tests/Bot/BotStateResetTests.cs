using System;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Bot;

public class BotStateResetTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ForDifferentBot_ClearsBotScopedSections_AndMarksDestinationsLost()
    {
        var (state, queue) = Seeded();

        BotStateReset.ForDifferentBot(state, queue, ["d1", "d2"], "bot changed", Now);

        Assert.Empty(queue.SentMessages);
        Assert.Empty(queue.FileIdCache);
        Assert.Empty(state.Telegram.Profile.PendingCalls);
        Assert.False(state.Telegram.Profile.SetProfile);
        Assert.Single(queue.Outbox);
        Assert.Equal(999, state.Telegram.BotId);
        foreach (var id in new[] { "d1", "d2" })
        {
            var h = state.Health[id];
            Assert.Equal((HealthStatus.Lost, "bot changed", Now, false, false), (h.Status, h.LastError, h.At!.Value, h.Notified, h.PhotosBlocked));
        }
    }

    [Fact]
    public void ForDisconnect_ClearsBotScopedSections_AndKeepsTheBotIdentity()
    {
        var (state, queue) = Seeded();

        BotStateReset.ForDisconnect(state, queue);

        Assert.Empty(state.Health);
        Assert.Empty(queue.SentMessages);
        Assert.Empty(queue.Outbox);
        Assert.Empty(queue.FileIdCache);
        Assert.Empty(state.Telegram.Profile.PendingCalls);
        Assert.Equal((999L, "old_bot"), (state.Telegram.BotId!.Value, state.Telegram.BotUsername));
    }

    private static (StateDocument State, QueueDocument Queue) Seeded()
    {
        var state = new StateDocument();
        state.Telegram.BotId = 999;
        state.Telegram.BotUsername = "old_bot";
        state.Telegram.Profile = new ProfileState { SetProfile = true, PendingCalls = ["commands"] };
        state.Health["d1"] = new DestinationHealth { Status = HealthStatus.Ok, PhotosBlocked = true, Notified = true };
        var queue = new QueueDocument();
        queue.SentMessages["d1|g"] = new SentMessage { MessageId = 1 };
        queue.FileIdCache["i"] = new CachedFileId { FileId = "f" };
        queue.Outbox.Add(new OutboxEntry { EntryId = "e", DestinationId = "d1", EventKey = "media.movie" });
        return (state, queue);
    }
}
