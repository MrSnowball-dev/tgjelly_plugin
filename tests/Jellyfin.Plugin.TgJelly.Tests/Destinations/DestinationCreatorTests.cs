using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Destinations;

public sealed class DestinationCreatorTests : IAsyncLifetime
{
    private readonly BotTestHarness _h = new();
    private HealthService _health = null!;
    private DestinationChecker _checker = null!;
    private DestinationCreator _creator = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _h.Connect();
        var gate = await _h.StartOwnedGateAsync();
        _checker = new DestinationChecker(_h.Connection, _h.Strings);
        _health = new HealthService(gate, _h.Connection, _checker, _h.Settings, _h.State, _h.Time, NullLogger<HealthService>.Instance);
        _h.Track(_health);
        var tester = new DestinationTester(_h.Connection, _health, _h.Strings, NullLogger<DestinationTester>.Instance);
        _creator = new DestinationCreator(_h.Settings, _health, tester, NullLogger<DestinationCreator>.Instance);
    }

    public ValueTask DisposeAsync()
    {
        _h.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<ChatCheckResult> CheckAsync(long chatId, string title = "Movie night")
    {
        _h.Api.AddChat(chatId, "supergroup", title);
        _h.Api.SetMember(chatId, BotTestHarness.AdminCanPost);
        var check = await _checker.CheckAsync(chatId, 0, Ct);
        Assert.True(check.Passed);
        return check;
    }

    [Fact]
    public async Task Added_SavesChecksAndTests()
    {
        var check = await CheckAsync(-1001);

        var result = await _creator.CreateAsync(check, 0, EventCatalog.Expand(PresetKind.AdminAlerts), PresetKind.AdminAlerts, 0, Ct);

        Assert.Equal(CreateOutcome.Added, result.Outcome);
        var saved = Assert.Single(_h.Settings.Current.Destinations);
        Assert.Equal((saved.Id, -1001L, "Movie night", PresetKind.AdminAlerts), (result.Destination!.Id, saved.ChatId, saved.Title, saved.Preset));
        Assert.True(result.Test!.Sent);
        Assert.Equal(HealthStatus.Ok, _health.Get(saved.Id).Status);
        Assert.Single(_h.Api.CallsTo("sendMessage"));
    }

    [Fact]
    public async Task Duplicate_ReChecksAndReturnsExisting()
    {
        var existing = _h.AddDestination(-1001);
        _health.MarkProblem(existing.Id, HealthStatus.Lost, "kicked");
        var check = await _checker.CheckAsync(-1001, 0, Ct);

        var result = await _creator.CreateAsync(check, 0, EventCatalog.Expand(PresetKind.Everything), PresetKind.Everything, _h.Settings.Current.Revision, Ct);

        Assert.Equal((CreateOutcome.AlreadyExists, existing.Id), (result.Outcome, result.Destination!.Id));
        Assert.Equal(HealthStatus.Ok, _health.Get(existing.Id).Status);
        Assert.Single(_h.Settings.Current.Destinations);
        Assert.Empty(_h.Api.CallsTo("sendMessage"));
    }

    [Fact]
    public async Task RaceWithAnotherAdd_IsAlreadyExists()
    {
        var check = await CheckAsync(-1001);
        var events = EventCatalog.Expand(PresetKind.Everything);

        var results = await Task.WhenAll(
            _creator.CreateAsync(check, 0, events, PresetKind.Everything, null, Ct),
            _creator.CreateAsync(check, 0, events, PresetKind.Everything, null, Ct));

        Assert.Equal(1, results.Count(r => r.Outcome == CreateOutcome.Added));
        Assert.Equal(1, results.Count(r => r.Outcome == CreateOutcome.AlreadyExists));
        Assert.Single(_h.Settings.Current.Destinations);
    }

    [Fact]
    public async Task StaleRevision_IsConflict()
    {
        _h.AddDestination(-1001);
        var check = await CheckAsync(-1002, "Other");

        var result = await _creator.CreateAsync(check, 0, EventCatalog.Expand(PresetKind.Everything), PresetKind.Everything, 0, Ct);

        Assert.Equal(CreateOutcome.Conflict, result.Outcome);
        Assert.Null(result.Destination);
        Assert.Single(result.Configuration.Destinations);
        Assert.Single(_h.Settings.Current.Destinations);
    }

    [Fact]
    public async Task CustomEvents_AreSavedAsGiven()
    {
        var check = await CheckAsync(-1001);

        var result = await _creator.CreateAsync(check, 0, ["media.movie", "server.started"], PresetKind.Custom, 0, Ct);

        Assert.Equal(CreateOutcome.Added, result.Outcome);
        var saved = Assert.Single(_h.Settings.Current.Destinations);
        Assert.Equal(PresetKind.Custom, saved.Preset);
        Assert.Equal(["media.movie", "server.started"], saved.Events);
    }
}
