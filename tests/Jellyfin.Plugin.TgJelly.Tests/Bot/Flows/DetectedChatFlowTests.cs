using System;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Bot.Flows;

public sealed class DetectedChatFlowTests : IDisposable, IAsyncLifetime
{
    private const long Manager = 42;
    private const long NewGroup = -1005550002;
    private readonly BotTestHarness _h = new();
    private BotRig _rig = null!;

    public async ValueTask InitializeAsync() => _rig = await BotRig.CreateAsync(_h);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task BotAddedToAGroup_IsRecorded_ShownAfter30s()
    {
        await Added(NewGroup);

        Assert.Empty(Visible());
        _h.Time.Advance(TimeSpan.FromSeconds(29));
        Assert.Empty(Visible());
        _h.Time.Advance(TimeSpan.FromSeconds(1));
        var detected = Assert.Single(Visible());
        Assert.Equal((NewGroup, "Family", ChatKind.Supergroup), (detected.ChatId, detected.Title, detected.Kind));

        await Text("/menu");
        var menu = Sent().Last();
        Assert.Contains("Chats added outside setup (1)", Labels(menu));
        Assert.Contains("1|xl|0", Data(menu));

        await Click("1|xl|0", 55);

        var list = Assert.Single(_h.Api.CallsTo("editMessageText"));
        Assert.Contains("Detected: <b>Family</b>, set it up?", (string)list.Body["text"]!, StringComparison.Ordinal);
        Assert.Equal(["Set up Family", "Dismiss Family", "Back"], Labels(list));
        Assert.Equal(["1|xs|-1005550002", "1|xd|-1005550002", "1|m"], Data(list));
    }

    [Fact]
    public async Task ChannelAdded_IsRecordedWithItsKind()
    {
        await Deliver("my_chat_member_channel_admin", ("chat_id", "-1003"));
        _h.Time.Advance(TimeSpan.FromSeconds(30));

        var detected = Assert.Single(Visible());
        Assert.Equal((-1003L, "News", ChatKind.Channel), (detected.ChatId, detected.Title, detected.Kind));
    }

    [Fact]
    public async Task AddMatchingAPrompt_IsNotRecorded()
    {
        await Text("/start add");

        await Added(NewGroup);

        _h.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(Visible());
        Assert.False(_rig.Memory.IsDetected(NewGroup));
    }

    [Fact]
    public async Task AddByAnotherUser_WhileAPromptWaits_IsRecorded()
    {
        await Text("/start add");

        await Deliver("my_chat_member_group_member", ("chat_id", Num(NewGroup)), ("user_id", "99"));

        _h.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(NewGroup, Assert.Single(Visible()).ChatId);
    }

    [Fact]
    public async Task AfterThePromptIsCancelled_AChatIsRecordedAgain()
    {
        await Text("/start add");
        await Click("1|on", PromptId());

        await Added(NewGroup);

        _h.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Single(Visible());
    }

    [Fact]
    public async Task BecomesADestinationWithin30s_NeverShown()
    {
        await Added(NewGroup);
        _h.AddDestination(NewGroup);

        _h.Time.Advance(TimeSpan.FromMinutes(1));

        Assert.Empty(_rig.Memory.VisibleDetected(_h.Settings.Current.Destinations.Select(d => d.ChatId).ToArray()));
        await Text("/menu");
        Assert.DoesNotContain(Labels(Sent().Last()), l => l.StartsWith("Chats added outside setup", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AlreadyADestination_IsNotRecorded()
    {
        _h.AddDestination(NewGroup);

        await Added(NewGroup);

        _h.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(Visible());
    }

    [Fact]
    public async Task Kicked_RemovesIt()
    {
        await Added(NewGroup);
        await Deliver("my_chat_member_kicked", ("chat_id", Num(NewGroup)));

        _h.Time.Advance(TimeSpan.FromMinutes(1));

        Assert.Empty(Visible());
    }

    [Fact]
    public async Task SetUp_ChecksOnce_ThenAsksForThePreset()
    {
        await Added(NewGroup);
        _h.Api.AddChat(NewGroup, "supergroup", "Family");
        _h.Api.SetMember(NewGroup, BotTestHarness.AdminCanPost);
        _h.Time.Advance(TimeSpan.FromSeconds(30));

        await Click("1|xs|-1005550002", 55);

        Assert.Single(_h.Api.CallsTo("getChatMember"));
        Assert.Empty(_rig.Delays);
        var wizard = Assert.Single(_h.Api.CallsTo("editMessageText"));
        Assert.Equal(55, (int)wizard.Body["message_id"]!);
        Assert.Contains("What should I send there?", (string)wizard.Body["text"]!, StringComparison.Ordinal);
        Assert.Equal(["Everything", "New media", "Admin alerts", "Custom…"], Labels(wizard));

        await Click("1|op|Everything", 55);

        var destination = Assert.Single(_h.Settings.Current.Destinations);
        Assert.Equal((NewGroup, "Family"), (destination.ChatId, destination.Title));
        Assert.Empty(_rig.Memory.VisibleDetected([]));
    }

    [Fact]
    public async Task SetUp_OfAForum_AsksForTheTopic()
    {
        await Added(NewGroup);
        _h.Api.AddChat(NewGroup, "supergroup", "Family", isForum: true);
        _h.Api.SetMember(NewGroup, BotTestHarness.AdminCanPost);
        _h.Time.Advance(TimeSpan.FromSeconds(30));

        await Click("1|xs|-1005550002", 55);

        Assert.Contains("General topic", Labels(Assert.Single(_h.Api.CallsTo("editMessageText"))));
    }

    [Fact]
    public async Task SetUp_FailingCheck_ExplainsAndKeepsIt()
    {
        await Added(NewGroup);
        _h.Api.AddChat(NewGroup, "supergroup", "Family");
        _h.Api.SetMember(NewGroup, FakeBotApi.Member("left"));
        _h.Time.Advance(TimeSpan.FromSeconds(30));

        await Click("1|xs|-1005550002", 55);

        Assert.Equal(["Try again", "Advanced options"], Labels(Assert.Single(_h.Api.CallsTo("editMessageText"))));
        Assert.Empty(_h.Settings.Current.Destinations);
        Assert.Single(Visible());
    }

    [Fact]
    public async Task SetUp_OfAChatNoLongerDetected_SaysTheMenuExpired()
    {
        await Click("1|xs|-1005550002", 55);

        Assert.Contains(Sent(), c => ((string)c.Body["text"]!).Contains("That menu expired", StringComparison.Ordinal));
        Assert.Empty(_h.Api.CallsTo("getChat"));
    }

    [Fact]
    public async Task DetectedList_ExplainsSetUpAndDismiss_Once()
    {
        await Added(NewGroup);
        await Added(-1005550003);
        _h.Time.Advance(TimeSpan.FromSeconds(30));

        await Click("1|xl|0", 55);

        var text = (string)Assert.Single(_h.Api.CallsTo("editMessageText")).Body["text"]!;
        Assert.Equal(1, Count(text, "<b>Set up …</b> — add the chat as a destination"));
        Assert.Equal(1, Count(text, "<b>Dismiss …</b> — hide it from this list; I stay in the chat"));
    }

    private static int Count(string text, string part) => text.Split(part).Length - 1;

    [Fact]
    public async Task Dismiss_RemovesIt()
    {
        await Added(NewGroup);
        await Added(-1005550003);
        _h.Time.Advance(TimeSpan.FromSeconds(30));

        await Click("1|xd|-1005550002", 55);

        var remaining = Assert.Single(Visible());
        Assert.Equal(-1005550003, remaining.ChatId);
        Assert.Equal(["1|xs|-1005550003", "1|xd|-1005550003", "1|m"], Data(Assert.Single(_h.Api.CallsTo("editMessageText"))));
    }

    [Fact]
    public async Task Dismiss_OfTheLastOne_ShowsTheMenu()
    {
        await Added(NewGroup);
        _h.Time.Advance(TimeSpan.FromSeconds(30));

        await Click("1|xd|-1005550002", 55);

        Assert.Empty(Visible());
        var menu = Assert.Single(_h.Api.CallsTo("editMessageText"));
        Assert.Contains("Menu", (string)menu.Body["text"]!, StringComparison.Ordinal);
        Assert.Contains("1|dl|0", Data(menu));
    }

    [Fact]
    public async Task TheList_IsPaged()
    {
        for (var i = 0; i < 9; i++)
        {
            await Added(-1005550100 - i);
        }

        _h.Time.Advance(TimeSpan.FromSeconds(30));

        await Click("1|xl|0", 55);
        var first = Assert.Single(_h.Api.CallsTo("editMessageText"));
        Assert.Equal(8, Data(first).Count(d => d.StartsWith("1|xs|", StringComparison.Ordinal)));
        Assert.Contains("1|xl|1", Data(first));

        await Click("1|xl|1", 55);
        var second = _h.Api.CallsTo("editMessageText").Last();
        Assert.Single(Data(second), d => d.StartsWith("1|xs|", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OnboardingMenus_CountDetectedChats()
    {
        await Added(NewGroup);
        _h.Time.Advance(TimeSpan.FromSeconds(30));
        await Text("/start add");
        var prompt = PromptId();

        await Click("1|on", prompt);

        Assert.Contains("Chats added outside setup (1)", Labels(_h.Api.CallsTo("editMessageText").Last()));
    }

    [Fact]
    public async Task PrivateBlockAndUnblock_UpdateManagers()
    {
        await Text("/menu");
        Assert.Equal([Manager], _rig.Memory.RecentManagers());

        await Deliver("my_chat_member_private_blocked");
        Assert.Empty(_rig.Memory.RecentManagers());

        await Deliver("my_chat_member_private_unblocked");
        Assert.Equal([Manager], _rig.Memory.RecentManagers());
    }

    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    private System.Collections.Generic.IReadOnlyList<Jellyfin.Plugin.TgJelly.State.Documents.DetectedChat> Visible() => _rig.Memory.VisibleDetected([]);

    private int PromptId() => _rig.Memory.GetFlow(Manager)!.WizardMessageId!.Value;

    private Task Added(long chatId) => Deliver("my_chat_member_group_member", ("chat_id", Num(chatId)));

    private Task Deliver(string fixture, params (string Placeholder, string Value)[] values) => _rig.Deliver(TelegramFixtures.Update(fixture, values));

    private Task Click(string data, int messageId)
        => _rig.Deliver(TelegramFixtures.Update("callback_query", ("data", data), ("message_id", Num(messageId))));

    private Task Text(string text)
        => _rig.Deliver(TelegramFixtures.Update("private_text", ("text", System.Text.Json.JsonEncodedText.Encode(text).ToString())));

    private BotApiCall[] Sent(long chatId = Manager)
        => [.. _h.Api.CallsTo("sendMessage").Where(c => c.Body["chat_id"]!.ToString() == Num(chatId))];

    private static string[] Labels(BotApiCall call) => [.. Buttons(call).Select(b => (string)b["text"]!)];

    private static string[] Data(BotApiCall call) => [.. Buttons(call).Select(b => (string)b["callback_data"]!)];

    private static JsonNode[] Buttons(BotApiCall call)
        => [.. call.Body["reply_markup"]!["inline_keyboard"]!.AsArray().SelectMany(r => r!.AsArray()).Select(b => b!)];
}
