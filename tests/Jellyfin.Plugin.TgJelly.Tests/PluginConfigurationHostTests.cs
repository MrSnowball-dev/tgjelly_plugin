using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests;

public class PluginConfigurationHostTests
{
    [Fact]
    public void SaveFromStore_KeepsRevision_WritesXml_AndRaisesNoOutsideEvent()
    {
        using var dir = new TempDir();
        var plugin = TestPlugin.Create(dir.Path);
        var outside = 0;
        plugin.ExternalConfigurationSaved += (_, _) => outside++;

        plugin.SaveFromStore(new PluginConfiguration { Revision = 5, PublicUrl = "https://jf.example.com" });

        Assert.Equal(5, plugin.Configuration.Revision);
        Assert.Equal(0, outside);
        var xml = File.ReadAllText(Path.Combine(dir.Path, "plugins", "configurations", "Jellyfin.Plugin.TgJelly.xml"));
        Assert.Contains("<PublicUrl>https://jf.example.com</PublicUrl>", xml, System.StringComparison.Ordinal);
    }

    [Fact]
    public void OutsideSave_BumpsRevisionAboveBoth_AndRaisesEventOnce()
    {
        using var dir = new TempDir();
        var plugin = TestPlugin.Create(dir.Path);
        plugin.SaveFromStore(new PluginConfiguration { Revision = 5 });
        PluginConfiguration? seen = null;
        var outside = 0;
        plugin.ExternalConfigurationSaved += (_, e) =>
        {
            outside++;
            seen = e.Configuration;
        };

        plugin.UpdateConfiguration(new PluginConfiguration { Revision = 2, PublicUrl = "https://stale.example.com" });

        Assert.Equal(6, plugin.Configuration.Revision);
        Assert.Equal(1, outside);
        Assert.Equal(6, seen!.Revision);
    }

    [Fact]
    public void OutsideSave_WithNullSections_IsNormalized()
    {
        using var dir = new TempDir();
        var plugin = TestPlugin.Create(dir.Path);

        plugin.UpdateConfiguration(new PluginConfiguration { Batching = null!, Destinations = null! });

        Assert.NotNull(plugin.Configuration.Batching);
        Assert.Empty(plugin.Configuration.Destinations);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void SaveFromStore_WhenTheXmlCannotBeWritten_KeepsThePreviousSettings()
    {
        // Jellyfin replaces the live settings before writing the XML; a failed write must not leave them changed.
        Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.UserName == "root", "needs Unix permissions as a normal user");
        using var dir = new TempDir();
        var plugin = TestPlugin.Create(dir.Path);
        plugin.SaveFromStore(new PluginConfiguration { Revision = 1, PublicUrl = "https://old.example.com" });
        File.SetUnixFileMode(XmlPath(dir), UnixFileMode.UserRead);

        Assert.ThrowsAny<UnauthorizedAccessException>(() => plugin.SaveFromStore(new PluginConfiguration { Revision = 2, PublicUrl = "https://new.example.com" }));

        Assert.Equal(1, plugin.Configuration.Revision);
        Assert.Equal("https://old.example.com", plugin.Configuration.PublicUrl);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void OutsideSave_WhenTheXmlCannotBeWritten_KeepsThePreviousSettings_AndRaisesNoEvent()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.UserName == "root", "needs Unix permissions as a normal user");
        using var dir = new TempDir();
        var plugin = TestPlugin.Create(dir.Path);
        plugin.SaveFromStore(new PluginConfiguration { Revision = 1, PublicUrl = "https://old.example.com" });
        var outside = 0;
        plugin.ExternalConfigurationSaved += (_, _) => outside++;
        File.SetUnixFileMode(XmlPath(dir), UnixFileMode.UserRead);

        Assert.ThrowsAny<UnauthorizedAccessException>(() => plugin.UpdateConfiguration(new PluginConfiguration { PublicUrl = "https://new.example.com" }));

        Assert.Equal(1, plugin.Configuration.Revision);
        Assert.Equal("https://old.example.com", plugin.Configuration.PublicUrl);
        Assert.Equal(0, outside);
    }

    [Fact]
    public void OutsideAndStoreSaves_NeverShareARevision()
    {
        using var dir = new TempDir();
        var plugin = TestPlugin.Create(dir.Path);
        var paths = new Jellyfin.Plugin.TgJelly.State.DataPaths(dir.Path);
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var state = new Jellyfin.Plugin.TgJelly.State.StateStore(paths, time, Microsoft.Extensions.Logging.Abstractions.NullLogger<Jellyfin.Plugin.TgJelly.State.StateStore>.Instance);
        state.Load();
        using var store = new SettingsStore(plugin, paths, state, time, Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsStore>.Instance);
        var revisions = new System.Collections.Concurrent.ConcurrentBag<long>();
        plugin.ExternalConfigurationSaved += (_, e) => revisions.Add(e.Configuration.Revision);

        System.Threading.Tasks.Parallel.For(0, 400, i =>
        {
            if (i % 2 == 0)
            {
                revisions.Add(store.Update(null, c => c.Language = "en").Configuration.Revision);
            }
            else
            {
                plugin.UpdateConfiguration(new PluginConfiguration());
            }
        });

        Assert.Equal(400, revisions.Distinct().Count());
        Assert.Equal(400, plugin.Configuration.Revision);
    }

    private static string XmlPath(TempDir dir) => Path.Combine(dir.Path, "plugins", "configurations", "Jellyfin.Plugin.TgJelly.xml");

    [Fact]
    public void OutsideSave_WithMaxRevision_DoesNotWrapAround()
    {
        using var dir = new TempDir();
        var plugin = TestPlugin.Create(dir.Path);
        var paths = new Jellyfin.Plugin.TgJelly.State.DataPaths(dir.Path);
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var state = new Jellyfin.Plugin.TgJelly.State.StateStore(paths, time, Microsoft.Extensions.Logging.Abstractions.NullLogger<Jellyfin.Plugin.TgJelly.State.StateStore>.Instance);
        state.Load();
        using var store = new SettingsStore(plugin, paths, state, time, Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsStore>.Instance);
        plugin.SaveFromStore(new PluginConfiguration { Revision = 5 });
        var events = 0;
        store.SettingsChanged += (_, _) => events++;

        plugin.UpdateConfiguration(new PluginConfiguration { Revision = long.MaxValue });
        Assert.Equal(6, plugin.Configuration.Revision);

        store.Update(null, c => c.PublicUrl = "https://jf.example.com");
        Assert.Equal(7, plugin.Configuration.Revision);
        Assert.Equal(2, events);
    }
}
