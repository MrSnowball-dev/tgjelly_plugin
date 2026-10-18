using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Api;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Delivery;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Api;

public sealed class DestinationsControllerTests : IDisposable
{
    private const string Member = """{"status":"member","user":{"id":123456789,"is_bot":true,"first_name":"b"}}""";

    private readonly BotTestHarness _h = new();
    private readonly HealthService _health;
    private readonly Outbox _outbox;
    private readonly DestinationsController _controller;

    public DestinationsControllerTests()
    {
        _h.Connect();
        var gate = new InstanceGate(_h.Paths, _h.Time, NullLogger<InstanceGate>.Instance);
        _h.Track(gate);
        var checker = new DestinationChecker(_h.Connection, _h.Strings);
        _health = new HealthService(gate, _h.Connection, checker, _h.Settings, _h.State, _h.Time, NullLogger<HealthService>.Instance);
        _h.Track(_health);
        _outbox = new Outbox(_h.State, _h.Strings, _h.Time);
        var tester = new DestinationTester(_h.Connection, _health, _h.Strings, NullLogger<DestinationTester>.Instance);
        _controller = new DestinationsController(_h.Settings, _h.Connection, checker, _health, tester, _outbox, _h.Strings, NullLogger<DestinationsController>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AddByChatId_ChecksSavesAndSendsTheTestMessage()
    {
        _h.Api.AddChat(-1001234567890, "supergroup", "Movie night");
        _h.Api.SetMember(-1001234567890, Member);

        var response = (await _controller.Add(new AddDestinationRequest { ExpectedRevision = 0, ChatId = -1001234567890, Preset = "AdminAlerts" }, Ct)).Value!;

        Assert.Equal(("Added", true), (response.Outcome, response.TestSent));
        var saved = Assert.Single(_h.Settings.Current.Destinations);
        Assert.Equal((-1001234567890L, "Movie night", ChatKind.Supergroup, PresetKind.AdminAlerts), (saved.ChatId, saved.Title, saved.Kind, saved.Preset));
        Assert.Equal(EventCatalog.Expand(PresetKind.AdminAlerts), saved.Events);
        Assert.True(SettingsValidator.IsValidId(saved.Id));
        Assert.Equal("✅ TgJelly test: this chat will receive <i>security alerts, server alerts</i>", (string)Assert.Single(_h.Api.CallsTo("sendMessage")).Body["text"]!);
        Assert.Equal(saved.Id, response.Destination.Id);
    }

    [Fact]
    public async Task AddByTopicLink_UsesTheThread()
    {
        _h.Api.AddChat(-1001234567890, "supergroup", "Forum", isForum: true);
        _h.Api.SetMember(-1001234567890, Member);

        var response = (await _controller.Add(new AddDestinationRequest { ExpectedRevision = 0, TopicLink = "https://t.me/c/1234567890/42" }, Ct)).Value!;

        Assert.Equal("Added", response.Outcome);
        Assert.Equal((-1001234567890L, 42), (response.Destination.ChatId, response.Destination.ThreadId));
        Assert.All(_h.Api.CallsTo("sendChatAction"), c => Assert.Equal(42, (int)c.Body["message_thread_id"]!)); // the add check, then the test send's re-check
        Assert.Equal(42, (int)Assert.Single(_h.Api.CallsTo("sendMessage")).Body["message_thread_id"]!);
    }

    [Fact]
    public async Task AddByUsernameLink_StoresTheResolvedNumericChatId()
    {
        _h.Api.AddChat(-1003, "supergroup", "Public", isForum: true, username: "jfpublic");
        _h.Api.SetMember(-1003, Member);

        var response = (await _controller.Add(new AddDestinationRequest { ExpectedRevision = 0, TopicLink = "https://t.me/jfpublic/7" }, Ct)).Value!;

        Assert.Equal(-1003, Assert.Single(_h.Settings.Current.Destinations).ChatId);
        Assert.Equal(7, response.Destination.ThreadId);
    }

    [Fact]
    public async Task Add_WhenTheCheckFails_Is400_AndSavesNothing()
    {
        var result = await _controller.Add(new AddDestinationRequest { ExpectedRevision = 0, ChatId = -1999 }, Ct);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(("ChatId", _h.Strings.Get("check.notFound")), (Assert.Single(Assert.IsType<ErrorsDto>(bad.Value).Errors).Field, Assert.Single(((ErrorsDto)bad.Value!).Errors).Message));
        Assert.Empty(_h.Settings.Current.Destinations);
    }

    [Fact]
    public async Task Add_WhenNotConnected_Is400()
    {
        _h.Connection.Detach();

        var bad = Assert.IsType<BadRequestObjectResult>((await _controller.Add(new AddDestinationRequest { ExpectedRevision = 0, ChatId = -1001 }, Ct)).Result);

        Assert.Equal(_h.Strings.Get("destination.notConnected"), Assert.Single(Assert.IsType<ErrorsDto>(bad.Value).Errors).Message);
    }

    [Fact]
    public async Task Add_ABadLinkOrNeitherIdNorLink_Is400()
    {
        Assert.IsType<BadRequestObjectResult>((await _controller.Add(new AddDestinationRequest { ExpectedRevision = 0, TopicLink = "https://example.com/x" }, Ct)).Result);
        Assert.IsType<BadRequestObjectResult>((await _controller.Add(new AddDestinationRequest { ExpectedRevision = 0 }, Ct)).Result);
        Assert.IsType<BadRequestObjectResult>((await _controller.Add(new AddDestinationRequest { ExpectedRevision = 0, ChatId = -1001, Preset = "Loud" }, Ct)).Result);
    }

    [Fact]
    public async Task Add_AnExistingChat_IsAlreadyExists_AndRechecks()
    {
        var existing = _h.AddDestination(-1001);
        _health.MarkProblem(existing.Id, HealthStatus.Lost, "kicked");

        var response = (await _controller.Add(new AddDestinationRequest { ExpectedRevision = _h.Settings.Current.Revision, ChatId = -1001 }, Ct)).Value!;

        Assert.Equal(("AlreadyExists", existing.Id), (response.Outcome, response.Destination.Id));
        Assert.Equal(HealthStatus.Ok, _health.Get(existing.Id).Status);
        Assert.Single(_h.Settings.Current.Destinations);
        Assert.Empty(_h.Api.CallsTo("sendMessage"));
    }

    [Fact]
    public async Task Add_WithAStaleRevision_Is409_WithCurrentData()
    {
        _h.AddDestination(-1001);
        _h.Api.AddChat(-1002, "supergroup", "Other");
        _h.Api.SetMember(-1002, Member);

        var conflict = Assert.IsType<ConflictObjectResult>((await _controller.Add(new AddDestinationRequest { ExpectedRevision = 0, ChatId = -1002 }, Ct)).Result);

        Assert.Single(Assert.IsType<ConflictDto<DestinationsDto>>(conflict.Value).Current.Items);
    }

    [Fact]
    public async Task TheSameChatAddedTwiceAtOnce_CreatesOneDestination()
    {
        _h.Api.AddChat(-1001, "supergroup", "Twice");
        _h.Api.OnAsync("getChatMember", async (_, ct) =>
        {
            await Task.Delay(100, ct);
            return FakeBotApi.Ok(Member);
        });
        var request = new AddDestinationRequest { ExpectedRevision = 0, ChatId = -1001 };

        var results = await Task.WhenAll(_controller.Add(request, Ct), _controller.Add(request, Ct));

        Assert.Equal(["Added", "AlreadyExists"], results.Select(r => r.Value!.Outcome).Order(StringComparer.Ordinal).ToArray());
        Assert.Single(_h.Settings.Current.Destinations);
        Assert.Single(_h.Api.CallsTo("sendMessage"));
    }

    [Fact]
    public void Put_PresetExpandsEvents()
    {
        var d = _h.AddDestination(-1001, preset: PresetKind.Custom);

        var dto = _controller.Update(d.Id, new UpdateDestinationRequest { ExpectedRevision = _h.Settings.Current.Revision, Preset = "NewMedia" }).Value!;

        Assert.Equal("NewMedia", dto.Preset);
        Assert.Equal(EventCatalog.Expand(PresetKind.NewMedia), _h.Settings.Current.Destinations.Single().Events);
    }

    [Fact]
    public void Put_EventsMakeItCustom()
    {
        var d = _h.AddDestination(-1001);

        var dto = _controller.Update(d.Id, new UpdateDestinationRequest { ExpectedRevision = _h.Settings.Current.Revision, Events = ["media.movie"], SilentGroups = ["media"], LibraryIds = ["F137A2DD-21BB-C1B9-9AA5-C0F6BF02A805"] }).Value!;

        var saved = _h.Settings.Current.Destinations.Single();
        Assert.Equal(("Custom", PresetKind.Custom), (dto.Preset, saved.Preset));
        Assert.Equal(["media.movie"], saved.Events);
        Assert.Equal(["media"], saved.SilentGroups);
        Assert.Equal(["f137a2dd21bbc1b99aa5c0f6bf02a805"], saved.LibraryIds);
    }

    [Fact]
    public void Put_UnknownEventKey_Is400()
    {
        var d = _h.AddDestination(-1001);

        Assert.IsType<BadRequestObjectResult>(_controller.Update(d.Id, new UpdateDestinationRequest { ExpectedRevision = _h.Settings.Current.Revision, Events = ["media.podcast"] }).Result);
        Assert.IsType<BadRequestObjectResult>(_controller.Update(d.Id, new UpdateDestinationRequest { ExpectedRevision = _h.Settings.Current.Revision, SilentGroups = ["noise"] }).Result);
        Assert.IsType<BadRequestObjectResult>(_controller.Update(d.Id, new UpdateDestinationRequest { ExpectedRevision = _h.Settings.Current.Revision, LibraryIds = ["not-a-guid"] }).Result);
    }

    [Fact]
    public void Put_UnknownId_Is404()
        => Assert.IsType<NotFoundResult>(_controller.Update("zzzzzzzz", new UpdateDestinationRequest { ExpectedRevision = 0, Enabled = false }).Result);

    [Fact]
    public void Put_DisablingDoesNotTouchHealth()
    {
        var d = _h.AddDestination(-1001);
        _health.MarkProblem(d.Id, HealthStatus.NeedsPermissions, "rights");

        var dto = _controller.Update(d.Id, new UpdateDestinationRequest { ExpectedRevision = _h.Settings.Current.Revision, Enabled = false }).Value!;

        Assert.False(dto.Enabled);
        Assert.Equal(HealthStatus.NeedsPermissions, _health.Get(d.Id).Status);
    }

    [Fact]
    public async Task Delete_RemovesTheDestination_ItsQueuedEntries_AndItsHealth()
    {
        var d = _h.AddDestination(-1001);
        _health.MarkProblem(d.Id, HealthStatus.Lost, "kicked");
        _outbox.Enqueue(new OutboxEntry { EntryId = "e1", DestinationId = d.Id, EventKey = "media.movie", QueuedAt = _h.Time.GetUtcNow() });

        Assert.IsType<NoContentResult>(await _controller.Delete(d.Id, leave: false, expectedRevision: _h.Settings.Current.Revision, Ct));

        Assert.Empty(_h.Settings.Current.Destinations);
        Assert.Equal(0, _outbox.Count);
        Assert.False(_h.State.Read(s => s.Health.ContainsKey(d.Id)));
        Assert.Empty(_h.Api.CallsTo("leaveChat"));
    }

    [Fact]
    public async Task Delete_WithLeave_LeavesTheChat()
    {
        var d = _h.AddDestination(-1001);

        await _controller.Delete(d.Id, leave: true, expectedRevision: _h.Settings.Current.Revision, Ct);

        Assert.Equal(-1001, (long)Assert.Single(_h.Api.CallsTo("leaveChat")).Body["chat_id"]!);
    }

    [Fact]
    public async Task Delete_WithLeave_StaysWhenAnotherTopicStillUsesTheChat()
    {
        var a = _h.AddDestination(-1001, threadId: 1);
        _h.AddDestination(-1001, threadId: 2);

        await _controller.Delete(a.Id, leave: true, expectedRevision: _h.Settings.Current.Revision, Ct);

        Assert.Empty(_h.Api.CallsTo("leaveChat"));
        Assert.Single(_h.Settings.Current.Destinations);
    }

    [Fact]
    public async Task Delete_StaleRevision_Is409_AndUnknown_Is404()
    {
        var d = _h.AddDestination(-1001);

        Assert.IsType<ConflictObjectResult>(await _controller.Delete(d.Id, leave: false, expectedRevision: 0, Ct));
        Assert.IsType<NotFoundResult>(await _controller.Delete("zzzzzzzz", leave: false, expectedRevision: _h.Settings.Current.Revision, Ct));
    }

    [Fact]
    public async Task TestAndCheck_ReportHealth()
    {
        var d = _h.AddDestination(-1001);

        var test = (await _controller.Test(d.Id, Ct)).Value!;
        var check = (await _controller.Check(d.Id, Ct)).Value!;

        Assert.True(test.Sent);
        Assert.Equal(("Ok", "Ok"), (test.Health.Status, check.Health.Status));
        Assert.IsType<NotFoundResult>((await _controller.Test("zzzzzzzz", Ct)).Result);
    }

    [Fact]
    public void Links_UseTheBotUsername_AndAreEmptyWithoutOne()
    {
        var links = _controller.GetLinks().Value!;
        Assert.Equal("https://t.me/tgjelly_test_bot?start=setup", links.OpenBot);
        Assert.Equal("https://t.me/tgjelly_test_bot?start=add", links.StartAdd);
        Assert.Equal("https://t.me/tgjelly_test_bot?startgroup=add", links.StartGroup);
        Assert.Equal("https://t.me/tgjelly_test_bot?startchannel&admin=post_messages+edit_messages", links.StartChannel);

        var empty = new DestinationsController(_h.Settings, new Jellyfin.Plugin.TgJelly.Bot.BotConnection(), new DestinationChecker(_h.Connection, _h.Strings), _health, new DestinationTester(_h.Connection, _health, _h.Strings, NullLogger<DestinationTester>.Instance), _outbox, _h.Strings, NullLogger<DestinationsController>.Instance).GetLinks().Value!;
        Assert.Equal(string.Empty, empty.OpenBot);
    }

    [Fact]
    public void Get_IncludesHealth()
    {
        var d = _h.AddDestination(-1001);
        _health.MarkProblem(d.Id, HealthStatus.TopicMissing, "gone");

        var list = _controller.Get().Value!;

        var item = Assert.Single(list.Items);
        Assert.Equal(("TopicMissing", "gone"), (item.Health.Status, item.Health.LastError));
        Assert.Equal(_h.Settings.Current.Revision, list.Revision);
    }

    public void Dispose() => _h.Dispose();
}
