using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Telegram.Bot.Types;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Destinations;

public sealed class TopicNamesTests : System.IDisposable
{
    private readonly BotTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private KnownTopic[] Known => _h.Settings.Current.Destinations.Single().KnownTopics;

    [Fact]
    public void ASentMessage_NamesOnlyATopicTgJellyCantName()
    {
        var d = _h.AddDestination(-1001);

        // A send returns the name the topic was created with, so it must not undo a rename.
        static Message Sent(string name) => new() { MessageThreadId = 7, ReplyToMessage = new Message { ForumTopicCreated = new ForumTopicCreated { Name = name } } };

        TopicNames.LearnFrom(_h.Settings, d.Id, Sent("Jellyfin"));
        var revision = _h.Settings.Current.Revision;
        TopicNames.LearnFrom(_h.Settings, d.Id, Sent("Old name"));
        TopicNames.LearnFrom(_h.Settings, d.Id, new Message { MessageThreadId = 7 });

        Assert.Equal((7, "Jellyfin"), (Assert.Single(Known).ThreadId, Known[0].Name));
        Assert.Equal(revision, _h.Settings.Current.Revision);
    }

    [Fact]
    public void ARename_IsFollowed_ForTopicsInUseAndGeneral()
    {
        _h.AddDestination(-1001, threadId: 7);
        _h.Settings.UpdateIf(null, c =>
        {
            c.Destinations[0].TopicRoutes = [new TopicRoute { Slot = "security", ThreadId = 8 }];
            return true;
        });
        static Message Renamed(int? thread, string? name) => new()
        {
            Chat = new Chat { Id = -1001 },
            MessageThreadId = thread,
            IsTopicMessage = thread is not null,
            ForumTopicEdited = new ForumTopicEdited { Name = name, IconCustomEmojiId = name is null ? "1" : null },
        };

        // 9 is a topic no destination uses; null is General; a new icon alone changes no name.
        foreach (var m in new[] { Renamed(7, "Films"), Renamed(8, "Alerts"), Renamed(9, "Chat"), Renamed(null, "Lobby"), Renamed(7, null) })
        {
            TopicNames.FollowRename(_h.Settings, m);
        }

        Assert.Equal([(7, "Films"), (8, "Alerts"), (0, "Lobby")], Known.Select(t => (t.ThreadId, t.Name)));
    }
}
