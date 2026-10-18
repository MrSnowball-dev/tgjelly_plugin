using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Compat;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Hosting;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests;

public class PluginServiceRegistratorTests
{
    [Fact]
    public void AllServices_Resolve_AndGateIsTheHostedInstance_RegisteredFirst()
    {
        using var dir = new TempDir();
        _ = TestPlugin.Create(dir.Path); // Jellyfin creates the plugin before resolving services.
        var paths = Substitute.For<IApplicationPaths>();
        paths.PluginConfigurationsPath.Returns(Path.Combine(dir.Path, "plugins", "configurations"));
        var host = Substitute.For<IServerApplicationHost>();
        host.ApplicationVersion.Returns(new Version(BuildInfo.TargetJellyfinMajor, 0, 0, 0));

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(paths);
        services.AddSingleton(host);
        new PluginServiceRegistrator().RegisterServices(services, host);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.NotNull(provider.GetRequiredService<SettingsStore>());
        Assert.NotNull(provider.GetRequiredService<SecretsStore>());
        Assert.NotNull(provider.GetRequiredService<StateStore>());
        Assert.True(provider.GetRequiredService<CompatResult>().IsCompatible);
        Assert.Equal(Path.Combine(dir.Path, "plugins", "configurations", "TgJelly"), provider.GetRequiredService<DataPaths>().Root);

        var hosted = provider.GetServices<IHostedService>().ToList();
        Assert.Same(provider.GetRequiredService<InstanceGate>(), hosted[0]);
        Assert.IsType<PersistenceService>(hosted[1]);
    }

    [Fact]
    public void WithoutAPluginInstance_ADetachedHostIsUsed_SoStartupCannotThrow()
    {
        var logger = new ListLogger<PluginServiceRegistrator>();
        var host = PluginServiceRegistrator.ResolveConfigurationHost(null, logger);

        Assert.IsType<DetachedConfigurationHost>(host);
        Assert.Equal(1, logger.Count(Microsoft.Extensions.Logging.LogLevel.Warning));
        Assert.Equal(0, host.Configuration.Revision);
        Assert.Throws<InvalidOperationException>(() => host.SaveFromStore(new PluginConfiguration()));
    }
}
