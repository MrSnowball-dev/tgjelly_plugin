using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Types;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Fakes;

public class TelegramFixturesTests
{
    [Theory]
    [InlineData("private_text")]
    [InlineData("private_command_start")]
    [InlineData("chat_shared")]
    [InlineData("callback_query")]
    [InlineData("my_chat_member_channel_admin")]
    [InlineData("my_chat_member_channel_rights")]
    [InlineData("my_chat_member_group_member")]
    [InlineData("my_chat_member_kicked")]
    [InlineData("my_chat_member_private_blocked")]
    [InlineData("my_chat_member_private_unblocked")]
    [InlineData("group_text")]
    [InlineData("group_command_start")]
    [InlineData("group_migrate_to")]
    [InlineData("group_migrate_from")]
    public void UpdateFixtures_Parse(string name)
        => Assert.True(TelegramFixtures.Update(name).Id > 0);

    [Fact]
    public void BotApiResultFixtures_Parse()
    {
        Assert.True(JsonSerializer.Deserialize<ChatFullInfo>(TelegramFixtures.Json("get_chat_forum"), JsonBotAPI.Options)!.IsForum);
        Assert.False(JsonSerializer.Deserialize<ChatFullInfo>(TelegramFixtures.Json("get_chat_group_no_photos"), JsonBotAPI.Options)!.Permissions!.CanSendPhotos);
        Assert.IsType<ChatMemberRestricted>(JsonSerializer.Deserialize<ChatMember>(TelegramFixtures.Json("get_chat_member_restricted_no_photos"), JsonBotAPI.Options));
        Assert.True(((ChatMemberAdministrator)JsonSerializer.Deserialize<ChatMember>(TelegramFixtures.Json("get_chat_member_admin_topics"), JsonBotAPI.Options)!).CanManageTopics);
        Assert.False(((ChatMemberAdministrator)JsonSerializer.Deserialize<ChatMember>(TelegramFixtures.Json("get_chat_member_admin_no_topics"), JsonBotAPI.Options)!).CanManageTopics);
        Assert.NotNull(JsonSerializer.Deserialize<Message>(TelegramFixtures.Json("forum_topic_created", ("text", "News")), JsonBotAPI.Options)!.ForumTopicCreated);
    }
}
