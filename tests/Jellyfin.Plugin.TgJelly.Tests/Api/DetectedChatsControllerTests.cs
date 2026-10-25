using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Api;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Delivery;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Api;

public sealed class DetectedChatsControllerTests : IAsyncLifetime
{
    private const long Chat = -1005550002;
    private readonly BotTestHarness _h = new();
    private readonly BotMemory _memory;
    private readonly HealthService _health;
    private readonly DestinationChecker _checker;
    private readonly DestinationCreator _creator;
    private readonly DestinationDtoMapper _mapper;
    private InstanceGate _gate = null!;

    public DetectedChatsControllerTests()
    {
        _h.Connect();
        _memory = new BotMemory(_h.State, _h.Time);
        _checker = new DestinationChecker(_h.Connection, _h.Strings);
        var gate = new InstanceGate(_h.Paths, _h.Time, NullLogger<InstanceGate>.Instance);
        _h.Track(gate);
        _health = new HealthService(gate, _h.Connection, _checker, _h.Settings, _h.State, _h.Time, NullLogger<HealthService>.Instance);
        _h.Track(_health);
        var tester = new DestinationTester(_h.Connection, _health, _h.Strings, NullLogger<DestinationTester>.Instance);
        _creator = new DestinationCreator(_h.Settings, _health, tester, _memory, NullLogger<DestinationCreator>.Instance);
        _mapper = new DestinationDtoMapper(_health, new FakeLibrary(), _memory, new SetupGate(_memory, _health, _h.Settings));
    }

    public async ValueTask InitializeAsync() => _gate = await _h.StartOwnedGateAsync();

    public ValueTask DisposeAsync()
    {
        _h.Dispose();
        return ValueTask.CompletedTask;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DetectedChatsController Controller(InstanceGate? gate = null)
        => new(_h.Settings, _h.Connection, _checker, _creator, _memory, _mapper, _h.Strings, gate ?? _gate, _h.State, NullLogger<DetectedChatsController>.Instance);

    private void Detect()
    {
        _memory.RecordDetected(Chat, "Family", ChatKind.Supergroup);
        _memory.RecordDetected(-1009, "Other", ChatKind.Channel);
    }

    [Fact]
    public async Task PostDetected_CreatesAndRemoves()
    {
        Detect();
        _h.Api.AddChat(Chat, "supergroup", "Family");
        _h.Api.SetMember(Chat, BotTestHarness.AdminCanPost);

        var response = (await Controller().SetUp(Chat, new SetUpDetectedChatRequest { ExpectedRevision = _h.Settings.Current.Revision }, Ct)).Value!;

        Assert.Equal(("Added", true), (response.Outcome, response.TestSent));
        var saved = Assert.Single(_h.Settings.Current.Destinations);
        Assert.Equal((Chat, 0, true, response.Destination.Id), (saved.ChatId, saved.ThreadId, saved.FollowsNewEvents, saved.Id));
        _h.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal([-1009L], _memory.VisibleDetected([]).Select(d => d.ChatId));
    }

    [Fact]
    public async Task PostDetected_OfAForum_UsesTheThread()
    {
        Detect();
        _h.Api.AddChat(Chat, "supergroup", "Family", isForum: true);
        _h.Api.SetMember(Chat, BotTestHarness.AdminCanPost);

        var response = (await Controller().SetUp(Chat, new SetUpDetectedChatRequest { ExpectedRevision = _h.Settings.Current.Revision, ThreadId = 7 }, Ct)).Value!;

        Assert.Equal("Added", response.Outcome);
        Assert.Equal(7, Assert.Single(_h.Settings.Current.Destinations).ThreadId);
    }

    [Fact]
    public async Task PostDetected_FailingCheck_Is400()
    {
        Detect();
        _h.Api.AddChat(Chat, "supergroup", "Family");
        _h.Api.SetMember(Chat, FakeBotApi.Member("left"));

        var result = (await Controller().SetUp(Chat, new SetUpDetectedChatRequest { ExpectedRevision = _h.Settings.Current.Revision }, Ct)).Result;

        var errors = Assert.IsType<ErrorsDto>(Assert.IsType<BadRequestObjectResult>(result).Value);
        Assert.Equal("ChatId", Assert.Single(errors.Errors).Field);
        Assert.Equal("The bot was removed from this chat or blocked.", Assert.Single(errors.Errors).Message);
        Assert.Empty(_h.Settings.Current.Destinations);
        Assert.Single(_h.Api.CallsTo("getChat")); // checked once, no retries
        _h.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(2, _memory.VisibleDetected([]).Count);
    }

    [Fact]
    public async Task PostDetected_UnknownChat_Is404()
    {
        var result = (await Controller().SetUp(Chat, new SetUpDetectedChatRequest(), Ct)).Result;

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(_h.Api.Calls);
    }

    [Fact]
    public async Task PostDetected_StaleRevision_Is409WithTheCurrentData()
    {
        Detect();
        _h.Api.AddChat(Chat, "supergroup", "Family");
        _h.Api.SetMember(Chat, BotTestHarness.AdminCanPost);

        var result = (await Controller().SetUp(Chat, new SetUpDetectedChatRequest { ExpectedRevision = _h.Settings.Current.Revision - 1 }, Ct)).Result;

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.IsType<ConflictDto<DestinationsDto>>(conflict.Value);
        Assert.Empty(_h.Settings.Current.Destinations);
    }

    [Fact]
    public void DeleteDetected_Dismisses()
    {
        Detect();

        Assert.IsType<NoContentResult>(Controller().Dismiss(Chat));

        _h.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal([-1009L], _memory.VisibleDetected([]).Select(d => d.ChatId));
        Assert.IsType<NoContentResult>(Controller().Dismiss(Chat));
    }

    [Fact]
    public async Task WhileWaitingForTheDataFolder_Is503()
    {
        Detect();
        var waiting = new InstanceGate(_h.Paths, _h.Time, NullLogger<InstanceGate>.Instance);
        _h.Track(waiting);
        var controller = Controller(waiting);

        Assert.Equal(503, Assert.IsType<ObjectResult>((await controller.SetUp(Chat, new SetUpDetectedChatRequest(), Ct)).Result).StatusCode);
        Assert.Equal(503, Assert.IsType<ObjectResult>(controller.Dismiss(Chat)).StatusCode);
        _h.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(2, _memory.VisibleDetected([]).Count);
    }
}
