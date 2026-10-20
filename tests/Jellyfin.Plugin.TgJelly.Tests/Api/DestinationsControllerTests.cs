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

public sealed class DestinationsControllerTests : IAsyncLifetime
{
    private const string Member = """{"status":"member","user":{"id":123456789,"is_bot":true,"first_name":"b"}}""";

    private readonly BotTestHarness _h = new();
    private readonly HealthService _health;
    private readonly Outbox _outbox;
    private DestinationsController _controller = null!;
    private readonly Jellyfin.Plugin.TgJelly.Bot.BotMemory _memory;
    private readonly FakeLibrary _library = new();
    private readonly DestinationChecker _checker;
    private readonly DestinationTester _tester;

    public DestinationsControllerTests()
    {
        _h.Connect();
        var gate = new InstanceGate(_h.Paths, _h.Time, NullLogger<InstanceGate>.Instance);
        _h.Track(gate);
        var checker = new DestinationChecker(_h.Connection, _h.Strings);
        _checker = checker;
        _health = new HealthService(gate, _h.Connection, checker, _h.Settings, _h.State, _h.Time, NullLogger<HealthService>.Instance);
        _h.Track(_health);
        _outbox = new Outbox(_h.State, _h.Strings, _h.Time);
        _memory = new Jellyfin.Plugin.TgJelly.Bot.BotMemory(_h.State, _h.Time);
        _tester = new DestinationTester(_h.Connection, _health, _h.Strings, NullLogger<DestinationTester>.Instance);
    }

    public async ValueTask InitializeAsync() => _controller = NewController(_h.Connection, await _h.StartOwnedGateAsync());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Get_FlagsRemovedLibraries()
    {
        var existing = Guid.NewGuid();
        var gone = Guid.NewGuid().ToString("N");
        _library.VirtualFolders.Add(new Jellyfin.Plugin.TgJelly.Library.LibraryRef(existing, "Movies", "movies"));
        _h.AddDestination(-1001);
        _h.AddDestination(-1002);
        _h.AddDestination(-1003);
        Assert.Equal(SettingsUpdateStatus.Saved, _h.Settings.Update(null, c =>
        {
            c.Destinations[0].LibraryIds = [existing.ToString("N"), gone];
            c.Destinations[1].LibraryIds = [gone];
        }).Status);

        var items = _controller.Get().Value!.Items;

        Assert.Equal([gone], items[0].RemovedLibraryIds);
        Assert.False(items[0].LibraryFilterBroken);
        Assert.Equal([gone], items[1].RemovedLibraryIds);
        Assert.True(items[1].LibraryFilterBroken);
        Assert.Empty(items[2].RemovedLibraryIds);
        Assert.False(items[2].LibraryFilterBroken);
    }

    [Fact]
    public void GetDestinations_IncludesVisibleDetectedChats()
    {
        _h.AddDestination(-1001);
        _memory.RecordDetected(-1002, "Family", ChatKind.Supergroup);
        _memory.RecordDetected(-1003, "Too new", ChatKind.Channel);
        _h.Time.Advance(TimeSpan.FromSeconds(10));
        _memory.RecordDetected(-1004, "News", ChatKind.Channel);
        _h.Time.Advance(TimeSpan.FromSeconds(20));
        _memory.RecordDetected(-1005, "Newest", ChatKind.Channel);

        var dto = _controller.Get().Value!;

        Assert.Equal([-1002L, -1003L], dto.DetectedChats.Select(d => d.ChatId));
        var first = dto.DetectedChats[0];
        Assert.Equal(("Family", "Supergroup", _h.Time.GetUtcNow().AddSeconds(-30)), (first.Title, first.Kind, first.DetectedAt));
        Assert.True(dto.ManualUnlocked);
    }

    [Fact]
    public void GetDestinations_ReportsManualUnlocked()
    {
        _h.AddDestination(-1001);
        Assert.False(_controller.Get().Value!.ManualUnlocked);

        _memory.RecordFailure(SetupFailureKind.CheckFailed);
        Assert.True(_controller.Get().Value!.ManualUnlocked);

        _h.Time.Advance(TimeSpan.FromDays(7));
        Assert.False(_controller.Get().Value!.ManualUnlocked);
    }

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
    public async Task Add_CheckerApiError_ShowsTelegramsRedactedText()
    {
        _h.Api.On("getChat", _ => FakeBotApi.Error(400, "Bad Request: something odd"));

        var bad = Assert.IsType<BadRequestObjectResult>((await _controller.Add(new AddDestinationRequest { ExpectedRevision = 0, ChatId = -1001 }, Ct)).Result);

        var error = Assert.Single(Assert.IsType<ErrorsDto>(bad.Value).Errors);
        Assert.Equal(("ChatId", "Telegram answered with an error: Bad Request: something odd"), (error.Field, error.Message));
    }

    [Fact]
    public async Task Add_CheckerTransportError_SaysUnreachable()
    {
        _h.Api.On("getChat", _ => throw new System.Net.Http.HttpRequestException("connection refused"));

        var bad = Assert.IsType<BadRequestObjectResult>((await _controller.Add(new AddDestinationRequest { ExpectedRevision = 0, ChatId = -1001 }, Ct)).Result);

        Assert.Equal(_h.Strings.Get("token.unreachable"), Assert.Single(Assert.IsType<ErrorsDto>(bad.Value).Errors).Message);
    }

    [Fact]
    public async Task Add_AlreadyExistsRecheckFails_StillAnswersAlreadyExists()
    {
        var existing = _h.AddDestination(-1001);
        _h.Api.SetMember(-1001, Member);
        var calls = 0;
        _h.Api.On("getChat", _ => Interlocked.Increment(ref calls) == 1
            ? FakeBotApi.Ok("""{"id":-1001,"type":"supergroup","title":"G","accent_color_id":0,"max_reaction_count":11}""")
            : throw new System.Net.Http.HttpRequestException("connection refused"));

        var response = (await _controller.Add(new AddDestinationRequest { ExpectedRevision = _h.Settings.Current.Revision, ChatId = -1001 }, Ct)).Value!;

        Assert.Equal(("AlreadyExists", existing.Id), (response.Outcome, response.Destination.Id));
    }

    [Fact]
    public async Task Add_MessageLinkInANonForumChat_IsRejected()
    {
        _h.Api.AddChat(-1001234567890, "supergroup", "Not a forum");
        _h.Api.SetMember(-1001234567890, Member);

        var bad = Assert.IsType<BadRequestObjectResult>((await _controller.Add(new AddDestinationRequest { ExpectedRevision = 0, TopicLink = "https://t.me/c/1234567890/55" }, Ct)).Result);

        var error = Assert.Single(Assert.IsType<ErrorsDto>(bad.Value).Errors);
        Assert.Equal(("TopicLink", _h.Strings.Get("destination.notForum")), (error.Field, error.Message));
        Assert.Empty(_h.Settings.Current.Destinations);
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

        using var gate = new InstanceGate(_h.Paths, _h.Time, NullLogger<InstanceGate>.Instance);
        var empty = NewController(new Jellyfin.Plugin.TgJelly.Bot.BotConnection(), gate).GetLinks().Value!;
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

    [Fact]
    public void Get_IncludesCatalog()
    {
        var catalog = _controller.Get().Value!.Catalog;

        Assert.Equal(EventCatalog.AllKeys, catalog.EventKeys);
        Assert.Equal(EventCatalog.Groups, catalog.Groups);
        Assert.Equal(["Everything", "NewMedia", "AdminAlerts"], catalog.Presets.Keys);
        Assert.Equal(EventCatalog.Expand(PresetKind.NewMedia), catalog.Presets["NewMedia"]);
        Assert.Equal(EventCatalog.Expand(PresetKind.AdminAlerts), catalog.Presets["AdminAlerts"]);
        Assert.Equal(EventCatalog.AllKeys, catalog.Presets["Everything"]);
    }

    [Fact]
    public async Task Mutations_BeforeOwnership_Are503_AndChangeNothing()
    {
        var d = _h.AddDestination(-1001);
        using var gate = new InstanceGate(_h.Paths, _h.Time, NullLogger<InstanceGate>.Instance);
        var controller = NewController(_h.Connection, gate);
        var revision = _h.Settings.Current.Revision;

        Assert.Equal(503, Assert.IsType<ObjectResult>((await controller.Add(new AddDestinationRequest { ExpectedRevision = revision, ChatId = -1002 }, Ct)).Result).StatusCode);
        Assert.Equal(503, Assert.IsType<ObjectResult>(controller.Update(d.Id, new UpdateDestinationRequest { ExpectedRevision = revision, Enabled = false }).Result).StatusCode);
        Assert.Equal(503, Assert.IsType<ObjectResult>(await controller.Delete(d.Id, leave: true, expectedRevision: revision, Ct)).StatusCode);
        Assert.Equal(503, Assert.IsType<ObjectResult>((await controller.Test(d.Id, Ct)).Result).StatusCode);
        Assert.Equal(503, Assert.IsType<ObjectResult>((await controller.Check(d.Id, Ct)).Result).StatusCode);

        Assert.Equal(revision, _h.Settings.Current.Revision);
        Assert.Empty(_h.Api.Calls);
        Assert.Single(controller.Get().Value!.Items);
    }

    public ValueTask DisposeAsync()
    {
        _h.Dispose();
        return ValueTask.CompletedTask;
    }

    private DestinationsController NewController(Jellyfin.Plugin.TgJelly.Bot.BotConnection connection, InstanceGate gate)
        => new(_h.Settings, connection, _checker, _health, _tester, _outbox, _h.Strings, gate, _h.State, NullLogger<DestinationsController>.Instance, new DestinationDtoMapper(_health, _library, _memory, new SetupGate(_memory, _health, _h.Settings)), new DestinationCreator(_h.Settings, _health, _tester, _memory, NullLogger<DestinationCreator>.Instance));
}
