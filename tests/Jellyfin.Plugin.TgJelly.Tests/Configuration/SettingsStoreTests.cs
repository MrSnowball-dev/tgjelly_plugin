using System;
using System.IO;
using System.Text.Json;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Configuration;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeConfigurationHost _host = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 16, 10, 0, 0, TimeSpan.Zero));
    private readonly StateStore _state;
    private readonly SettingsStore _store;
    private readonly DataPaths _paths;

    public SettingsStoreTests()
    {
        _paths = new DataPaths(_dir.Path);
        _state = new StateStore(_paths, _time, NullLogger<StateStore>.Instance);
        _state.Load();
        _store = new SettingsStore(_host, _paths, _state, _time, NullLogger<SettingsStore>.Instance);
    }

    [Fact]
    public void Update_Saves_BumpsRevision_WritesBackup_RaisesEventOnce()
    {
        var events = 0;
        _store.SettingsChanged += (_, e) =>
        {
            events++;
            Assert.Equal("https://jf.example.com", e.Configuration.PublicUrl);
        };

        var result = _store.Update(0, c => c.PublicUrl = "https://jf.example.com");

        Assert.Equal(SettingsUpdateStatus.Saved, result.Status);
        Assert.Equal(1, result.Configuration.Revision);
        Assert.Equal(1, _host.Configuration.Revision);
        Assert.Equal(1, events);
        var backup = JsonSerializer.Deserialize<PluginConfiguration>(File.ReadAllText(_paths.SettingsBackup), JsonDefaults.Options)!;
        Assert.Equal("https://jf.example.com", backup.PublicUrl);
        Assert.Equal(1, backup.Revision);
    }

    [Fact]
    public void Update_WithStaleRevision_IsAConflict_AndSavesNothing()
    {
        _store.Update(null, c => c.PublicUrl = "https://a.example.com");

        var result = _store.Update(0, c => c.PublicUrl = "https://b.example.com");

        Assert.Equal(SettingsUpdateStatus.Conflict, result.Status);
        Assert.Equal("https://a.example.com", result.Configuration.PublicUrl);
        Assert.Equal(1, _host.Saves);
    }

    [Fact]
    public void Update_Invalid_ReturnsErrors_AndSavesNothing()
    {
        var result = _store.Update(null, c => c.Batching.DigestThreshold = 1);

        Assert.Equal(SettingsUpdateStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, e => e.Field == "Batching.DigestThreshold");
        Assert.Equal(0, _host.Saves);
        Assert.False(File.Exists(_paths.SettingsBackup));
    }

    [Fact]
    public void Current_IsACopy()
    {
        var copy = _store.Current;
        copy.PublicUrl = "https://changed.example.com";

        Assert.Equal(string.Empty, _host.Configuration.PublicUrl);
    }

    [Fact]
    public void OutsideSave_IsBackedUp_AndRaisesSettingsChanged()
    {
        var events = 0;
        _store.SettingsChanged += (_, _) => events++;

        _host.SaveFromOutside(new PluginConfiguration { Revision = 4, PublicUrl = "https://outside.example.com" });

        Assert.Equal(1, events);
        Assert.Contains("outside.example.com", File.ReadAllText(_paths.SettingsBackup), StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreBackupIfWiped_RestoresBackup_AndRecordsNotice()
    {
        _store.Update(null, c => c.Destinations = [new Destination { Id = "abcd1234", ChatId = 42, Title = "Me" }]);
        _host.SaveFromStore(new PluginConfiguration()); // what Jellyfin does after a failed XML load (F9)

        Assert.True(_store.RestoreBackupIfWiped());

        Assert.Equal(1, _host.Configuration.Revision);
        Assert.Equal(42, Assert.Single(_host.Configuration.Destinations).ChatId);
        Assert.Equal(_time.GetUtcNow(), _state.Read(s => s.Status.BackupRestoredAt));
    }

    [Fact]
    public void RestoreBackupIfWiped_DoesNothing_WhenSettingsAreReal_OrNoBackup()
    {
        Assert.False(_store.RestoreBackupIfWiped());

        _store.Update(null, c => c.PublicUrl = "https://jf.example.com");
        Assert.False(_store.RestoreBackupIfWiped());
        Assert.Equal("https://jf.example.com", _host.Configuration.PublicUrl);
    }

    [Fact]
    public void ConcurrentUpdates_LoseNothing_AndRevisionsStayStrictlyIncreasing()
    {
        // The bot and the dashboard can save at the same moment.
        System.Threading.Tasks.Parallel.For(0, 50, i => _store.Update(
            null,
            c => c.Destinations = [.. c.Destinations, new Destination { Id = "d" + i.ToString("D7", System.Globalization.CultureInfo.InvariantCulture), ChatId = i }]));

        Assert.Equal(50, _host.Configuration.Revision);
        Assert.Equal(50, _host.Configuration.Destinations.Length);
        Assert.Equal(50, _host.Saves);
    }

    [Fact]
    public void BackupWriteFailure_DoesNotTurnASaveIntoAnError()
    {
        Directory.CreateDirectory(_paths.SettingsBackup); // a folder where settings.bak.json should be
        var events = 0;
        _store.SettingsChanged += (_, _) => events++;

        var result = _store.Update(null, c => c.PublicUrl = "https://jf.example.com");
        _host.SaveFromOutside(new PluginConfiguration { Revision = 5 });

        Assert.Equal(SettingsUpdateStatus.Saved, result.Status);
        Assert.Equal(2, events);
    }

    [Fact]
    public void ThrowingHandler_DoesNotFailTheSave_AndOtherHandlersStillRun()
    {
        var secondCalled = false;
        _store.SettingsChanged += (_, _) => throw new InvalidOperationException("handler bug");
        _store.SettingsChanged += (_, _) => secondCalled = true;

        var result = _store.Update(null, c => c.PublicUrl = "https://jf.example.com");

        Assert.Equal(SettingsUpdateStatus.Saved, result.Status);
        Assert.True(secondCalled);
    }

    [Fact]
    public void ConcurrentSaves_DeliverEventsInRevisionOrder_NeverGoingBackwards()
    {
        var seen = new System.Collections.Concurrent.ConcurrentQueue<long>();
        _store.SettingsChanged += (_, e) => seen.Enqueue(e.Configuration.Revision);

        System.Threading.Tasks.Parallel.For(0, 40, _ => _store.Update(null, c => c.Language = "en"));

        var revisions = seen.ToArray();
        for (var i = 1; i < revisions.Length; i++)
        {
            Assert.True(revisions[i] > revisions[i - 1], "events went backwards: " + string.Join(",", revisions));
        }

        Assert.Equal(40, revisions[^1]);
    }

    [Fact]
    public void AStaleRevision_IsNeverDeliveredAfterANewerOne()
    {
        var seen = new System.Collections.Generic.List<long>();
        _store.SettingsChanged += (_, e) => seen.Add(e.Configuration.Revision);

        _store.RaiseSettingsChanged(new PluginConfiguration { Revision = 2 });
        _store.RaiseSettingsChanged(new PluginConfiguration { Revision = 1 });
        _store.RaiseSettingsChanged(new PluginConfiguration { Revision = 3 });

        Assert.Equal([2, 3], seen);
    }

    [Fact]
    public void BackupFromANewerVersion_WithUnknownEnumNames_IsStillRestored()
    {
        Directory.CreateDirectory(_paths.Root);
        File.WriteAllText(
            _paths.SettingsBackup,
            "{\"revision\": 4, \"proxy\": {\"mode\": \"Mtproto\", \"url\": \"\"}, "
            + "\"destinations\": [{\"id\": \"abcd1234\", \"chatId\": 42, \"kind\": \"Bogus\", \"preset\": \"Playback\"}]}");

        Assert.True(_store.RestoreBackupIfWiped());

        Assert.Equal(ProxyMode.System, _host.Configuration.Proxy.Mode);
        var d = Assert.Single(_host.Configuration.Destinations);
        Assert.Equal(PresetKind.Custom, d.Preset);
        Assert.Equal(ChatKind.Private, d.Kind);
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
    }
}
