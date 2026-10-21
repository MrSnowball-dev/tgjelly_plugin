using System;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot.Flows;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Bot.Flows;

public sealed class TopicFlowTests : IDisposable, IAsyncLifetime
{
    private const long Manager = 42;
    private const long Group = -1005550001;
    private const long Other = -1009990001;
    private readonly BotTestHarness _h = new();
    private BotRig _rig = null!;

    public async ValueTask InitializeAsync() => _rig = await BotRig.CreateAsync(_h);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Forum_AsksWhereToPost()
    {
        AddForum();
        await Text("/start add");
        var promptId = _rig.Memory.GetFlow(Manager)!.WizardMessageId!.Value;

        await Added();

        var wizard = _h.Api.CallsTo("editMessageText").Last();
        Assert.Equal(promptId, (int)wizard.Body["message_id"]!);
        Assert.Contains("This group has topics. Where should I post?", (string)wizard.Body["text"]!, StringComparison.Ordinal);
        Assert.Equal(["General topic", "Create a \"Jellyfin\" topic", "Use an existing topic…"], Labels(wizard));
        Assert.Equal(["1|tg", "1|tc", "1|te"], Data(wizard));
        Assert.Empty(Deleted());
        Assert.Single(Sent());
        var flow = _rig.Memory.GetFlow(Manager)!;
        Assert.Equal(promptId, flow.WizardMessageId);
        Assert.Equal("1", flow.Params["isForum"]);
        Assert.Equal(Group.ToString(CultureInfo.InvariantCulture), flow.Params["chatId"]);
    }

    [Fact]
    public async Task General_UsesThreadZero_AndAppliesTheDuplicateRule()
    {
        _h.AddDestination(Group, threadId: 5); // another topic of the same chat is not a duplicate of General
        AddForum();
        await AskWhere();
        var wizard = WizardId();

        await Click("1|tg", wizard);

        var edit = _h.Api.CallsTo("editMessageText").Last();
        Assert.Equal(wizard, (int)edit.Body["message_id"]!);
        Assert.Contains("What should I send there?", (string)edit.Body["text"]!, StringComparison.Ordinal);
        var flow = _rig.Memory.GetFlow(Manager)!;
        Assert.Equal("0", flow.Params["threadId"]);
        Assert.Equal(wizard, flow.WizardMessageId);
    }

    [Fact]
    public async Task General_WhenAlreadyADestination_SaysSo()
    {
        _h.AddDestination(Group);
        AddForum();
        await AskWhere();

        await Click("1|tg", WizardId());

        Assert.Contains("is already set up", (string)_h.Api.CallsTo("editMessageText").Last().Body["text"]!, StringComparison.Ordinal);
        Assert.Null(_rig.Memory.GetFlow(Manager));
    }

    [Fact]
    public async Task Create_WithTopicRights_CreatesAndUsesTheThread()
    {
        AddForum();
        await AskWhere();
        var wizard = WizardId();

        await Click("1|tc", wizard);

        var create = Assert.Single(_h.Api.CallsTo("createForumTopic")).Body;
        Assert.Equal(("Jellyfin", Group), ((string)create["name"]!, (long)create["chat_id"]!));
        var flow = _rig.Memory.GetFlow(Manager)!;
        Assert.Equal("101", flow.Params["threadId"]);
        Assert.Equal(wizard, flow.WizardMessageId);
        Assert.Contains("What should I send there?", (string)_h.Api.CallsTo("editMessageText").Last().Body["text"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoubleCreateTopicClick_CreatesOnce()
    {
        AddForum();
        await AskWhere();
        var wizard = WizardId();

        await Click("1|tc", wizard);
        await Click("1|tc", wizard); // the same button redelivered

        Assert.Single(_h.Api.CallsTo("createForumTopic"));
        Assert.Equal("101", _rig.Memory.GetFlow(Manager)!.Params["threadId"]);
        Assert.Empty(_h.Settings.Current.Destinations);
    }

    [Fact]
    public async Task Create_WithoutRights_EditsTheWizardToThePromotionLinks()
    {
        AddForum(topics: false);
        await AskWhere();
        var wizard = WizardId();
        var sent = Sent().Length;

        await Click("1|tc", wizard);

        Assert.Empty(_h.Api.CallsTo("createForumTopic"));
        var edit = _h.Api.CallsTo("editMessageText").Last();
        Assert.Equal(wizard, (int)edit.Body["message_id"]!);
        AssertPromotionScreen(edit);
        Assert.Contains("I need permission to manage topics in <b>Family</b>.", (string)edit.Body["text"]!, StringComparison.Ordinal);
        Assert.Equal(sent, Sent().Length);
        Assert.True(TopicFlow.AwaitsPromotion(_rig.Memory.GetFlow(Manager)!));
    }

    [Fact]
    public async Task Promotion_InTheFlowsForum_CreatesTheTopic()
    {
        AddForum(topics: false);
        await AskWhere();
        var wizard = WizardId();
        await Click("1|tc", wizard);
        var sent = Sent().Length;

        _h.Api.SetMember(Group, TopicRights());
        await Promoted(Group);

        Assert.Equal("Jellyfin", (string)Assert.Single(_h.Api.CallsTo("createForumTopic")).Body["name"]!);
        Assert.Contains(_h.Api.CallsTo("editMessageText"), c => (int)c.Body["message_id"]! == wizard && ((string)c.Body["text"]!).StartsWith("Checking <b>Family</b>", StringComparison.Ordinal));
        var edit = _h.Api.CallsTo("editMessageText").Last();
        Assert.Equal(wizard, (int)edit.Body["message_id"]!);
        Assert.Contains("What should I send there?", (string)edit.Body["text"]!, StringComparison.Ordinal);
        var after = _rig.Memory.GetFlow(Manager)!;
        Assert.Equal("101", after.Params["threadId"]);
        Assert.Equal(wizard, after.WizardMessageId);
        Assert.Equal(sent, Sent().Length);
        Assert.False(_rig.Memory.IsDetected(Group));
    }

    [Fact]
    public async Task Promotion_WhoseRightsArriveLate_RetriesThenCreates()
    {
        AddForum(topics: false);
        await AskWhere();
        await Click("1|tc", WizardId());
        _h.Api.OnSequence("getChatMember", FakeBotApi.Ok(TelegramFixtures.Json("get_chat_member_admin_no_topics")), FakeBotApi.Ok(TelegramFixtures.Json("get_chat_member_admin_no_topics")), FakeBotApi.Ok(TopicRights()));

        await Promoted(Group);

        Assert.Single(_h.Api.CallsTo("createForumTopic"));
        Assert.Equal([TimeSpan.FromSeconds(1.5)], _rig.Delays);
        Assert.False(_rig.Memory.HasRecentFailure());
    }

    [Fact]
    public async Task Promotion_InAnotherGroup_SaysSo_AndKeepsWaiting()
    {
        AddForum(topics: false);
        _h.Api.AddChat(Other, "supergroup", "Elsewhere", isForum: true);
        await AskWhere();
        var wizard = WizardId();
        await Click("1|tc", wizard);

        await Promoted(Other);

        Assert.Empty(_h.Api.CallsTo("createForumTopic"));
        var edit = _h.Api.CallsTo("editMessageText").Last();
        Assert.Equal(wizard, (int)edit.Body["message_id"]!);
        Assert.Contains("You changed my rights in", (string)edit.Body["text"]!, StringComparison.Ordinal);
        AssertPromotionScreen(edit);
        Assert.True(TopicFlow.AwaitsPromotion(_rig.Memory.GetFlow(Manager)!));
        Assert.False(_rig.Memory.IsDetected(Other));
        Assert.False(_rig.Memory.HasRecentFailure());
    }

    [Fact]
    public async Task CheckAgain_WithRights_CreatesTheTopic()
    {
        AddForum(topics: false);
        await AskWhere();
        var wizard = WizardId();
        await Click("1|tc", wizard);

        _h.Api.SetMember(Group, TopicRights());
        await Click("1|tk", wizard);

        Assert.Equal("Jellyfin", (string)Assert.Single(_h.Api.CallsTo("createForumTopic")).Body["name"]!);
        Assert.Equal("101", _rig.Memory.GetFlow(Manager)!.Params["threadId"]);
        Assert.Contains("What should I send there?", (string)_h.Api.CallsTo("editMessageText").Last().Body["text"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAgain_WithoutRights_SaysSo_AndKeepsTheLinks()
    {
        AddForum(topics: false);
        await AskWhere();
        var wizard = WizardId();
        await Click("1|tc", wizard);

        await Click("1|tk", wizard);

        Assert.Empty(_h.Api.CallsTo("createForumTopic"));
        var edit = _h.Api.CallsTo("editMessageText").Last();
        Assert.Equal(wizard, (int)edit.Body["message_id"]!);
        Assert.Contains("I still can't manage topics in", (string)edit.Body["text"]!, StringComparison.Ordinal);
        AssertPromotionScreen(edit);
        Assert.True(TopicFlow.AwaitsPromotion(_rig.Memory.GetFlow(Manager)!));
        Assert.False(_rig.Memory.HasRecentFailure());
    }

    [Fact]
    public async Task GroupStartTopics_InTheFlowsForum_CountsAsThePromotion()
    {
        AddForum(topics: false);
        await AskWhere();
        await Click("1|tc", WizardId());

        _h.Api.SetMember(Group, TopicRights());
        await _rig.Deliver(TelegramFixtures.Update(
            "group_command_start",
            ("chat_id", Group.ToString(CultureInfo.InvariantCulture)),
            ("text", "/start@tgjelly_test_bot topics")));

        Assert.Single(_h.Api.CallsTo("createForumTopic"));
        Assert.Equal("101", _rig.Memory.GetFlow(Manager)!.Params["threadId"]);
        Assert.DoesNotContain(_h.Api.CallsTo("sendMessage"), c => c.Body["chat_id"]!.ToString() == Group.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task PromotedButCreationFails_FallsBackToGeneralAndAdvanced_RecordsTopicFailed()
    {
        AddForum(topics: false);
        await AskWhere();
        var wizard = WizardId();
        await Click("1|tc", wizard);
        _h.Api.SetMember(Group, TopicRights());
        _h.Api.On("createForumTopic", _ => FakeBotApi.Error(400, "Bad Request: not enough rights to create a topic"));

        await Promoted(Group);

        var edit = _h.Api.CallsTo("editMessageText").Last();
        Assert.Equal(wizard, (int)edit.Body["message_id"]!);
        Assert.Equal(["General topic", "Advanced options"], Labels(edit));
        Assert.Equal(["1|tg", "1|ox"], Data(edit));
        Assert.Equal([SetupFailureKind.TopicFailed], _h.State.Read(s => s.SetupFailures.Select(f => f.Kind).ToList()));
        Assert.False(TopicFlow.AwaitsPromotion(_rig.Memory.GetFlow(Manager)!));

        await Click("1|tg", wizard);
        Assert.Equal("0", _rig.Memory.GetFlow(Manager)!.Params["threadId"]);
    }

    [Fact]
    public async Task CreationFails_FallsBackToGeneralAndAdvanced_RecordsTopicFailed()
    {
        AddForum();
        _h.Api.On("createForumTopic", _ => FakeBotApi.Error(400, "Bad Request: not enough rights to create a topic"));
        await AskWhere();

        await Click("1|tc", WizardId());

        Assert.Equal(["1|tg", "1|ox"], Data(_h.Api.CallsTo("editMessageText").Last()));
        Assert.Equal([SetupFailureKind.TopicFailed], _h.State.Read(s => s.SetupFailures.Select(f => f.Kind).ToList()));
    }

    [Fact]
    public async Task LegacyAllowTopicPick_IsExpired()
    {
        AddForum(topics: false);
        await AskWhere();
        await Click("1|tc", WizardId());
        var before = _rig.Memory.GetFlow(Manager)!;
        var sent = Sent().Length;

        await _rig.Deliver(TelegramFixtures.Update("chat_shared", ("request_id", "5"), ("chat_id", Group.ToString(CultureInfo.InvariantCulture))));

        var expired = Sent()[sent];
        Assert.Equal("That menu expired", (string)expired.Body["text"]!);
        Assert.True((bool)expired.Body["reply_markup"]!["remove_keyboard"]!);
        Assert.Empty(_h.Api.CallsTo("createForumTopic"));
        var after = _rig.Memory.GetFlow(Manager)!;
        Assert.Equal(before.WizardMessageId, after.WizardMessageId);
        Assert.Equal(before.Params, after.Params);
        Assert.True(TopicFlow.AwaitsPromotion(after));
    }

    [Fact]
    public async Task PresetButton_OnTheTopicWizard_IsExpired_AndCreatesNothing()
    {
        AddForum();
        await AskWhere();

        await Click("1|op|Everything", WizardId());

        Assert.Contains(_h.Strings.Get("menu.expired"), (string)Sent().First(c => c.Body["text"]!.ToString().Contains(_h.Strings.Get("menu.expired"), StringComparison.Ordinal)).Body["text"]!, StringComparison.Ordinal);
        Assert.Empty(_h.Api.CallsTo("createForumTopic"));
        Assert.Empty(_h.Settings.Current.Destinations);
    }

    [Fact]
    public async Task Existing_RecordsTopicNotPickable_AndAsksInTheWizard()
    {
        AddForum();
        await AskWhere();
        var wizard = WizardId();

        await Click("1|te", wizard);

        var prompt = _h.Api.CallsTo("editMessageText").Last();
        Assert.Equal(wizard, (int)prompt.Body["message_id"]!);
        Assert.Contains("Send me the topic's link as a message.", (string)prompt.Body["text"]!, StringComparison.Ordinal);
        Assert.Equal(["1|tb"], Data(prompt));
        prompt.Explains("Cancel");
        Assert.DoesNotContain(_h.Api.Calls, c => c.Body.ToJsonString().Contains("force_reply", StringComparison.Ordinal));
        Assert.Equal("topicLink", _rig.Memory.GetFlow(Manager)!.Params["input"]);
        Assert.Equal([SetupFailureKind.TopicNotPickable], _h.State.Read(s => s.SetupFailures.Select(f => f.Kind).ToList()));
    }

    [Theory]
    [InlineData("https://t.me/c/5550001/9")]
    [InlineData("https://t.me/familychat/9")]
    public async Task ExistingLink_ForThisChat_ByIdAndByUsername_Works(string link)
    {
        AddForum(username: "familychat");
        await AskWhere();
        var wizard = WizardId();
        await Click("1|te", wizard);

        await Text(link);

        var probe = Assert.Single(_h.Api.CallsTo("sendChatAction")).Body;
        Assert.Equal(9, (int)probe["message_thread_id"]!);
        var flow = _rig.Memory.GetFlow(Manager)!;
        Assert.Equal("9", flow.Params["threadId"]);
        Assert.False(flow.Params.ContainsKey("input"));
        Assert.Equal(wizard, flow.WizardMessageId);
        Assert.Contains("What should I send there?", (string)_h.Api.CallsTo("editMessageText").Last().Body["text"]!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://t.me/c/999/9")]
    [InlineData("https://t.me/otherchat/9")]
    public async Task ExistingLink_ForAnotherChat_IsRejected(string link)
    {
        AddForum(username: "familychat");
        await AskWhere();
        await Click("1|te", WizardId());

        await Text(link);

        Assert.Empty(_h.Api.CallsTo("sendChatAction"));
        var prompt = Sent().Last();
        Assert.Contains("different chat", (string)prompt.Body["text"]!, StringComparison.Ordinal);
        Assert.Equal(["1|tb"], Data(prompt));
        Assert.Equal("topicLink", _rig.Memory.GetFlow(Manager)!.Params["input"]);
    }

    [Fact]
    public async Task ExistingLink_ThatIsNotALink_ExplainsAndAsksAgain()
    {
        AddForum();
        await AskWhere();
        await Click("1|te", WizardId());

        await Text("hello");

        var prompt = Sent().Last();
        Assert.Contains("isn't a topic link", (string)prompt.Body["text"]!, StringComparison.Ordinal);
        Assert.Equal(["1|tb"], Data(prompt));
        Assert.Equal("topicLink", _rig.Memory.GetFlow(Manager)!.Params["input"]);
    }

    [Fact]
    public async Task TopicQuestion_ExplainsEachChoice()
    {
        AddForum();

        await AskWhere();

        _h.Api.CallsTo("editMessageText").Last().Explains("General topic", "Create a &quot;Jellyfin&quot; topic", "Use an existing topic…");
    }

    [Fact]
    public async Task LinkCancel_ReturnsToTheTopicQuestion()
    {
        AddForum();
        await AskWhere();
        var wizard = WizardId();
        await Click("1|te", wizard);

        await Click("1|tb", wizard);

        var edit = _h.Api.CallsTo("editMessageText").Last();
        Assert.Equal(wizard, (int)edit.Body["message_id"]!);
        Assert.Equal(["1|tg", "1|tc", "1|te"], Data(edit));
        await Text("https://t.me/c/5550001/9");
        Assert.Empty(_h.Api.CallsTo("sendChatAction"));
    }

    [Fact]
    public async Task BadLink_AsksAgainInANewMessage_ThatLaterStepsEdit()
    {
        AddForum();
        await AskWhere();
        var oldId = WizardId();
        await Click("1|te", oldId);
        var before = Sent().Length;

        await Text("nope");

        var again = Sent().Skip(before).Single();
        Assert.StartsWith("That isn't a topic link.", (string)again.Body["text"]!, StringComparison.Ordinal);
        Assert.Equal(["1|tb"], Data(again));
        var newId = WizardId();
        Assert.NotEqual(oldId, newId);
        await Text("https://t.me/c/5550001/9");
        var edit = _h.Api.CallsTo("editMessageText").Last();
        Assert.Equal(newId, (int)edit.Body["message_id"]!);
        Assert.Contains("What should I send there?", (string)edit.Body["text"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExistingLink_ToAMissingTopic_FallsBackToGeneralAndAdvanced()
    {
        AddForum();
        _h.Api.On("sendChatAction", _ => FakeBotApi.Error(400, "Bad Request: message thread not found"));
        await AskWhere();
        await Click("1|te", WizardId());

        await Text("https://t.me/c/5550001/9");

        Assert.Equal(["1|tg", "1|ox"], Data(_h.Api.CallsTo("editMessageText").Last()));
        Assert.Contains(SetupFailureKind.TopicFailed, _h.State.Read(s => s.SetupFailures.Select(f => f.Kind).ToList()));
    }

    [Fact]
    public async Task ExistingTopic_AlreadyADestination_SaysSo()
    {
        _h.AddDestination(Group, threadId: 9);
        AddForum();
        await AskWhere();
        await Click("1|te", WizardId());

        await Text("https://t.me/c/5550001/9");

        Assert.Contains("is already set up", (string)_h.Api.CallsTo("editMessageText").Last().Body["text"]!, StringComparison.Ordinal);
        Assert.Null(_rig.Memory.GetFlow(Manager));
    }

    [Fact]
    public async Task LinkText_WithoutTheTopicLinkInput_IsNotTreatedAsALink()
    {
        AddForum();
        await AskWhere();
        var edits = _h.Api.CallsTo("editMessageText").Count();

        await Text("https://t.me/c/5550001/9");

        Assert.Empty(_h.Api.CallsTo("sendChatAction"));
        Assert.Equal(edits, _h.Api.CallsTo("editMessageText").Count());
    }

    private static string TopicRights() => TelegramFixtures.Json("get_chat_member_admin_topics");

    private void AddForum(bool topics = true, string? username = null)
    {
        _h.Api.AddChat(Group, "supergroup", "Family", isForum: true, username: username);
        _h.Api.SetMember(Group, topics ? TopicRights() : TelegramFixtures.Json("get_chat_member_admin_no_topics"));
    }

    private async Task AskWhere()
    {
        await Text("/start add");
        await Added();
    }

    /// <summary>The manager adds the bot to the forum through the prompt's Add to a group link.</summary>
    private Task Added() => _rig.Deliver(TelegramFixtures.Update("my_chat_member_group_member", ("chat_id", Group.ToString(CultureInfo.InvariantCulture))));

    private int WizardId() => _rig.Memory.GetFlow(Manager)!.WizardMessageId!.Value;

    /// <summary>The manager makes the bot an admin who can manage topics, through the promotion screen's link.</summary>
    private Task Promoted(long chatId)
        => _rig.Deliver(TelegramFixtures.Update("my_chat_member_group_promoted", ("chat_id", chatId.ToString(CultureInfo.InvariantCulture))));

    private Task Click(string data, int messageId)
        => _rig.Deliver(TelegramFixtures.Update("callback_query", ("data", data), ("message_id", messageId.ToString(CultureInfo.InvariantCulture))));

    private Task Text(string text)
        => _rig.Deliver(TelegramFixtures.Update("private_text", ("text", System.Text.Json.JsonEncodedText.Encode(text).ToString())));

    private BotApiCall[] Sent()
        => [.. _h.Api.CallsTo("sendMessage").Where(c => c.Body["chat_id"]!.ToString() == Manager.ToString(CultureInfo.InvariantCulture))];

    private int[] Deleted() => [.. _h.Api.CallsTo("deleteMessage").Select(c => (int)c.Body["message_id"]!)];

    private static string[] Labels(BotApiCall call) => [.. Buttons(call).Select(b => (string)b["text"]!)];

    /// <summary>The callback data of a message's buttons; URL buttons have none and are skipped.</summary>
    private static string[] Data(BotApiCall call) => [.. Buttons(call).Select(b => (string?)b["callback_data"]).OfType<string>()];

    /// <summary>The promotion screen: its four buttons, the Allow link, and one explanation line per button in order.</summary>
    private static void AssertPromotionScreen(BotApiCall call)
    {
        Assert.Equal(["Allow topic creation", "Check again", "General topic", "Advanced options"], Labels(call));
        Assert.Equal("https://t.me/tgjelly_test_bot?startgroup=topics&admin=manage_topics", (string)Buttons(call)[0]["url"]!);
        Assert.Equal(["1|tk", "1|tg", "1|ox"], Data(call));
        string[] labels = ["Allow topic creation", "Check again", "General topic", "Advanced options"];
        call.Explains(labels);
        var text = (string)call.Body["text"]!;
        var at = labels.Select(l => text.IndexOf("<b>" + l + "</b> — ", StringComparison.Ordinal)).ToArray();
        Assert.Equal([.. at.Order()], at);
    }

    private static JsonNode[] Buttons(BotApiCall call)
        => [.. call.Body["reply_markup"]!["inline_keyboard"]!.AsArray().SelectMany(r => r!.AsArray()).Select(b => b!)];
}
