using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Bot.Flows;
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
    private const string Prompt = "Where should notifications go?";
    private const string NoPhotos = """{"can_send_messages":true,"can_send_photos":false}""";
    private const string ChannelAdd = "my_chat_member_channel_admin";
    private const string GroupAdd = "my_chat_member_group_member";
    private static readonly string[] Presets = ["Everything", "New media", "Admin alerts", "Custom…"];
    private readonly BotTestHarness _h = new();
    private BotRig _rig = null!;

    public async ValueTask InitializeAsync() => _rig = await BotRig.CreateAsync(_h);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Start_SendsAnInlinePrompt_WithAddLinks()
    {
        await Start();

        var prompt = Assert.Single(Sent());
        var text = (string)prompt.Body["text"]!;
        Assert.StartsWith(Prompt + "\nAfter you choose a chat in Telegram, I'll continue here.\n\n", text, StringComparison.Ordinal);
        Assert.Equal(["Add to a channel", "Add to a group", "Right here", "Cancel"], Labels(prompt));
        var buttons = Buttons(prompt);
        Assert.Equal("https://t.me/tgjelly_test_bot?startchannel&admin=post_messages+edit_messages", (string)buttons[0]["url"]!);
        Assert.Equal("https://t.me/tgjelly_test_bot?startgroup=add", (string)buttons[1]["url"]!);
        Assert.Equal(["1|oh", "1|on"], buttons.Select(b => (string?)b["callback_data"]).OfType<string>());
        Assert.Null(prompt.Body["reply_markup"]!["keyboard"]);

        prompt.Explains("Add to a channel", "Add to a group", "Right here", "Cancel");
        var order = new[] { "Add to a channel", "Add to a group", "Right here", "Cancel" }.Select(l => text.IndexOf("<b>" + l + "</b> — ", StringComparison.Ordinal)).ToList();
        Assert.Equal(order.Order(), order);
        Assert.DoesNotContain("Set up", text, StringComparison.Ordinal);

        var flow = _rig.Memory.GetFlow(Manager)!;
        Assert.Equal(1, flow.WizardMessageId);
        Assert.Equal(_h.Time.GetUtcNow(), flow.PromptSentAt);
        Assert.Equal("chat", flow.Params["await"]);
        Assert.True(OnboardingFlow.AwaitsChat(flow));
    }

    [Theory]
    [InlineData("/start setup")]
    [InlineData("/start add")]
    [InlineData("/start")]
    public async Task StartCommands_SendThePrompt(string text)
    {
        await Text(text);

        Assert.StartsWith(Prompt, (string)Assert.Single(Sent()).Body["text"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartFromAButton_EditsThatMessage()
    {
        _h.AddDestination(-1001);

        await Click("1|oa", 7);

        var edit = Assert.Single(_h.Api.CallsTo("editMessageText"));
        Assert.Equal(7, (int)edit.Body["message_id"]!);
        Assert.StartsWith(Prompt, (string)edit.Body["text"]!, StringComparison.Ordinal);
        Assert.Empty(_h.Api.CallsTo("sendMessage"));
        Assert.Equal(7, _rig.Memory.GetFlow(Manager)!.WizardMessageId);
    }

    [Fact]
    public async Task Start_ListsUpToThreeDetectedChats()
    {
        for (var i = 0; i < 4; i++)
        {
            _rig.Memory.RecordDetected(-1007770000 - i, "Chat " + Num(i), ChatKind.Supergroup);
            _h.Time.Advance(TimeSpan.FromSeconds(1));
        }

        _h.Time.Advance(BotMemory.DetectedGrace);

        await Start();

        var prompt = Assert.Single(Sent());
        var labels = Labels(prompt);
        Assert.Equal(["Add to a channel", "Add to a group", "Right here", "Set up Chat 0", "Set up Chat 1", "Set up Chat 2", "Cancel"], labels);
        Assert.Equal(["1|oh", "1|xs|-1007770000", "1|xs|-1007770001", "1|xs|-1007770002", "1|on"], Buttons(prompt).Select(b => (string?)b["callback_data"]).OfType<string>());
        var text = (string)prompt.Body["text"]!;
        const string line = "<b>Set up …</b> — a chat I'm already in that isn't set up yet";
        Assert.Equal(1, Occurrences(text, line));
        Assert.True(text.IndexOf("<b>Right here</b>", StringComparison.Ordinal) < text.IndexOf(line, StringComparison.Ordinal));
        Assert.True(text.IndexOf(line, StringComparison.Ordinal) < text.IndexOf("<b>Cancel</b>", StringComparison.Ordinal));
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
    public async Task ChannelAdded_ByThePromptsUser_ChecksAndShowsPresets_InTheSameMessage()
    {
        AddChannel();
        await Start();
        var prompt = WizardId();

        await Added(ChannelAdd, Channel);

        var edits = Edits(prompt);
        Assert.Equal("Checking <b>News</b>…", (string)edits[0].Body["text"]!);
        var presets = edits.Last();
        Assert.Equal(Presets, Labels(presets));
        Assert.StartsWith("<b>Family</b>\nWhat should I send there?", (string)presets.Body["text"]!, StringComparison.Ordinal);
        Assert.Single(Sent());
        Assert.Empty(Deleted());
        Assert.False(_rig.Memory.IsDetected(Channel));

        var flow = _rig.Memory.GetFlow(Manager)!;
        Assert.Equal(prompt, flow.WizardMessageId);
        Assert.Null(flow.PromptSentAt);
        Assert.False(flow.Params.ContainsKey("await"));
        Assert.Equal(Num(Channel), flow.Params["chatId"]);
        Assert.Equal("Family", flow.Params["title"]);
        Assert.Equal(nameof(ChatKind.Channel), flow.Params["kind"]);
        Assert.Equal("0", flow.Params["threadId"]);
        Assert.Empty(_rig.Delays);
    }

    [Fact]
    public async Task GroupAdded_ByThePromptsUser_ContinuesTheWizard()
    {
        _h.Api.AddChat(Group, "supergroup", "Family");
        _h.Api.SetMember(Group, FakeBotApi.Member("member"));
        await Start();
        var prompt = WizardId();

        await Added(GroupAdd, Group);

        var edits = Edits(prompt);
        Assert.Equal("Checking <b>Family</b>…", (string)edits[0].Body["text"]!);
        Assert.Equal(Presets, Labels(edits.Last()));
        Assert.Single(Sent());
        Assert.Empty(Sent(Group));
        Assert.False(_rig.Memory.IsDetected(Group));
    }

    [Fact]
    public async Task RightsGranted_InAChannelTheBotIsAlreadyIn_CountsAsTheChoice()
    {
        AddChannel();
        await Start();
        var prompt = WizardId();

        await Added("my_chat_member_channel_rights", Channel);

        Assert.Equal(Presets, Labels(Edits(prompt).Last()));
        Assert.False(_rig.Memory.IsDetected(Channel));
    }

    [Fact]
    public async Task GroupStart_ByThePromptsUser_CountsAsTheChoice()
    {
        _h.Api.AddChat(Group, "supergroup", "Family");
        _h.Api.SetMember(Group, FakeBotApi.Member("member"));
        await Start();
        var prompt = WizardId();

        await GroupStart(Group, "/start@tgjelly_test_bot add");

        Assert.Equal("Checking <b>Family</b>…", (string)Edits(prompt)[0].Body["text"]!);
        Assert.Equal(Presets, Labels(Edits(prompt).Last()));
        Assert.Empty(Sent(Group));
    }

    [Fact]
    public async Task GroupStart_WithoutAPrompt_DoesNothing()
    {
        _h.Api.AddChat(Group, "supergroup", "Family");

        await GroupStart(Group, "/start@tgjelly_test_bot add");

        Assert.DoesNotContain(_h.Api.Calls, c => c.Method is "sendMessage" or "editMessageText" or "getChat");
    }

    [Fact]
    public async Task AddedByAnotherUser_IsADetectedChat_AndTheWizardWaits()
    {
        AddChannel();
        await Start();
        var prompt = WizardId();

        await Added(ChannelAdd, Channel, userId: 99);

        Assert.True(_rig.Memory.IsDetected(Channel));
        Assert.True(OnboardingFlow.AwaitsChat(_rig.Memory.GetFlow(Manager)!));
        Assert.Empty(Edits(prompt));
    }

    [Fact]
    public async Task RedeliveredAdd_IsHandledOnce()
    {
        AddChannel();
        await Start();
        var update = TelegramFixtures.Update(ChannelAdd, ("chat_id", Num(Channel)));

        await _rig.Deliver(update);
        await _rig.Deliver(update);

        Assert.Single(_h.Api.CallsTo("editMessageText"), c => ((string)c.Body["text"]!).StartsWith("Checking", StringComparison.Ordinal));
        Assert.Single(_h.Api.CallsTo("getChat"), c => c.Body["chat_id"]!.ToString() == Num(Channel));
        Assert.False(_rig.Memory.IsDetected(Channel));

        var wizard = WizardId();
        await Click("1|op|Everything", wizard);
        await Click("1|op|Everything", wizard);

        Assert.Single(_h.Settings.Current.Destinations);
        Assert.Single(Sent(Channel));
    }

    [Fact]
    public async Task TwoManagers_EachGetOnlyTheirOwnAdd()
    {
        _h.Api.AddChat(Group, "supergroup", "Family");
        _h.Api.SetMember(Group, FakeBotApi.Member("member"));
        await Start();
        await Text("/start add", userId: 43);
        var first = WizardId();
        var second = _rig.Memory.GetFlow(43)!.WizardMessageId!.Value;

        await Added(GroupAdd, Group, userId: 43);

        Assert.Equal(Presets, Labels(Edits(second).Last()));
        Assert.Equal("43", Edits(second).Last().Body["chat_id"]!.ToString());
        Assert.True(OnboardingFlow.AwaitsChat(_rig.Memory.GetFlow(Manager)!));
        Assert.Empty(Edits(first));
        Assert.False(_rig.Memory.IsDetected(Group));
    }

    [Fact]
    public async Task AddedBeforeTheRightsSettle_RetriesUntilThePass()
    {
        _h.Api.AddChat(Channel, "channel", "Family");
        _h.Api.OnSequence("getChatMember", FakeBotApi.Ok(FakeBotApi.Member("left")), FakeBotApi.Ok(FakeBotApi.Member("left")), FakeBotApi.Ok(BotTestHarness.AdminCanPost));
        await Start();

        await Added(ChannelAdd, Channel);

        Assert.Equal([TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(1.5)], _rig.Delays);
        Assert.Equal(3, _h.Api.CallsTo("getChatMember").Count());
        Assert.Equal(Presets, Labels(_h.Api.CallsTo("editMessageText").Last()));
    }

    [Fact]
    public async Task UnansweredPrompt_Expires_AndSaysSo()
    {
        AddChannel();
        await Start();
        var prompt = WizardId();

        _h.Time.Advance(BotMemory.PromptTtl);
        await _rig.Flow.TickAsync(TestContext.Current.CancellationToken);

        var expired = Assert.Single(Edits(prompt));
        Assert.StartsWith("That setup prompt expired.", (string)expired.Body["text"]!, StringComparison.Ordinal);
        Assert.Equal(["1|oa"], Data(expired));
        expired.Explains("Add destination");
        Assert.Null(_rig.Memory.GetFlow(Manager));
        Assert.Equal([SetupFailureKind.PromptExpired], _h.State.Read(s => s.SetupFailures.Select(f => f.Kind).ToList()));

        await Added(ChannelAdd, Channel);

        Assert.True(_rig.Memory.IsDetected(Channel));
        Assert.Single(Edits(prompt));
        Assert.Empty(_h.Settings.Current.Destinations);
    }

    [Fact]
    public async Task RightHere_ChecksThisChat_InTheSameMessage()
    {
        _h.Api.AddChat(Manager, "private", "Ann");
        await Start();
        var prompt = WizardId();

        await Click("1|oh", prompt);

        var edits = Edits(prompt);
        Assert.Equal("Checking this chat…", (string)edits[0].Body["text"]!);
        Assert.Equal(Presets, Labels(edits.Last()));
        Assert.Single(Sent());
        Assert.Empty(_h.Api.CallsTo("getChatMember"));

        await Click("1|op|AdminAlerts", prompt);

        var destination = Assert.Single(_h.Settings.Current.Destinations);
        Assert.Equal(Manager, destination.ChatId);
        Assert.Equal(ChatKind.Private, destination.Kind);
        Assert.Equal(PresetKind.AdminAlerts, destination.Preset);
        Assert.Contains(Sent(), c => ((string)c.Body["text"]!).Contains("TgJelly test", StringComparison.Ordinal));
        Assert.StartsWith("✅ Done: <b>Ann</b>", (string)Edits(prompt).Last().Body["text"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RightHere_OnAnOldPrompt_IsExpired()
    {
        await Start();
        var first = WizardId();
        await Start();
        var second = WizardId();

        Assert.Equal([first], Deleted());

        await Click("1|oh", first);

        Assert.Contains(Sent(), c => (string)c.Body["text"]! == "That menu expired");
        Assert.Contains("Add destination", Labels(Sent().Last()));
        Assert.Empty(_h.Api.CallsTo("getChat"));
        var flow = _rig.Memory.GetFlow(Manager)!;
        Assert.Equal(second, flow.WizardMessageId);
        Assert.True(OnboardingFlow.AwaitsChat(flow));
    }

    [Fact]
    public async Task Cancel_ShowsTheMenuInPlace()
    {
        await Start();
        var prompt = WizardId();

        await Click("1|on", prompt);

        var menu = Assert.Single(Edits(prompt));
        Assert.Contains("<tg-emoji emoji-id=\"5875271289605722323\">🏠</tg-emoji> Menu", (string)menu.Body["text"]!, StringComparison.Ordinal);
        Assert.Null(_rig.Memory.GetFlow(Manager));
        Assert.Single(Sent());
        Assert.DoesNotContain(_h.Api.Calls, c => c.Body["reply_markup"]?["remove_keyboard"] is not null);
    }

    [Theory]
    [InlineData("📢 Choose a channel")]
    [InlineData("👥 Choose a group")]
    [InlineData("💬 Right here")]
    [InlineData("Cancel")]
    [InlineData("chat_shared")]
    public async Task LegacyReplyInput_RemovesTheOldKeyboard_AndShowsTheMenu(string input)
    {
        if (input == "chat_shared")
        {
            await _rig.Deliver(TelegramFixtures.Update("chat_shared", ("request_id", "5")));
        }
        else
        {
            Assert.Contains(input, OnboardingFlow.LegacyReplyTexts);
            await Text(input);
        }

        var sent = Sent();
        Assert.Equal(2, sent.Length);
        Assert.Equal("That menu expired", (string)sent[0].Body["text"]!);
        Assert.True((bool)sent[0].Body["reply_markup"]!["remove_keyboard"]!);
        Assert.Contains("Add destination", Labels(sent[1]));
        Assert.Single(_h.Api.Calls, c => c.Body["reply_markup"]?["remove_keyboard"] is not null);
        Assert.Empty(_h.Api.CallsTo("getChat"));
    }

    [Fact]
    public async Task CheckFails_ExplainsInTheWizard_WithTryAgainAndAdvanced()
    {
        _h.Api.AddChat(Channel, "channel", "Family");
        _h.Api.SetMember(Channel, FakeBotApi.Member("member"));
        await Start();
        var prompt = WizardId();

        await Added(ChannelAdd, Channel);

        Assert.Equal(3, _rig.Delays.Count);
        var failure = Edits(prompt).Last();
        Assert.StartsWith(_h.Strings.Get("check.channelNotAdmin"), (string)failure.Body["text"]!, StringComparison.Ordinal);
        Assert.Equal(["Try again", "Advanced options"], Labels(failure));
        Assert.Equal(["1|ot", "1|ox"], Data(failure));
        failure.Explains("Try again", "Advanced options");
        Assert.Equal([SetupFailureKind.CheckFailed], _h.State.Read(s => s.SetupFailures.Select(f => f.Kind).ToList()));
        Assert.Single(Sent());
        Assert.Empty(Deleted());
        Assert.Empty(_h.Settings.Current.Destinations);
    }

    [Fact]
    public async Task TryAgain_AfterAFailedChat_RechecksThatChat()
    {
        _h.Api.AddChat(Channel, "channel", "Family");
        _h.Api.SetMember(Channel, FakeBotApi.Member("member"));
        await Start();
        var prompt = WizardId();
        await Added(ChannelAdd, Channel);
        var checks = _h.Api.CallsTo("getChatMember").Count();
        var delays = _rig.Delays.Count;
        _h.Api.SetMember(Channel, BotTestHarness.AdminCanPost);

        await Click("1|ot", prompt);

        var edits = Edits(prompt);
        Assert.StartsWith("Checking <b>", (string)edits[^2].Body["text"]!, StringComparison.Ordinal);
        Assert.Equal(Presets, Labels(edits[^1]));
        Assert.Single(Sent());
        Assert.Equal(checks + 1, _h.Api.CallsTo("getChatMember").Count());
        Assert.Equal(delays, _rig.Delays.Count);
        Assert.Equal(Num(Channel), _rig.Memory.GetFlow(Manager)!.Params["chatId"]);
    }

    [Fact]
    public async Task TryAgain_AfterRightHereFails_RechecksThisChat()
    {
        var broken = true;
        _h.Api.On("getChat", _ => broken
            ? FakeBotApi.Error(400, "Bad Request: chat not found")
            : FakeBotApi.Ok("""{"id":42,"type":"private","first_name":"Ann","accent_color_id":0,"max_reaction_count":11}"""));
        await Start();
        var prompt = WizardId();

        await Click("1|oh", prompt);

        Assert.Equal(["1|ot", "1|ox"], Data(Edits(prompt).Last()));

        broken = false;
        await Click("1|ot", prompt);

        var edits = Edits(prompt);
        Assert.Equal("Checking this chat…", (string)edits[^2].Body["text"]!);
        Assert.Equal(Presets, Labels(edits[^1]));
        Assert.Single(Sent());
    }

    [Fact]
    public async Task TryAgain_WithoutAFailedChat_SendsAFreshPromptInPlace()
    {
        _rig.Memory.SaveFlow(Manager, new FlowState { Screen = "onboard", WizardMessageId = 77 });

        await Click("1|ot", 77);

        var edit = Assert.Single(Edits(77));
        Assert.StartsWith(Prompt, (string)edit.Body["text"]!, StringComparison.Ordinal);
        Assert.Empty(Sent());
        var flow = _rig.Memory.GetFlow(Manager)!;
        Assert.Equal(77, flow.WizardMessageId);
        Assert.True(OnboardingFlow.AwaitsChat(flow));
    }

    [Fact]
    public async Task AdvancedOptions_ShowsTheAdvancedScreen()
    {
        await Click("1|ox", 77);

        var edit = Assert.Single(_h.Api.CallsTo("editMessageText"));
        Assert.Equal(77, (int)edit.Body["message_id"]!);
        Assert.Contains("Advanced options", (string)edit.Body["text"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TransportErrorDuringCheck_ReportsFailure()
    {
        AddChannel();
        await Start();
        var prompt = WizardId();
        _h.Api.On("getChat", _ => throw new HttpRequestException("connection reset"));

        await Added(ChannelAdd, Channel);

        var failure = Edits(prompt).Last();
        Assert.StartsWith(_h.Strings.Get("token.unreachable"), (string)failure.Body["text"]!, StringComparison.Ordinal);
        Assert.Equal(["Try again", "Advanced options"], Labels(failure));
        Assert.True(_rig.Memory.HasRecentFailure());

        await Text("/menu");
        Assert.Contains("<tg-emoji emoji-id=\"5875271289605722323\">🏠</tg-emoji> Menu", (string)Sent().Last().Body["text"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preset_Creates_SendsTheTest_AndEditsTheWizardToDone()
    {
        await ChooseChannel();
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
        Assert.StartsWith("✅ Done: <b>Family</b> will receive <i>new media, security alerts, server alerts</i>.", (string)done.Body["text"]!, StringComparison.Ordinal);
        done.Explains("Add another place", "Menu");
        Assert.Contains("<b>Add another place</b> — set up one more chat", (string)done.Body["text"]!, StringComparison.Ordinal);
        Assert.Contains("<b>Menu</b> — back to the main menu", (string)done.Body["text"]!, StringComparison.Ordinal);
        Assert.Equal(["Add another place", "Menu"], Labels(done));
        Assert.Equal(["1|oa", "1|m"], Data(done));
        Assert.Null(_rig.Memory.GetFlow(Manager));
    }

    [Fact]
    public async Task SecondPresetClick_AfterDone_SaysExpired()
    {
        await ChooseChannel();
        var wizard = WizardId();
        await Click("1|op|Everything", wizard);

        await Click("1|op|Everything", wizard);

        Assert.Single(_h.Settings.Current.Destinations);
        Assert.Single(Sent(Channel));
        Assert.Equal("That menu expired", (string)Sent().SkipLast(1).Last().Body["text"]!);
    }

    [Fact]
    public async Task Preset_ReChecksFirst_AndReportsAChatThatChanged()
    {
        await ChooseChannel();
        _h.Api.SetMember(Channel, FakeBotApi.Member("left"));

        await Click("1|op|Everything", WizardId());

        Assert.Empty(_h.Settings.Current.Destinations);
        var failure = _h.Api.CallsTo("editMessageText").Last();
        Assert.StartsWith(_h.Strings.Get("check.lost"), (string)failure.Body["text"]!, StringComparison.Ordinal);
        Assert.Equal(["Try again", "Advanced options"], Labels(failure));
        Assert.True(_rig.Memory.HasRecentFailure());
        Assert.Equal(Num(Channel), _rig.Memory.GetFlow(Manager)!.Params["chatId"]);
    }

    [Fact]
    public async Task FailedTestSend_PutsItsErrorUnderTheDoneLine()
    {
        await ChooseChannel();
        _h.Api.On("sendMessage", call => call.Body["chat_id"]!.ToString() == Num(Channel)
            ? FakeBotApi.Error(400, "Bad Request: not enough rights to send text messages to the chat")
            : FakeBotApi.Ok("""{"message_id":900,"date":1790000000,"chat":{"id":42,"type":"private"},"text":"x"}"""));

        await Click("1|op|NewMedia", WizardId());

        var done = (string)_h.Api.CallsTo("editMessageText").Last().Body["text"]!;
        Assert.StartsWith("✅ Done: <b>Family</b> will receive <i>new media</i>.\n", done, StringComparison.Ordinal);
        Assert.Contains("❌ Test failed: Bad Request: not enough rights", done, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Custom_OpensTheGroups_WithDone()
    {
        await ChooseChannel();
        var wizard = WizardId();

        await Click("1|op|Custom", wizard);

        var groups = _h.Api.CallsTo("editMessageText").Last();
        Assert.Equal(wizard, (int)groups.Body["message_id"]!);
        Assert.Equal(["Media 0/5", "Security 0/6", "Server 0/8", "Done"], Labels(groups));
        Assert.Equal(["1|ocg|media", "1|ocg|security", "1|ocg|server", "1|ocd"], Data(groups));
        groups.Explains("Media", "Security", "Server", "Done");
    }

    [Fact]
    public async Task PresetScreen_ExplainsEachPreset()
    {
        await ChooseChannel();

        _h.Api.CallsTo("editMessageText").Last().Explains("Everything", "New media", "Admin alerts", "Custom…");
    }

    [Fact]
    public async Task CustomGroup_SwitchesStayInTheGroup_AndBackReturns()
    {
        await ChooseChannel();
        var wizard = WizardId();
        await Click("1|op|Custom", wizard);

        await Click("1|ocg|media", wizard);
        var media = _h.Api.CallsTo("editMessageText").Last();
        Assert.Equal(6, Labels(media).Length);
        Assert.Equal(["Back"], Labels(media)[5..]);
        Assert.Equal("1|ocg", Data(media)[5]);
        Assert.EndsWith("\n\nTap an event to switch it on (✅) or off (▫️).", (string)media.Body["text"]!, StringComparison.Ordinal);

        await Click("1|oc|media.movie", wizard);
        var after = _h.Api.CallsTo("editMessageText").Last();
        Assert.Equal(6, Labels(after).Length);
        Assert.StartsWith("✅", Labels(after)[0], StringComparison.Ordinal);

        await Click("1|ocg", wizard);
        Assert.Equal("Media 1/5", Labels(_h.Api.CallsTo("editMessageText").Last())[0]);
    }

    [Fact]
    public async Task CustomDone_CreatesWithTheChosenEvents()
    {
        await ChooseChannel();
        var wizard = WizardId();
        await Click("1|op|Custom", wizard);

        await Click("1|oc|server.started", wizard);
        await Click("1|oc|media.movie", wizard);
        await Click("1|oc|security.lockout", wizard);
        await Click("1|oc|security.lockout", wizard);
        Assert.Empty(_h.Settings.Current.Destinations);
        await Click("1|ocd", wizard);

        var destination = Assert.Single(_h.Settings.Current.Destinations);
        Assert.Equal(PresetKind.Custom, destination.Preset);
        Assert.Equal(["media.movie", "server.started"], destination.Events);
        Assert.StartsWith("✅ Done: <b>Family</b> will receive <i>new media, server alerts</i>.\n\n", (string)_h.Api.CallsTo("editMessageText").Last().Body["text"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Duplicate_SaysAlreadySetUp_ReChecks_AndOpensTheDestination()
    {
        var existing = _h.AddDestination(Channel, ChatKind.Channel);
        await Start();
        var prompt = WizardId();

        await Added(ChannelAdd, Channel);

        Assert.Single(_h.Settings.Current.Destinations);
        Assert.Equal(2, _h.Api.CallsTo("getChatMember").Count());
        var screen = Edits(prompt).Last();
        Assert.StartsWith("<b>" + existing.Title + "</b> is already set up", (string)screen.Body["text"]!, StringComparison.Ordinal);
        Assert.Contains("1|de|" + existing.Id, Data(screen));
        Assert.Empty(Deleted());
        Assert.Null(_rig.Memory.GetFlow(Manager));
        Assert.Empty(Sent(Channel));
    }

    [Fact]
    public async Task GroupWithoutPhotos_DoneMentionsPhotosBlocked()
    {
        _h.Api.AddChat(Group, "supergroup", "Family", permissionsJson: NoPhotos);
        _h.Api.SetMember(Group, FakeBotApi.Member("member"));
        await Start();

        await Added(GroupAdd, Group);
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
        var prompt = WizardId();

        var update = TelegramFixtures.Update(ChannelAdd, ("chat_id", Num(Channel)));
        update.MyChatMember!.Chat.Title = "<Tom & Jerry>";
        await _rig.Deliver(update);

        Assert.Equal("Checking <b>&lt;Tom &amp; Jerry&gt;</b>…", (string)Edits(prompt)[0].Body["text"]!);
    }

    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static int Occurrences(string text, string part)
    {
        var count = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private void AddChannel()
    {
        _h.Api.AddChat(Channel, "channel", "Family");
        _h.Api.SetMember(Channel, BotTestHarness.AdminCanPost);
    }

    private Task Start() => Text("/start add");

    /// <summary>Sends the prompt and adds the bot to the channel, which leaves the wizard on the preset step.</summary>
    private async Task ChooseChannel()
    {
        AddChannel();
        await Start();
        await Added(ChannelAdd, Channel);
    }

    private int WizardId() => _rig.Memory.GetFlow(Manager)!.WizardMessageId!.Value;

    private Task Added(string fixture, long chatId, long userId = Manager)
        => _rig.Deliver(TelegramFixtures.Update(fixture, ("chat_id", Num(chatId)), ("user_id", Num(userId))));

    private Task GroupStart(long chatId, string text, long userId = Manager)
        => _rig.Deliver(TelegramFixtures.Update(
            "group_command_start",
            ("chat_id", Num(chatId)),
            ("user_id", Num(userId)),
            ("text", System.Text.Json.JsonEncodedText.Encode(text).ToString())));

    private Task Click(string data, int messageId)
        => _rig.Deliver(TelegramFixtures.Update("callback_query", ("data", data), ("message_id", Num(messageId))));

    private Task Text(string text, long userId = Manager)
        => _rig.Deliver(TelegramFixtures.Update("private_text", ("text", System.Text.Json.JsonEncodedText.Encode(text).ToString()), ("user_id", Num(userId))));

    /// <summary>The messages sent to a chat (the manager's private chat by default).</summary>
    private BotApiCall[] Sent(long chatId = Manager)
        => [.. _h.Api.CallsTo("sendMessage").Where(c => c.Body["chat_id"]!.ToString() == Num(chatId))];

    /// <summary>The edits of one message.</summary>
    private BotApiCall[] Edits(int messageId)
        => [.. _h.Api.CallsTo("editMessageText").Where(c => (int)c.Body["message_id"]! == messageId)];

    private int[] Deleted() => [.. _h.Api.CallsTo("deleteMessage").Select(c => (int)c.Body["message_id"]!)];

    private static string[] Labels(BotApiCall call) => [.. Buttons(call).Select(b => (string)b["text"]!)];

    private static string[] Data(BotApiCall call) => [.. Buttons(call).Select(b => (string)b["callback_data"]!)];

    private static JsonNode[] Buttons(BotApiCall call)
        => [.. call.Body["reply_markup"]!["inline_keyboard"]!.AsArray().SelectMany(r => r!.AsArray()).Select(b => b!)];
}
