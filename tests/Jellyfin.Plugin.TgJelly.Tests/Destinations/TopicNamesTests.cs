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
    public void ARenameOrNewTopic_IsFollowed_GeneralIncluded()
    {
        _h.AddDestination(-1001, threadId: 7);
        static Message InForum(int? thread) => new() { Chat = new Chat { Id = -1001 }, MessageThreadId = thread, IsTopicMessage = thread is not null };

        var renamed = InForum(7);
        renamed.ForumTopicEdited = new ForumTopicEdited { Name = "Films" };
        var created = InForum(9);
        created.ForumTopicCreated = new ForumTopicCreated { Name = "Music" };
        var general = InForum(null);
        general.ForumTopicEdited = new ForumTopicEdited { Name = "Lobby" };
        var iconOnly = InForum(7);
        iconOnly.ForumTopicEdited = new ForumTopicEdited { IconCustomEmojiId = "1" };

        foreach (var m in new[] { renamed, created, general, iconOnly })
        {
            TopicNames.FollowRename(_h.Settings, m);
        }

        Assert.Equal([(7, "Films"), (9, "Music"), (0, "Lobby")], Known.Select(t => (t.ThreadId, t.Name)));
    }
}
