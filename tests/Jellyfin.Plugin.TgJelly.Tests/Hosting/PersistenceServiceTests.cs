using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Hosting;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Hosting;

public sealed class PersistenceServiceTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly DataPaths _paths;
    private readonly FakeTimeProvider _time = new();
    private readonly FakeConfigurationHost _host = new();
    private readonly InstanceGate _gate;
    private readonly StateStore _state;
    private readonly SettingsStore _settings;
    private readonly SecretsStore _secrets;

    public PersistenceServiceTests()
    {
        _paths = new DataPaths(_dir.Path);
        _gate = new InstanceGate(_paths, _time, NullLogger<InstanceGate>.Instance);
        _state = new StateStore(_paths, _time, NullLogger<StateStore>.Instance);
        _settings = new SettingsStore(_host, _paths, _state, _time, NullLogger<SettingsStore>.Instance);
        _secrets = new SecretsStore(_paths, _time, NullLogger<SecretsStore>.Instance);
    }

    [Fact]
    public async Task Start_LoadsState_RestoresWipedSettings_FlushesEverySecond_AndOnStop()
    {
        Directory.CreateDirectory(_paths.Root);
        var backup = new PluginConfiguration { Revision = 3, PublicUrl = "https://jf.example.com" };
        await File.WriteAllTextAsync(_paths.SettingsBackup, JsonSerializer.Serialize(backup, JsonDefaults.Options), TestContext.Current.CancellationToken);
        using var service = new PersistenceService(_gate, _state, _settings, _secrets, _time, NullLogger<PersistenceService>.Instance);

        await _gate.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);
        // BackupRestoredAt is set after the restored settings are saved, so waiting for it covers both
        // (on .NET 10 the service runs on the thread pool, so IsLoaded alone would race).
        await Eventually.TrueAsync(
            () => _state.IsLoaded && _state.Read(s => s.Status.BackupRestoredAt) is not null,
            "state loaded and backup restored after ownership");

        Assert.Equal(3, _host.Configuration.Revision);

        await Eventually.AdvanceUntilAsync(_time, TimeSpan.FromSeconds(1), () => File.Exists(_paths.State), "state.json written by the 1 s loop");

        File.Delete(_paths.State);
        _state.Update(s => s.Status.LoadWarnings.Add("x"));
        await service.StopAsync(CancellationToken.None);
        Assert.True(File.Exists(_paths.State));

        await _gate.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Start_AddsNewEvents_Once()
    {
        var stored = new PluginConfiguration { Destinations = [new Destination { Id = "abcd1234", Events = [.. EventCatalog.AllKeys.Where(k => k != "media.book")] }] };
        stored.Revision = 4;
        _host.SaveFromStore(stored);
        using var service = new PersistenceService(_gate, _state, _settings, _secrets, _time, NullLogger<PersistenceService>.Instance);

        await _gate.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);
        await Eventually.TrueAsync(() => _host.Configuration.Revision == 5, "new events added");
        await Eventually.SettleAsync();

        Assert.Contains("media.book", _host.Configuration.Destinations[0].Events);
        Assert.Equal(5, _host.Configuration.Revision);
        await service.StopAsync(CancellationToken.None);
        await _gate.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WithoutOwnership_NothingIsLoadedOrWritten()
    {
        using var other = InstanceLock.TryAcquire(_paths.Lock);
        using var service = new PersistenceService(_gate, _state, _settings, _secrets, _time, NullLogger<PersistenceService>.Instance);

        await _gate.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(3));
        await Eventually.SettleAsync();

        Assert.False(_state.IsLoaded);
        await service.StopAsync(CancellationToken.None);
        Assert.False(File.Exists(_paths.State));
        await _gate.StopAsync(CancellationToken.None);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task UnwritableDataFolder_DoesNotStopTheService_AndStateIsWrittenWhenItRecovers()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.UserName == "root", "needs Unix permissions as a normal user");
        using var service = new PersistenceService(_gate, _state, _settings, _secrets, _time, NullLogger<PersistenceService>.Instance);
        await _gate.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);
        await Eventually.TrueAsync(() => _state.IsLoaded, "state loaded");

        File.SetUnixFileMode(_paths.Root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            _state.Update(s => s.Status.LoadWarnings.Add("pending"));
            await Eventually.AdvanceUntilAsync(
                _time, TimeSpan.FromSeconds(1), () => _state.LastErrorsSnapshot().Any(e => e.Source == "persistence"), "the failed flush is recorded");
            Assert.False(File.Exists(_paths.State));
        }
        finally
        {
            File.SetUnixFileMode(_paths.Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        await Eventually.AdvanceUntilAsync(_time, TimeSpan.FromSeconds(1), () => File.Exists(_paths.State), "state.json written once the folder recovered");
        await service.StopAsync(CancellationToken.None);
        await _gate.StopAsync(CancellationToken.None);
    }

    public void Dispose()
    {
        _settings.Dispose();
        _gate.Dispose();
        _dir.Dispose();
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task FlushFailure_IsRecordedInStatus_AndTheLoopKeepsRunning()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.UserName == "root", "needs Unix permissions as a normal user");
        var logger = new ListLogger<PersistenceService>();
        using var service = new PersistenceService(_gate, _state, _settings, _secrets, _time, logger);
        await _gate.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);
        await Eventually.TrueAsync(() => _state.IsLoaded, "state loaded");

        File.SetUnixFileMode(_paths.Root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            _state.Update(s => s.Status.LoadWarnings.Add("pending"));
            await Eventually.AdvanceUntilAsync(
                _time, TimeSpan.FromSeconds(1), () => _state.LastErrorsSnapshot().Any(e => e.Source == "persistence"), "the failed flush is recorded");
        }
        finally
        {
            File.SetUnixFileMode(_paths.Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        await Eventually.AdvanceUntilAsync(_time, TimeSpan.FromSeconds(1), () => File.Exists(_paths.State), "the same loop writes state.json once the folder recovered");
        Assert.Equal(0, logger.Count(LogLevel.Error)); // the supervisor never had to restart the loop
        await service.StopAsync(CancellationToken.None);
        await _gate.StopAsync(CancellationToken.None);
    }
}
