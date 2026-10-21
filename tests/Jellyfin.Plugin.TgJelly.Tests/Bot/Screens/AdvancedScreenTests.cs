using System;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Bot.Screens;

public sealed class AdvancedScreenTests : IDisposable, IAsyncLifetime
{
    private const long Manager = 42;
    private const long Group = -1005550001;
    private readonly BotTestHarness _h = new();
    private BotRig _rig = null!;

    public async ValueTask InitializeAsync() => _rig = await BotRig.CreateAsync(_h);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Options_HasTopicAndDashboardOnly()
    {
        await Click("1|ox", 77);

        var edit = Assert.Single(_h.Api.CallsTo("editMessageText"));
        Assert.Equal(77, (int)edit.Body["message_id"]!);
        var buttons = Buttons(edit);
        Assert.Equal(["Use an existing topic", "Open dashboard"], buttons.Select(b => (string)b["text"]!));
        Assert.Equal(["1|ol", "1|od"], buttons.Select(b => (string)b["callback_data"]!));
    }

    [Fact]
    public async Task OpenDashboard_ShowsThePath()
    {
        await Click("1|ox", 77);
        var data = (string)Buttons(_h.Api.CallsTo("editMessageText").Last())[1]["callback_data"]!;

        await Click(data, 77);

        Assert.Contains("Dashboard → Plugins → Telegram → Advanced → Add chat by ID", (string)_h.Api.CallsTo("editMessageText").Last().Body["text"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MainMenu_NeverLinksAdvanced()
    {
        _h.AddDestination(-1001);

        await Text("/menu");

        var data = Buttons(Assert.Single(_h.Api.CallsTo("sendMessage"))).Select(b => (string?)b["callback_data"]);
        Assert.DoesNotContain("1|ox", data);
    }

    [Fact]
    public async Task ExistingTopic_WithAPickedChat_RequiresTheLinkToNameThatChat()
    {
        AddForum();
        _h.Api.On("sendChatAction", _ => FakeBotApi.Error(400, "Bad Request: message thread not found"));
        await Text("/start add");
        await _rig.Deliver(TelegramFixtures.Update("my_chat_member_group_member", ("chat_id", Group.ToString(CultureInfo.InvariantCulture))));
        var wizard = _rig.Memory.GetFlow(Manager)!.WizardMessageId!.Value;
        await Click("1|te", wizard);
        await Text("https://t.me/c/5550001/9"); // fails: the topic is gone
        _h.Api.On("sendChatAction", _ => FakeBotApi.Ok("true"));
        await Click("1|ox", wizard);
        var existing = (string)Buttons(_h.Api.CallsTo("editMessageText").Last())[0]["callback_data"]!;

        await Click(existing, wizard);
        await Text("https://t.me/c/999/9");

        Assert.Contains("different chat", (string)Sent().Last().Body["text"]!, StringComparison.Ordinal);
        Assert.Equal("topicLink", _rig.Memory.GetFlow(Manager)!.Params["input"]);

        await Text("https://t.me/c/5550001/9");

        Assert.Equal("9", _rig.Memory.GetFlow(Manager)!.Params["threadId"]);
        Assert.Contains("What should I send there?", (string)_h.Api.CallsTo("editMessageText").Last().Body["text"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExistingTopic_WithNoPickedChat_ChecksTheLinksChatDirectly()
    {
        AddForum();
        await Click("1|ox", 77);
        var existing = (string)Buttons(_h.Api.CallsTo("editMessageText").Last())[0]["callback_data"]!;

        await Click(existing, 77);

        var prompt = _h.Api.CallsTo("editMessageText").Last();
        Assert.Contains("Copy link", (string)prompt.Body["text"]!, StringComparison.Ordinal);
        Assert.Contains(SetupFailureKind.TopicNotPickable, _h.State.Read(s => s.SetupFailures.Select(f => f.Kind).ToList()));

        await Text("https://t.me/c/5550001/9");

        Assert.Equal(9, (int)Assert.Single(_h.Api.CallsTo("sendChatAction")).Body["message_thread_id"]!);
        var flow = _rig.Memory.GetFlow(Manager)!;
        Assert.Equal(("9", Group.ToString(CultureInfo.InvariantCulture)), (flow.Params["threadId"], flow.Params["chatId"]));
        Assert.Contains("What should I send there?", (string)_h.Api.CallsTo("editMessageText").Last().Body["text"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdvancedTopic_AsksInPlace_WithCancelBackToAdvanced()
    {
        AddForum();

        await Click("1|ol", 9);

        var edit = Assert.Single(_h.Api.CallsTo("editMessageText"));
        Assert.Equal(9, (int)edit.Body["message_id"]!);
        Assert.Contains("Send me the topic's link as a message.", (string)edit.Body["text"]!, StringComparison.Ordinal);
        Assert.Equal(["1|ox"], Buttons(edit).Select(b => (string)b["callback_data"]!));
        Assert.Empty(_h.Api.CallsTo("sendMessage"));
    }

    [Fact]
    public async Task ExistingTopic_WithNoPickedChat_AlreadyADestination_SaysSo()
    {
        _h.AddDestination(Group, threadId: 9);
        AddForum(); // after the destination, which registers a plain supergroup
        await Click("1|ox", 77);
        await Click((string)Buttons(_h.Api.CallsTo("editMessageText").Last())[0]["callback_data"]!, 77);

        await Text("https://t.me/c/5550001/9");

        Assert.Contains("is already set up", (string)_h.Api.CallsTo("editMessageText").Last().Body["text"]!, StringComparison.Ordinal);
        Assert.Null(_rig.Memory.GetFlow(Manager));
    }

    [Fact]
    public async Task ExistingTopic_WithNoPickedChat_FailedCheck_OffersAdvancedAgain()
    {
        AddForum();
        _h.Api.On("sendChatAction", _ => FakeBotApi.Error(400, "Bad Request: message thread not found"));
        await Click("1|ox", 77);
        await Click((string)Buttons(_h.Api.CallsTo("editMessageText").Last())[0]["callback_data"]!, 77);

        await Text("https://t.me/c/5550001/9");

        Assert.Equal(["1|ox"], Buttons(_h.Api.CallsTo("editMessageText").Last()).Select(b => (string)b["callback_data"]!));
        Assert.Contains(SetupFailureKind.TopicFailed, _h.State.Read(s => s.SetupFailures.Select(f => f.Kind).ToList()));
    }

    private void AddForum()
    {
        _h.Api.AddChat(Group, "supergroup", "Family", isForum: true);
        _h.Api.SetMember(Group, TelegramFixtures.Json("get_chat_member_admin_topics"));
    }

    private Task Click(string data, int messageId)
        => _rig.Deliver(TelegramFixtures.Update("callback_query", ("data", data), ("message_id", messageId.ToString(CultureInfo.InvariantCulture))));

    private Task Text(string text)
        => _rig.Deliver(TelegramFixtures.Update("private_text", ("text", System.Text.Json.JsonEncodedText.Encode(text).ToString())));

    private BotApiCall[] Sent()
        => [.. _h.Api.CallsTo("sendMessage").Where(c => c.Body["chat_id"]!.ToString() == Manager.ToString(CultureInfo.InvariantCulture))];

    private static JsonNode[] Buttons(BotApiCall call)
        => [.. call.Body["reply_markup"]!["inline_keyboard"]!.AsArray().SelectMany(r => r!.AsArray()).Select(b => b!)];
}
