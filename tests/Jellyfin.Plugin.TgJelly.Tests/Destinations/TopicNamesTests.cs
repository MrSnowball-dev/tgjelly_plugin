using System.Linq;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Telegram.Bot.Types;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Destinations;

public sealed class TopicNamesTests : System.IDisposable
{
    private readonly BotTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public void ASentMessage_NamesItsTopic_OnceAndOnlyWhenNew()
    {
        var d = _h.AddDestination(-1001);
        static Message Sent(string name) => new() { MessageThreadId = 7, ReplyToMessage = new Message { ForumTopicCreated = new ForumTopicCreated { Name = name } } };

        TopicNames.LearnFrom(_h.Settings, d.Id, Sent("Jellyfin"));
        var revision = _h.Settings.Current.Revision;
        TopicNames.LearnFrom(_h.Settings, d.Id, Sent("Jellyfin"));
        TopicNames.LearnFrom(_h.Settings, d.Id, new Message { MessageThreadId = 7 });

        var known = Assert.Single(_h.Settings.Current.Destinations.Single().KnownTopics);
        Assert.Equal((7, "Jellyfin"), (known.ThreadId, known.Name));
        Assert.Equal(revision, _h.Settings.Current.Revision);

        TopicNames.LearnFrom(_h.Settings, d.Id, Sent("Movies & shows"));
        Assert.Equal("Movies & shows", Assert.Single(_h.Settings.Current.Destinations.Single().KnownTopics).Name);
    }
}
