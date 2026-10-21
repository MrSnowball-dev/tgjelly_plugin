using System;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
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
    private int _nextMessageId = 100;

    public async ValueTask InitializeAsync() => _rig = await BotRig.CreateAsync(_h);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Forum_AsksWhereToPost()
    {
        AddForum();
        await Text("/start add");
        var promptId = _rig.Memory.GetFlow(Manager)!.PromptMessageId!.Value;

        await Shared(GroupRequest());

        var wizard = Sent().Last();
        Assert.Contains("This group has topics. Where should I post?", (string)wizard.Body["text"]!, StringComparison.Ordinal);
        Assert.Equal(["General topic", "Create a \"Jellyfin\" topic", "Use an existing topic…"], Labels(wizard));
        Assert.Equal(["1|tg", "1|tc", "1|te"], Data(wizard));
        Assert.Equal([promptId, promptId + 1], Deleted());
        var flow = _rig.Memory.GetFlow(Manager)!;
        Assert.Equal(promptId + 2, flow.WizardMessageId);
        Assert.Equal("1", flow.Params["isForum"]);
        Assert.Equal(Group.ToString(CultureInfo.InvariantCulture), flow.Params["chatId"]);
        Assert.Null(flow.CheckingMessageId);
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
    public async Task Create_WithoutRights_SendsThePromotionPicker_ThenCreates()
    {
        AddForum(topics: false);
        await AskWhere();
        var wizard = WizardId();

        await Click("1|tc", wizard);

        Assert.Empty(_h.Api.CallsTo("createForumTopic"));
        var prompt = Sent().Last();
        var keyboard = prompt.Body["reply_markup"]!["keyboard"]!.AsArray().Select(r => r!.AsArray()).ToList();
        var button = Assert.Single(Assert.Single(keyboard))!;
        Assert.Equal("Allow topic creation", (string)button["text"]!);
        var request = button["request_chat"]!;
        Assert.True((bool)request["chat_is_forum"]!);
        Assert.False((bool?)request["chat_is_channel"] ?? false);
        Assert.True((bool)request["request_title"]!);
        Assert.True((bool)request["bot_administrator_rights"]!["can_manage_topics"]!);
        Assert.True((bool)request["user_administrator_rights"]!["can_manage_topics"]!);
        var flow = _rig.Memory.GetFlow(Manager)!;
        Assert.Equal([(int)request["request_id"]!], flow.PickerRequestIds);
        Assert.Contains("manage topics", (string)prompt.Body["text"]!, StringComparison.Ordinal);
        Assert.Equal(1, Sent().Count(c => c.Body["reply_markup"]?["keyboard"] is not null && c.Body["reply_markup"]!["keyboard"]!.ToString().Contains("Allow topic creation", StringComparison.Ordinal)));
        var promptId = flow.PromptMessageId!.Value;

        _h.Api.SetMember(Group, TopicRights());
        await Shared((int)request["request_id"]!);

        Assert.Equal("Jellyfin", (string)Assert.Single(_h.Api.CallsTo("createForumTopic")).Body["name"]!);
        Assert.Contains(promptId, Deleted());
        var after = _rig.Memory.GetFlow(Manager)!;
        Assert.Equal("101", after.Params["threadId"]);
        Assert.Equal(wizard, after.WizardMessageId);
        Assert.Contains("What should I send there?", (string)_h.Api.CallsTo("editMessageText").Last().Body["text"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Promotion_ForAnotherChat_IsRejected()
    {
        AddForum(topics: false);
        _h.Api.AddChat(Other, "supergroup", "Elsewhere", isForum: true);
        await AskWhere();
        await Click("1|tc", WizardId());
        var request = _rig.Memory.GetFlow(Manager)!.PickerRequestIds.Single();

        await Shared(request, Other);

        Assert.Empty(_h.Api.CallsTo("createForumTopic"));
        Assert.Contains("different chat", (string)Sent().Last().Body["text"]!, StringComparison.Ordinal);
        Assert.Equal([request], _rig.Memory.GetFlow(Manager)!.PickerRequestIds);
        Assert.False(_rig.Memory.HasRecentFailure());
    }

    [Fact]
    public async Task PromotionFails_FallsBackToGeneralAndAdvanced_RecordsTopicFailed()
    {
        AddForum(topics: false);
        await AskWhere();
        var wizard = WizardId();
        await Click("1|tc", wizard);

        await Shared(_rig.Memory.GetFlow(Manager)!.PickerRequestIds.Single()); // the promotion did not happen

        Assert.Empty(_h.Api.CallsTo("createForumTopic"));
        var edit = _h.Api.CallsTo("editMessageText").Last();
        Assert.Equal(wizard, (int)edit.Body["message_id"]!);
        Assert.Equal(["General topic", "Advanced options"], Labels(edit));
        Assert.Equal(["1|tg", "1|ox"], Data(edit));
        Assert.Equal([SetupFailureKind.TopicFailed], _h.State.Read(s => s.SetupFailures.Select(f => f.Kind).ToList()));

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
    public async Task AbandonedPromotion_DoesNotBlockCreate()
    {
        AddForum(topics: false);
        await AskWhere();
        var wizard = WizardId();
        await Click("1|tc", wizard);
        await Click("1|te", wizard);

        await Click("1|tc", wizard);

        Assert.Equal(2, Sent().Count(c => c.Body["reply_markup"]?["keyboard"]?.ToString().Contains("Allow topic creation", StringComparison.Ordinal) == true));
    }

    [Fact]
    public async Task ExpiredPromotion_CreateAgain_SendsAFreshPrompt()
    {
        AddForum(topics: false);
        await AskWhere();
        var wizard = WizardId();
        await Click("1|tc", wizard);
        var first = _rig.Memory.GetFlow(Manager)!.PickerRequestIds.Single();

        _h.Time.Advance(TimeSpan.FromMinutes(11));
        await _rig.Flow.TickAsync(TestContext.Current.CancellationToken);
        await Click("1|tc", wizard);

        Assert.Equal(2, PromotionPrompts());
        var second = _rig.Memory.GetFlow(Manager)!.PickerRequestIds.Single();
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task IgnoredPromotion_CreateAgain_SpendsTheOldPromptAndSendsAFreshOne()
    {
        AddForum(topics: false);
        await AskWhere();
        var wizard = WizardId();
        await Click("1|tc", wizard);
        var flow = _rig.Memory.GetFlow(Manager)!;
        var oldPrompt = flow.PromptMessageId!.Value;
        var first = flow.PickerRequestIds.Single();

        await Click("1|tc", wizard);

        Assert.Equal(2, PromotionPrompts());
        Assert.Contains(oldPrompt, Deleted());
        Assert.NotEqual(first, _rig.Memory.GetFlow(Manager)!.PickerRequestIds.Single());
    }

    [Fact]
    public async Task Promotion_WhoseRightsArriveLate_RetriesThenCreates()
    {
        AddForum(topics: false);
        await AskWhere();
        await Click("1|tc", WizardId());
        var request = _rig.Memory.GetFlow(Manager)!.PickerRequestIds.Single();
        _h.Api.OnSequence("getChatMember", FakeBotApi.Ok(TelegramFixtures.Json("get_chat_member_admin_no_topics")), FakeBotApi.Ok(TelegramFixtures.Json("get_chat_member_admin_no_topics")), FakeBotApi.Ok(TopicRights()));

        await Shared(request);

        Assert.Single(_h.Api.CallsTo("createForumTopic"));
        Assert.Equal([TimeSpan.FromSeconds(1.5)], _rig.Delays);
        Assert.False(_rig.Memory.HasRecentFailure());
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
    public async Task Existing_RecordsTopicNotPickable_AndAsksForTheLink()
    {
        AddForum();
        await AskWhere();

        await Click("1|te", WizardId());

        var prompt = Sent().Last();
        Assert.Contains("Copy link", (string)prompt.Body["text"]!, StringComparison.Ordinal);
        Assert.True((bool)prompt.Body["reply_markup"]!["force_reply"]!);
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
        Assert.True((bool)prompt.Body["reply_markup"]!["force_reply"]!);
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
        Assert.True((bool)prompt.Body["reply_markup"]!["force_reply"]!);
        Assert.Equal("topicLink", _rig.Memory.GetFlow(Manager)!.Params["input"]);
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

        await Text("https://t.me/c/5550001/9");

        Assert.Empty(_h.Api.CallsTo("sendChatAction"));
        Assert.Empty(_h.Api.CallsTo("editMessageText"));
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
        await Shared(GroupRequest());
    }

    private int GroupRequest() => int.Parse(_rig.Memory.GetFlow(Manager)!.Params["requestGroup"], CultureInfo.InvariantCulture);

    private int WizardId() => _rig.Memory.GetFlow(Manager)!.WizardMessageId!.Value;

    private Task Shared(int requestId, long chatId = Group)
        => _rig.Deliver(TelegramFixtures.Update(
            "chat_shared",
            ("request_id", requestId.ToString(CultureInfo.InvariantCulture)),
            ("chat_id", chatId.ToString(CultureInfo.InvariantCulture)),
            ("message_id", (++_nextMessageId).ToString(CultureInfo.InvariantCulture))));

    private Task Click(string data, int messageId)
        => _rig.Deliver(TelegramFixtures.Update("callback_query", ("data", data), ("message_id", messageId.ToString(CultureInfo.InvariantCulture))));

    private Task Text(string text)
        => _rig.Deliver(TelegramFixtures.Update("private_text", ("text", System.Text.Json.JsonEncodedText.Encode(text).ToString())));

    private BotApiCall[] Sent()
        => [.. _h.Api.CallsTo("sendMessage").Where(c => c.Body["chat_id"]!.ToString() == Manager.ToString(CultureInfo.InvariantCulture))];

    private int PromotionPrompts()
        => Sent().Count(c => c.Body["reply_markup"]?["keyboard"]?.ToString().Contains("Allow topic creation", StringComparison.Ordinal) == true);

    private int[] Deleted() => [.. _h.Api.CallsTo("deleteMessage").Select(c => (int)c.Body["message_id"]!)];

    private static string[] Labels(BotApiCall call) => [.. Buttons(call).Select(b => (string)b["text"]!)];

    private static string[] Data(BotApiCall call) => [.. Buttons(call).Select(b => (string)b["callback_data"]!)];

    private static JsonNode[] Buttons(BotApiCall call)
        => [.. call.Body["reply_markup"]!["inline_keyboard"]!.AsArray().SelectMany(r => r!.AsArray()).Select(b => b!)];
}
