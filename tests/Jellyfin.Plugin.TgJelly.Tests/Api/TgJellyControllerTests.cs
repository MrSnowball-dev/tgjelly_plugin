using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using System;
using Jellyfin.Plugin.TgJelly.Api;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Compat;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Delivery;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.Intake;
using Jellyfin.Plugin.TgJelly.Library;
using Jellyfin.Plugin.TgJelly.Pipeline;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.Strings;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Api;

public class TgJellyControllerTests
{
    [Fact]
    public async Task Status_ReportsCompat_Instance_AndRevision()
    {
        using var dir = new TempDir();
        var paths = new DataPaths(dir.Path);
        var time = new FakeTimeProvider();
        var state = new StateStore(paths, time, NullLogger<StateStore>.Instance);
        state.Load();
        var host = new FakeConfigurationHost();
        using var settings = new SettingsStore(host, paths, state, time, NullLogger<SettingsStore>.Instance);
        settings.Update(null, c => c.PublicUrl = "https://jf.example.com");
        using var gate = new InstanceGate(paths, time, NullLogger<InstanceGate>.Instance);
        await gate.StartAsync(CancellationToken.None);
        await gate.WaitForOwnershipAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var compat = CompatGuard.Evaluate(new Version(12, 2, 0, 0), 10, "10.11");

        var status = NewController(compat, gate, settings, state, new SecretsStore(paths, time, NullLogger<SecretsStore>.Instance)).GetStatus().Value!;

        Assert.Equal("10.11", status.BuildTarget);
        Assert.False(status.Compatible);
        Assert.Equal(compat.Message, status.CompatMessage);
        Assert.True(status.InstanceOwner);
        Assert.Null(status.InstanceMessage);
        Assert.Equal(1, status.Revision);
        Assert.Empty(status.LoadWarnings);

        await gate.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Status_Answers_BeforeOwnershipAndBeforeStateIsLoaded()
    {
        // The page may be opened while a previous instance still holds state.lock.
        using var dir = new TempDir();
        var paths = new DataPaths(dir.Path);
        var time = new FakeTimeProvider();
        var state = new StateStore(paths, time, NullLogger<StateStore>.Instance);
        using var settings = new SettingsStore(new FakeConfigurationHost(), paths, state, time, NullLogger<SettingsStore>.Instance);
        using var previous = InstanceLock.TryAcquire(paths.Lock);
        Assert.NotNull(previous);
        using var gate = new InstanceGate(paths, time, NullLogger<InstanceGate>.Instance);
        await gate.StartAsync(CancellationToken.None);
        await Eventually.TrueAsync(() => gate.WaitingMessage == InstanceGate.WaitingForPreviousInstance, "the gate found state.lock busy");
        var compat = CompatGuard.Evaluate(new Version(BuildInfo.TargetJellyfinMajor, 0, 0, 0));

        var status = NewController(compat, gate, settings, state, new SecretsStore(paths, time, NullLogger<SecretsStore>.Instance)).GetStatus().Value!;

        Assert.True(status.Compatible);
        Assert.False(status.InstanceOwner);
        Assert.Equal(InstanceGate.WaitingForPreviousInstance, status.InstanceMessage);
        Assert.Equal(0, status.Revision);
        await gate.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Status_ShowsAQuarantinedSecretsFile()
    {
        using var dir = new TempDir();
        var paths = new DataPaths(dir.Path);
        var time = new FakeTimeProvider();
        var state = new StateStore(paths, time, NullLogger<StateStore>.Instance);
        state.Load();
        using var settings = new SettingsStore(new FakeConfigurationHost(), paths, state, time, NullLogger<SettingsStore>.Instance);
        using var gate = new InstanceGate(paths, time, NullLogger<InstanceGate>.Instance);
        System.IO.Directory.CreateDirectory(paths.Root);
        System.IO.File.WriteAllText(paths.Secrets, "{oops");
        var secrets = new SecretsStore(paths, time, NullLogger<SecretsStore>.Instance);
        _ = secrets.Load();

        var status = NewController(CompatGuard.Evaluate(new Version(BuildInfo.TargetJellyfinMajor, 0, 0, 0)), gate, settings, state, secrets)
            .GetStatus().Value!;

        var warning = Assert.Single(status.LoadWarnings);
        Assert.StartsWith("secrets.json was unreadable", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_ShowsLastErrors()
    {
        using var dir = new TempDir();
        var paths = new DataPaths(dir.Path);
        var time = new FakeTimeProvider();
        var state = new StateStore(paths, time, NullLogger<StateStore>.Instance);
        state.Load();
        using var settings = new SettingsStore(new FakeConfigurationHost(), paths, state, time, NullLogger<SettingsStore>.Instance);
        using var gate = new InstanceGate(paths, time, NullLogger<InstanceGate>.Instance);
        state.RecordError("outbox", "boom");

        var status = NewController(CompatGuard.Evaluate(new Version(BuildInfo.TargetJellyfinMajor, 0, 0, 0)), gate, settings, state, new SecretsStore(paths, time, NullLogger<SecretsStore>.Instance)).GetStatus().Value!;

        var error = Assert.Single(status.LastErrors);
        Assert.Equal(("outbox", "boom"), (error.Source, error.Message));
    }

    [Fact]
    public async Task Status_ShowsConnectionState_BotUsername_AndCounts()
    {
        using var h = new BotTestHarness();
        h.Connect();
        var d = h.AddDestination(-1001);
        var gate = await h.StartOwnedGateAsync();
        var outbox = new Outbox(h.State, h.Strings, h.Time);
        outbox.Enqueue(new OutboxEntry { EntryId = "e1", DestinationId = d.Id, EventKey = "media.movie", QueuedAt = h.Time.GetUtcNow() });
        using var health = new HealthService(gate, h.Connection, new DestinationChecker(h.Connection, h.Strings), h.Settings, h.State, h.Time, NullLogger<HealthService>.Instance);
        health.MarkProblem(d.Id, HealthStatus.Lost, "kicked");
        var library = new FakeLibrary();
        var readiness = new MediaReadiness(h.State, h.Settings, library, new PendingIdSet(), h.Time, NullLogger<MediaReadiness>.Instance);
        var controller = new TgJellyController(CompatGuard.Evaluate(new Version(BuildInfo.TargetJellyfinMajor, 0, 0, 0)), gate, h.Settings, h.State, h.Secrets, h.Connection, outbox, health, h.Strings, readiness, library);

        var status = controller.GetStatus().Value!;

        Assert.Equal(("Connected", "Connected as @tgjelly_test_bot", FakeBotApi.BotUsername), (status.ConnectionState, status.ConnectionMessage, status.BotUsername));
        Assert.Equal((1, 1), (status.OutboxCount, status.UnhealthyDestinations));
        Assert.False(status.HasToken);
    }

    [Fact]
    public async Task Status_ShowsWaitingForInstance_UntilTheGateIsOwned()
    {
        using var h = new BotTestHarness();
        using var previous = InstanceLock.TryAcquire(h.Paths.Lock);
        using var gate = new InstanceGate(h.Paths, h.Time, NullLogger<InstanceGate>.Instance);
        await gate.StartAsync(CancellationToken.None);
        await Eventually.TrueAsync(() => gate.WaitingMessage is not null, "gate waiting");

        var status = NewController(CompatGuard.Evaluate(new Version(BuildInfo.TargetJellyfinMajor, 0, 0, 0)), gate, h.Settings, h.State, h.Secrets).GetStatus().Value!;

        Assert.Equal(("WaitingForInstance", InstanceGate.WaitingForPreviousInstance), (status.ConnectionState, status.ConnectionMessage));
        await gate.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Status_NeverContainsTheToken()
    {
        using var h = new BotTestHarness();
        h.Secrets.Save(new BotSecrets { BotToken = FakeBotApi.Token });
        using var gate = new InstanceGate(h.Paths, h.Time, NullLogger<InstanceGate>.Instance);
        h.State.RecordError("bot", "failed for " + FakeBotApi.Token);

        var status = NewController(CompatGuard.Evaluate(new Version(BuildInfo.TargetJellyfinMajor, 0, 0, 0)), gate, h.Settings, h.State, h.Secrets).GetStatus().Value!;

        Assert.True(status.HasToken);
        Assert.DoesNotContain(FakeBotApi.Token, JsonSerializer.Serialize(status), StringComparison.Ordinal);
    }

    [Fact]
    public void Status_PendingMedia()
    {
        using var h = new BotTestHarness();
        var library = new FakeLibrary();
        var readiness = new MediaReadiness(h.State, h.Settings, library, new PendingIdSet(), h.Time, NullLogger<MediaReadiness>.Instance);
        readiness.Process(new MediaIntake(MediaIntakeKind.Added, PipelineRig.Facts(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Movie }, refreshed: false), h.Time.GetUtcNow()));
        readiness.Process(new MediaIntake(MediaIntakeKind.Added, PipelineRig.Facts(new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Movie }, refreshed: false), h.Time.GetUtcNow()));
        using var gate = new InstanceGate(h.Paths, h.Time, NullLogger<InstanceGate>.Instance);

        var status = NewController(Compatible, gate, h.Settings, h.State, h.Secrets, readiness, library).GetStatus().Value!;

        Assert.Equal(2, status.PendingMedia);
    }

    [Fact]
    public void Status_UpdateCheckFailing_After3()
    {
        using var h = new BotTestHarness();
        using var gate = new InstanceGate(h.Paths, h.Time, NullLogger<InstanceGate>.Instance);
        h.State.Update(s =>
        {
            s.UpdateCheck.ConsecutiveFailures = 2;
            s.UpdateCheck.LastError = "GitHub answered 403 Forbidden";
        });
        Assert.Null(NewController(Compatible, gate, h.Settings, h.State, h.Secrets).GetStatus().Value!.UpdateCheckFailing);

        h.State.Update(s => s.UpdateCheck.ConsecutiveFailures = 3);

        Assert.Equal("Update check failing: GitHub answered 403 Forbidden", NewController(Compatible, gate, h.Settings, h.State, h.Secrets).GetStatus().Value!.UpdateCheckFailing);
    }

    [Fact]
    public void Status_RemovedLibraryFilters()
    {
        using var h = new BotTestHarness();
        using var gate = new InstanceGate(h.Paths, h.Time, NullLogger<InstanceGate>.Instance);
        var library = new FakeLibrary();
        var existing = Guid.NewGuid();
        library.VirtualFolders.Add(new LibraryRef(existing, "Movies", "movies"));
        var gone = Guid.NewGuid().ToString("N");
        h.AddDestination(-1001);
        h.AddDestination(-1002);
        h.AddDestination(-1003);
        h.Settings.Update(null, c =>
        {
            c.Destinations[0].LibraryIds = [existing.ToString("N"), gone];
            c.Destinations[1].LibraryIds = [gone];
        });

        Assert.Equal(1, NewController(Compatible, gate, h.Settings, h.State, h.Secrets, library: library).GetStatus().Value!.RemovedLibraryFilters);
    }

    private static CompatResult Compatible => CompatGuard.Evaluate(new Version(BuildInfo.TargetJellyfinMajor, 0, 0, 0));

    private static TgJellyController NewController(CompatResult compat, InstanceGate gate, SettingsStore settings, StateStore state, SecretsStore secrets, MediaReadiness? readiness = null, ILibraryAccess? library = null)
    {
        var connection = new BotConnection();
        var strings = StringTable.LoadEmbedded();
        var health = new HealthService(gate, connection, new DestinationChecker(connection, strings), settings, state, TimeProvider.System, NullLogger<HealthService>.Instance);
        library ??= new FakeLibrary();
        readiness ??= new MediaReadiness(state, settings, library, new PendingIdSet(), TimeProvider.System, NullLogger<MediaReadiness>.Instance);
        return new TgJellyController(compat, gate, settings, state, secrets, connection, new Outbox(state, strings, TimeProvider.System), health, strings, readiness, library);
    }
}
