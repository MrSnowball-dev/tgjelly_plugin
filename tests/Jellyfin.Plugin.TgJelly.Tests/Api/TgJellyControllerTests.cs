using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Api;
using Jellyfin.Plugin.TgJelly.Compat;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.State;
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

        var status = new TgJellyController(compat, gate, settings, state, new SecretsStore(paths, time, NullLogger<SecretsStore>.Instance)).GetStatus().Value!;

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

        var status = new TgJellyController(compat, gate, settings, state, new SecretsStore(paths, time, NullLogger<SecretsStore>.Instance)).GetStatus().Value!;

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

        var status = new TgJellyController(CompatGuard.Evaluate(new Version(BuildInfo.TargetJellyfinMajor, 0, 0, 0)), gate, settings, state, secrets)
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

        var status = new TgJellyController(CompatGuard.Evaluate(new Version(BuildInfo.TargetJellyfinMajor, 0, 0, 0)), gate, settings, state, new SecretsStore(paths, time, NullLogger<SecretsStore>.Instance)).GetStatus().Value!;

        var error = Assert.Single(status.LastErrors);
        Assert.Equal(("outbox", "boom"), (error.Source, error.Message));
    }
}
