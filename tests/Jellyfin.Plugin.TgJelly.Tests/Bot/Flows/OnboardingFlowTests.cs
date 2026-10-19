using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Bot.Flows;

public sealed class OnboardingFlowTests : IDisposable, IAsyncLifetime
{
    private const long Manager = 42;
    private const long Channel = -1001234567890;
    private const long Group = -1005550001;
    private const string NoPhotos = """{"can_send_messages":true,"can_send_photos":false}""";
    private readonly BotTestHarness _h = new();
    private BotRig _rig = null!;

    public async ValueTask InitializeAsync() => _rig = await BotRig.CreateAsync(_h);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Start_SendsTheReplyKeyboard_WithFreshRequestIds()
    {
        await Start();

        var prompt = Assert.Single(Sent());
        Assert.Equal("Where should notifications go?", (string)prompt.Body["text"]!);
        var keyboard = prompt.Body["reply_markup"]!["keyboard"]!.AsArray().Select(r => r!.AsArray()).ToList();
        Assert.Equal(["📢 Choose a channel", "👥 Choose a group", "💬 Right here", "Cancel"], keyboard.Select(r => (string)Assert.Single(r)!["text"]!));

        var channel = keyboard[0][0]!["request_chat"]!;
        Assert.True((bool)channel["chat_is_channel"]!);
        Assert.True((bool)channel["request_title"]!);
        foreach (var rights in new[] { channel["bot_administrator_rights"]!, channel["user_administrator_rights"]! })
        {
            Assert.True((bool)rights["can_post_messages"]!);
            Assert.True((bool)rights["can_edit_messages"]!);
        }

        var group = keyboard[1][0]!["request_chat"]!;
        Assert.False((bool?)group["chat_is_channel"] ?? false); // Telegram.Bot leaves false out; the API reads that as false
        Assert.True((bool)group["bot_is_member"]!);
        Assert.True((bool)group["request_title"]!);

        var flow = _rig.Memory.GetFlow(Manager)!;
        var first = new[] { (int)channel["request_id"]!, (int)group["request_id"]! };
        Assert.Equal(first, flow.PickerRequestIds);
        Assert.Equal(first[0].ToString(System.Globalization.CultureInfo.InvariantCulture), flow.Params["requestChannel"]);
        Assert.Equal(first[1].ToString(System.Globalization.CultureInfo.InvariantCulture), flow.Params["requestGroup"]);
        Assert.Equal(1, flow.PromptMessageId);
        Assert.Equal(_h.Time.GetUtcNow(), flow.PromptSentAt);
        Assert.NotEqual(first[0], first[1]);

        await Start();
        var second = _rig.Memory.GetFlow(Manager)!.PickerRequestIds;
        Assert.Empty(first.Intersect(second));
    }

    [Theory]
    [InlineData("/start setup")]
    [InlineData("/start add")]
    [InlineData("/start")]
    public async Task StartCommands_SendThePrompt(string text)
    {
        await Text(text);

        Assert.Equal("Where should notifications go?", (string)Assert.Single(Sent()).Body["text"]!);
    }

    [Fact]
    public async Task MenuAddButton_SendsThePrompt()
    {
        _h.AddDestination(-1001);

        await Click("1|oa", 55);

        Assert.Equal("Where should notifications go?", (string)Assert.Single(Sent()).Body["text"]!);
    }

    [Fact]
    public async Task StartReplacesAUrlPrompt()
    {
        await Click("1|spt", 55);
        Assert.Equal("publicUrl", _rig.Memory.GetFlow(Manager)!.Params["input"]);

        await Text("/start add");

        Assert.False(_rig.Memory.GetFlow(Manager)!.Params.ContainsKey("input"));
    }

    [Fact]
    public async Task ChannelShared_ChecksThenAsksForThePreset_AndDeletesCheckingAndPrompt()
    {
        AddChannel();
        await Start();
        var promptId = _rig.Memory.GetFlow(Manager)!.PromptMessageId!.Value;

        await Shared(ChannelRequest());

        var sent = Sent().Skip(1).ToList();
        Assert.Equal(2, sent.Count);
        Assert.Equal("Checking <b>Family</b>…", (string)sent[0].Body["text"]!);
        Assert.True((bool)sent[0].Body["reply_markup"]!["remove_keyboard"]!);
        Assert.Contains("What should I send there?", (string)sent[1].Body["text"]!, StringComparison.Ordinal);
        Assert.Equal(["Everything", "New media", "Admin alerts", "Custom…"], Labels(sent[1]));

        var checkingId = promptId + 1;
        Assert.Equal([promptId, checkingId], Deleted());

        var flow = _rig.Memory.GetFlow(Manager)!;
        Assert.Equal(checkingId + 1, flow.WizardMessageId);
        Assert.Empty(flow.PickerRequestIds);
        Assert.Null(flow.PromptMessageId);
        Assert.Null(flow.PromptSentAt);
        Assert.Equal(Channel.ToString(System.Globalization.CultureInfo.InvariantCulture), flow.Params["chatId"]);
        Assert.Equal("Family", flow.Params["title"]);
        Assert.Equal(nameof(ChatKind.Channel), flow.Params["kind"]);
        Assert.Equal("0", flow.Params["threadId"]);
        Assert.Empty(_rig.Delays);
    }

    [Fact]
    public async Task ChatSharedBeforeMyChatMember_RetriesUntilThePass()
    {
        _h.Api.AddChat(Channel, "channel", "Family");
        _h.Api.OnSequence("getChatMember", FakeBotApi.Ok(FakeBotApi.Member("left")), FakeBotApi.Ok(FakeBotApi.Member("left")), FakeBotApi.Ok(BotTestHarness.AdminCanPost));
        await Start();

        await Shared(ChannelRequest());

        Assert.Equal([TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(1.5)], _rig.Delays);
        Assert.Equal(3, _h.Api.CallsTo("getChatMember").Count());
        Assert.Contains("What should I send there?", (string)Sent().Last().Body["text"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatSharedAfterMyChatMember_PassesFirstTry()
    {
        AddChannel();
        await Start();

        await Shared(ChannelRequest());

        Assert.Empty(_rig.Delays);
        Assert.Single(_h.Api.CallsTo("getChatMember"));
        Assert.Contains("What should I send there?", (string)Sent().Last().Body["text"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckFails_DeletesChecking_OffersTryAgainAndAdvanced_RecordsFailure()
    {
        _h.Api.AddChat(Channel, "channel", "Family");
        await Start();
        var promptId = _rig.Memory.GetFlow(Manager)!.PromptMessageId!.Value;

        await Shared(ChannelRequest());

        Assert.Equal(3, _rig.Delays.Count);
        Assert.Equal([promptId, promptId + 1], Deleted());
        var failure = Sent().Last();
        Assert.Equal(_h.Strings.Get("check.lost"), (string)failure.Body["text"]!);
        Assert.Equal(["Try again", "Advanced options"], Labels(failure));
        Assert.Equal(["1|ot", "1|ox"], Data(failure));
        Assert.Equal([SetupFailureKind.CheckFailed], _h.State.Read(s => s.SetupFailures.Select(f => f.Kind).ToList()));
        Assert.Empty(_rig.Memory.GetFlow(Manager)!.PickerRequestIds);
        Assert.Empty(_h.Settings.Current.Destinations);

        await Click("1|ot", 77);
        Assert.Equal("Where should notifications go?", (string)Sent().Last().Body["text"]!);
        Assert.NotEmpty(_rig.Memory.GetFlow(Manager)!.PickerRequestIds);
    }

    [Fact]
    public async Task AdvancedOptions_ShowsTheMenu()
    {
        await Click("1|ox", 77);

        var edit = Assert.Single(_h.Api.CallsTo("editMessageText"));
        Assert.Equal(77, (int)edit.Body["message_id"]!);
        Assert.Contains("🏠 Menu", (string)edit.Body["text"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TransportErrorDuringCheck_ReportsFailure()
    {
        AddChannel();
        await Start();
        var promptId = _rig.Memory.GetFlow(Manager)!.PromptMessageId!.Value;
        _h.Api.On("getChat", _ => throw new HttpRequestException("connection reset"));

        await Shared(ChannelRequest());

        Assert.Equal([promptId, promptId + 1], Deleted());
        var failure = Sent().Last();
        Assert.Equal(_h.Strings.Get("token.unreachable"), (string)failure.Body["text"]!);
        Assert.Equal(["Try again", "Advanced options"], Labels(failure));
        Assert.True(_rig.Memory.HasRecentFailure());

        await Text("/menu");
        Assert.Contains("🏠 Menu", (string)Sent().Last().Body["text"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preset_Creates_SendsTheTest_AndEditsTheWizardToDone()
    {
        AddChannel();
        await Start();
        await Shared(ChannelRequest());
        var wizard = WizardId();
        var checks = _h.Api.CallsTo("getChatMember").Count();

        await Click("1|op|Everything", wizard);

        var destination = Assert.Single(_h.Settings.Current.Destinations);
        Assert.Equal(Channel, destination.ChatId);
        Assert.Equal(0, destination.ThreadId);
        Assert.Equal("Family", destination.Title);
        Assert.Equal(ChatKind.Channel, destination.Kind);
        Assert.Equal(PresetKind.Everything, destination.Preset);
        Assert.Equal(EventCatalog.AllKeys, destination.Events);
        Assert.True(_h.Api.CallsTo("getChatMember").Count() > checks + 1, "re-checked before creating, then before the test");

        var test = Assert.Single(Sent(Channel));
        Assert.Contains("TgJelly test", (string)test.Body["text"]!, StringComparison.Ordinal);

        var done = _h.Api.CallsTo("editMessageText").Last();
        Assert.Equal(wizard, (int)done.Body["message_id"]!);
        Assert.Equal("✅ Done: <b>Family</b> will receive <i>new media, security alerts, server alerts</i>.", (string)done.Body["text"]!);
        Assert.Equal(["Add another place", "Menu"], Labels(done));
        Assert.Equal(["1|oa", "1|m"], Data(done));
        Assert.Null(_rig.Memory.GetFlow(Manager));
    }

    [Fact]
    public async Task Preset_ReChecksFirst_AndReportsAChatThatChanged()
    {
        AddChannel();
        await Start();
        await Shared(ChannelRequest());
        _h.Api.SetMember(Channel, FakeBotApi.Member("left"));

        await Click("1|op|Everything", WizardId());

        Assert.Empty(_h.Settings.Current.Destinations);
        var failure = _h.Api.CallsTo("editMessageText").Last();
        Assert.Equal(_h.Strings.Get("check.lost"), (string)failure.Body["text"]!);
        Assert.Equal(["Try again", "Advanced options"], Labels(failure));
        Assert.True(_rig.Memory.HasRecentFailure());
    }

    [Fact]
    public async Task FailedTestSend_PutsItsErrorUnderTheDoneLine()
    {
        AddChannel();
        await Start();
        await Shared(ChannelRequest());
        _h.Api.On("sendMessage", call => call.Body["chat_id"]!.ToString() == Channel.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ? FakeBotApi.Error(400, "Bad Request: not enough rights to send text messages to the chat")
            : FakeBotApi.Ok("""{"message_id":900,"date":1790000000,"chat":{"id":42,"type":"private"},"text":"x"}"""));

        await Click("1|op|NewMedia", WizardId());

        var done = (string)_h.Api.CallsTo("editMessageText").Last().Body["text"]!;
        Assert.StartsWith("✅ Done: <b>Family</b> will receive <i>new media</i>.\n", done, StringComparison.Ordinal);
        Assert.Contains("❌ Test failed: Bad Request: not enough rights", done, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Custom_StartsWithNothingSelected_ThenCreatesWithTheChosenKeys()
    {
        AddChannel();
        await Start();
        await Shared(ChannelRequest());
        var wizard = WizardId();

        await Click("1|op|Custom", wizard);

        var switches = _h.Api.CallsTo("editMessageText").Last();
        Assert.Equal(wizard, (int)switches.Body["message_id"]!);
        var labels = Labels(switches);
        Assert.Equal(EventCatalog.AllKeys.Count + 1, labels.Length);
        Assert.All(labels.Take(EventCatalog.AllKeys.Count), l => Assert.StartsWith("▫️", l, StringComparison.Ordinal));
        Assert.Equal("1|ocd", Data(switches).Last());

        await Click("1|oc|server.started", wizard);
        await Click("1|oc|media.movie", wizard);
        await Click("1|oc|security.lockout", wizard);
        await Click("1|oc|security.lockout", wizard);
        Assert.Equal(2, Labels(_h.Api.CallsTo("editMessageText").Last()).Count(l => l.StartsWith('✅')));
        Assert.Empty(_h.Settings.Current.Destinations);

        await Click("1|ocd", wizard);

        var destination = Assert.Single(_h.Settings.Current.Destinations);
        Assert.Equal(PresetKind.Custom, destination.Preset);
        Assert.Equal(["media.movie", "server.started"], destination.Events);
        Assert.Equal("✅ Done: <b>Family</b> will receive <i>new media, server alerts</i>.", (string)_h.Api.CallsTo("editMessageText").Last().Body["text"]!);
    }

    [Fact]
    public async Task Duplicate_SaysAlreadySetUp_ReChecks_AndOpensTheDestination()
    {
        var existing = _h.AddDestination(Channel, ChatKind.Channel);
        await Start();
        var promptId = _rig.Memory.GetFlow(Manager)!.PromptMessageId!.Value;

        await Shared(ChannelRequest());

        Assert.Single(_h.Settings.Current.Destinations);
        Assert.Equal(2, _h.Api.CallsTo("getChatMember").Count());
        var screen = Sent().Last();
        Assert.StartsWith("<b>" + existing.Title + "</b> is already set up", (string)screen.Body["text"]!, StringComparison.Ordinal);
        Assert.Contains("1|de|" + existing.Id + "|0", Data(screen));
        Assert.Equal([promptId, promptId + 1], Deleted());
        Assert.Null(_rig.Memory.GetFlow(Manager));
        Assert.Empty(Sent(Channel));
    }

    [Fact]
    public async Task RedeliveredChatShared_AddsOnce()
    {
        AddChannel();
        await Start();
        var shared = TelegramFixtures.Update("chat_shared", ("request_id", ChannelRequest().ToString(System.Globalization.CultureInfo.InvariantCulture)));

        await _rig.Deliver(shared);
        await _rig.Deliver(shared);

        Assert.Single(Sent(), c => ((string)c.Body["text"]!).Contains("What should I send there?", StringComparison.Ordinal));
        Assert.DoesNotContain(Sent(), c => (string)c.Body["text"]! == "That picker expired");

        var wizard = WizardId();
        await Click("1|op|Everything", wizard);
        await Click("1|op|Everything", wizard);

        Assert.Single(_h.Settings.Current.Destinations);
        Assert.Single(Sent(Channel));
        Assert.Equal("That menu expired", (string)Sent().SkipLast(1).Last().Body["text"]!);
    }

    [Fact]
    public async Task RightHere_UsesThisChat()
    {
        _h.Api.AddChat(Manager, "private", "Ann");
        await Start();
        var promptId = _rig.Memory.GetFlow(Manager)!.PromptMessageId!.Value;

        await Text("💬 Right here");

        var sent = Sent().Skip(1).ToList();
        Assert.Equal("Right here.", (string)sent[0].Body["text"]!);
        Assert.True((bool)sent[0].Body["reply_markup"]!["remove_keyboard"]!);
        Assert.Contains("What should I send there?", (string)sent[1].Body["text"]!, StringComparison.Ordinal);
        Assert.Equal([promptId, promptId + 1], Deleted());
        Assert.Empty(_h.Api.CallsTo("getChatMember"));

        await Click("1|op|AdminAlerts", WizardId());

        var destination = Assert.Single(_h.Settings.Current.Destinations);
        Assert.Equal(Manager, destination.ChatId);
        Assert.Equal(ChatKind.Private, destination.Kind);
        Assert.Equal(PresetKind.AdminAlerts, destination.Preset);
        Assert.Contains(Sent(), c => ((string)c.Body["text"]!).Contains("TgJelly test", StringComparison.Ordinal));
        Assert.StartsWith("✅ Done: <b>Ann</b>", (string)_h.Api.CallsTo("editMessageText").Last().Body["text"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancel_RemovesTheKeyboard()
    {
        await Start();
        var promptId = _rig.Memory.GetFlow(Manager)!.PromptMessageId!.Value;

        await Text("Cancel");

        var sent = Sent().Skip(1).ToList();
        Assert.Equal(2, sent.Count);
        Assert.True((bool)sent[0].Body["reply_markup"]!["remove_keyboard"]!);
        Assert.Contains("🏠 Menu", (string)sent[1].Body["text"]!, StringComparison.Ordinal);
        Assert.Equal([promptId], Deleted());
        Assert.Null(_rig.Memory.GetFlow(Manager));
    }

    [Fact]
    public async Task ExpiredPicker_SaysExpired_WithKeyboardRemove()
    {
        await Shared(5);
        await Text("💬 Right here");
        await Start();
        await Shared(_rig.Memory.GetFlow(Manager)!.PickerRequestIds.Max() + 1);

        var expired = Sent().Where(c => (string)c.Body["text"]! == "That picker expired").ToList();
        Assert.Equal(3, expired.Count);
        Assert.All(expired, c => Assert.True((bool)c.Body["reply_markup"]!["remove_keyboard"]!));
        var all = Sent().ToList();
        Assert.All(expired, c => Assert.Contains("➕ Add destination", Labels(all[all.IndexOf(c) + 1])));
        Assert.Empty(_h.Api.CallsTo("getChat"));
        Assert.NotEmpty(_rig.Memory.GetFlow(Manager)!.PickerRequestIds);
    }

    [Fact]
    public async Task PromptExpiry_ViaTick_RecordsAFailure()
    {
        AddChannel();
        await Start();
        var request = ChannelRequest();

        _h.Time.Advance(TimeSpan.FromMinutes(10));
        await _rig.Flow.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal([SetupFailureKind.PromptExpired], _h.State.Read(s => s.SetupFailures.Select(f => f.Kind).ToList()));
        Assert.Empty(_rig.Memory.GetFlow(Manager)!.PickerRequestIds);

        await Shared(request);
        Assert.Equal("That picker expired", (string)Sent().SkipLast(1).Last().Body["text"]!);
        Assert.Empty(_h.Settings.Current.Destinations);
    }

    [Fact]
    public async Task GroupWithoutPhotos_DoneMentionsPhotosBlocked()
    {
        _h.Api.AddChat(Group, "supergroup", "Family", permissionsJson: NoPhotos);
        _h.Api.SetMember(Group, FakeBotApi.Member("member"));
        await Start();

        await Shared(GroupRequest(), Group);
        Assert.Equal("1", _rig.Memory.GetFlow(Manager)!.Params["photosBlocked"]);
        await Click("1|op|NewMedia", WizardId());

        var destination = Assert.Single(_h.Settings.Current.Destinations);
        Assert.Equal(ChatKind.Supergroup, destination.Kind);
        Assert.True(_rig.Health.Get(destination.Id).PhotosBlocked);
        var done = (string)_h.Api.CallsTo("editMessageText").Last().Body["text"]!;
        Assert.StartsWith("✅ Done: <b>Family</b> will receive <i>new media</i>.", done, StringComparison.Ordinal);
        Assert.Contains(_h.Strings.Get("check.photosBlocked"), done, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostileTitle_IsEscaped()
    {
        _h.Api.AddChat(Channel, "channel", "<Tom & Jerry>");
        _h.Api.SetMember(Channel, BotTestHarness.AdminCanPost);
        await Start();

        var shared = TelegramFixtures.Update("chat_shared", ("request_id", ChannelRequest().ToString(System.Globalization.CultureInfo.InvariantCulture)));
        shared.Message!.ChatShared!.Title = "<Tom & Jerry>";
        await _rig.Deliver(shared);

        Assert.Equal("Checking <b>&lt;Tom &amp; Jerry&gt;</b>…", (string)Sent().Skip(1).First().Body["text"]!);
    }

    private void AddChannel()
    {
        _h.Api.AddChat(Channel, "channel", "Family");
        _h.Api.SetMember(Channel, BotTestHarness.AdminCanPost);
    }

    private Task Start() => Text("/start add");

    private int ChannelRequest() => int.Parse(_rig.Memory.GetFlow(Manager)!.Params["requestChannel"], System.Globalization.CultureInfo.InvariantCulture);

    private int GroupRequest() => int.Parse(_rig.Memory.GetFlow(Manager)!.Params["requestGroup"], System.Globalization.CultureInfo.InvariantCulture);

    private int WizardId() => _rig.Memory.GetFlow(Manager)!.WizardMessageId!.Value;

    private Task Shared(int requestId, long chatId = Channel)
        => _rig.Deliver(TelegramFixtures.Update(
            "chat_shared",
            ("request_id", requestId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("chat_id", chatId.ToString(System.Globalization.CultureInfo.InvariantCulture))));

    private Task Click(string data, int messageId)
        => _rig.Deliver(TelegramFixtures.Update("callback_query", ("data", data), ("message_id", messageId.ToString(System.Globalization.CultureInfo.InvariantCulture))));

    private Task Text(string text)
        => _rig.Deliver(TelegramFixtures.Update("private_text", ("text", System.Text.Json.JsonEncodedText.Encode(text).ToString())));

    /// <summary>The messages sent to a chat (the manager's private chat by default).</summary>
    private BotApiCall[] Sent(long chatId = Manager)
        => [.. _h.Api.CallsTo("sendMessage").Where(c => c.Body["chat_id"]!.ToString() == chatId.ToString(System.Globalization.CultureInfo.InvariantCulture))];

    private int[] Deleted() => [.. _h.Api.CallsTo("deleteMessage").Select(c => (int)c.Body["message_id"]!)];

    private static string[] Labels(BotApiCall call) => [.. Buttons(call).Select(b => (string)b["text"]!)];

    private static string[] Data(BotApiCall call) => [.. Buttons(call).Select(b => (string)b["callback_data"]!)];

    private static JsonNode[] Buttons(BotApiCall call)
        => [.. call.Body["reply_markup"]!["inline_keyboard"]!.AsArray().SelectMany(r => r!.AsArray()).Select(b => b!)];
}
