using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Telegram.Bot.Exceptions;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Destinations;

public sealed class DestinationCheckerTests : IDisposable
{
    private const string Member = """{"status":"member","user":{"id":123456789,"is_bot":true,"first_name":"b"}}""";
    private const string RestrictedNoPhotos = """{"status":"restricted","user":{"id":123456789,"is_bot":true,"first_name":"b"},"is_member":true,"can_send_messages":true,"can_send_audios":true,"can_send_documents":true,"can_send_photos":false,"can_send_videos":true,"can_send_video_notes":true,"can_send_voice_notes":true,"can_send_polls":true,"can_send_other_messages":true,"can_add_web_page_previews":true,"can_change_info":false,"can_invite_users":false,"can_pin_messages":false,"can_manage_topics":false,"until_date":0}""";
    private const string Left = """{"status":"left","user":{"id":123456789,"is_bot":true,"first_name":"b"}}""";

    private readonly BotTestHarness _h = new();
    private readonly DestinationChecker _checker;

    public DestinationCheckerTests()
    {
        _h.Connect();
        _checker = new DestinationChecker(_h.Connection, _h.Strings);
    }

    [Fact]
    public async Task Channel_AdminWhoCanPost_Passes()
    {
        _h.Api.AddChat(-1001, "channel", "News");
        _h.Api.SetMember(-1001, BotTestHarness.AdminCanPost);

        var result = await _checker.CheckAsync(-1001, 0, TestContext.Current.CancellationToken);

        Assert.True(result.Passed);
        Assert.Equal((ChatKind.Channel, "News", -1001L), (result.Kind, result.Title, result.ChatId));
    }

    [Fact]
    public async Task Channel_PlainMember_NeedsPermissions()
    {
        _h.Api.AddChat(-1001, "channel", "News");
        _h.Api.SetMember(-1001, Member);

        var result = await _checker.CheckAsync(-1001, 0, TestContext.Current.CancellationToken);

        Assert.False(result.Passed);
        Assert.Equal(HealthStatus.NeedsPermissions, result.Problem);
        Assert.Equal(_h.Strings.Get("check.channelNotAdmin"), result.Message);
    }

    [Fact]
    public async Task Group_Member_Passes()
    {
        _h.Api.AddChat(-1002, "supergroup", "Fans");
        _h.Api.SetMember(-1002, Member);

        var result = await _checker.CheckAsync(-1002, 0, TestContext.Current.CancellationToken);

        Assert.True(result.Passed);
        Assert.False(result.PhotosBlocked);
        Assert.Equal(ChatKind.Supergroup, result.Kind);
    }

    [Fact]
    public async Task Group_RestrictedWithoutPhotos_PassesWithPhotosBlocked()
    {
        _h.Api.AddChat(-1002, "supergroup", "Fans");
        _h.Api.SetMember(-1002, RestrictedNoPhotos);

        var result = await _checker.CheckAsync(-1002, 0, TestContext.Current.CancellationToken);

        Assert.True(result.Passed);
        Assert.True(result.PhotosBlocked);
    }

    [Fact]
    public async Task Group_MemberWhenMembersCantSendPhotos_PassesWithPhotosBlocked()
    {
        _h.Api.AddChat(-1002, "group", "Family", permissionsJson: """{"can_send_messages":true,"can_send_photos":false}""");
        _h.Api.SetMember(-1002, Member);

        var result = await _checker.CheckAsync(-1002, 0, TestContext.Current.CancellationToken);

        Assert.True(result.Passed);
        Assert.True(result.PhotosBlocked);
        Assert.Equal(ChatKind.Group, result.Kind);
    }

    [Fact]
    public async Task Group_BotLeft_IsLost()
    {
        _h.Api.AddChat(-1002, "supergroup", "Fans");
        _h.Api.SetMember(-1002, Left);

        var result = await _checker.CheckAsync(-1002, 0, TestContext.Current.CancellationToken);

        Assert.False(result.Passed);
        Assert.Equal(HealthStatus.Lost, result.Problem);
        Assert.Equal(_h.Strings.Get("check.lost"), result.Message);
    }

    [Fact]
    public async Task UnknownChat_IsLost()
    {
        var result = await _checker.CheckAsync(-1999, 0, TestContext.Current.CancellationToken);

        Assert.False(result.Passed);
        Assert.Equal(HealthStatus.Lost, result.Problem);
        Assert.Equal(_h.Strings.Get("check.notFound"), result.Message);
    }

    [Fact]
    public async Task PrivateChat_Passes_WithoutAMemberCheck()
    {
        _h.Api.AddChat(42, "private", "Ann");

        var result = await _checker.CheckAsync(42, 0, TestContext.Current.CancellationToken);

        Assert.True(result.Passed);
        Assert.Equal((ChatKind.Private, "Ann"), (result.Kind, result.Title));
        Assert.Empty(_h.Api.CallsTo("getChatMember"));
    }

    [Fact]
    public async Task Topic_ThatNoLongerExists_IsTopicMissing()
    {
        _h.Api.AddChat(-1002, "supergroup", "Forum", isForum: true);
        _h.Api.SetMember(-1002, Member);
        _h.Api.On("sendChatAction", _ => FakeBotApi.Error(400, "Bad Request: message thread not found"));

        var result = await _checker.CheckAsync(-1002, 7, TestContext.Current.CancellationToken);

        Assert.False(result.Passed);
        Assert.Equal(HealthStatus.TopicMissing, result.Problem);
        Assert.True(result.IsForum);
    }

    [Fact]
    public async Task Topic_ProbeIsATypingAction_NotAMessage()
    {
        _h.Api.AddChat(-1002, "supergroup", "Forum", isForum: true);
        _h.Api.SetMember(-1002, Member);

        var result = await _checker.CheckAsync(-1002, 7, TestContext.Current.CancellationToken);

        Assert.True(result.Passed);
        var probe = Assert.Single(_h.Api.CallsTo("sendChatAction")).Body;
        Assert.Equal(("typing", 7), ((string)probe["action"]!, (int)probe["message_thread_id"]!));
        Assert.Empty(_h.Api.CallsTo("sendMessage"));
    }

    [Fact]
    public async Task UsernameChat_ResolvesToTheNumericId()
    {
        _h.Api.AddChat(-1003, "supergroup", "Public", username: "jfpublic");
        _h.Api.SetMember(-1003, Member);

        var result = await _checker.CheckAsync("@jfpublic", 0, TestContext.Current.CancellationToken);

        Assert.True(result.Passed);
        Assert.Equal(-1003, result.ChatId);
    }

    [Fact]
    public async Task TransportError_Throws_SoTheOldHealthIsKept()
    {
        _h.Api.On("getChat", _ => throw new HttpRequestException("network down"));

        await Assert.ThrowsAsync<RequestException>(() => _checker.CheckAsync(-1001, 0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NotConnected_ThrowsBotNotConnected()
    {
        var checker = new DestinationChecker(new BotConnection(), _h.Strings);

        await Assert.ThrowsAsync<BotNotConnectedException>(() => checker.CheckAsync(-1001, 0, TestContext.Current.CancellationToken));
    }

    public void Dispose() => _h.Dispose();
}
