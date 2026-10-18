using System;
using Jellyfin.Plugin.TgJelly.Compat;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Hosting;
using Jellyfin.Plugin.TgJelly.State;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jellyfin.Plugin.TgJelly;

/// <summary>
/// Registers TgJelly's services with Jellyfin's container (spec §5.3). Runs before the container is built,
/// so everything that needs other services is registered as a factory. Must never throw (F2, F3).
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.TryAddSingleton(TimeProvider.System);
        serviceCollection.AddSingleton(sp => new DataPaths(sp.GetRequiredService<IApplicationPaths>().PluginConfigurationsPath));
        serviceCollection.AddSingleton(sp => CompatGuard.Evaluate(sp.GetRequiredService<IServerApplicationHost>().ApplicationVersion));
        serviceCollection.AddSingleton<SecretsStore>();
        serviceCollection.AddSingleton<StateStore>();
        serviceCollection.AddSingleton(_ => ResolveConfigurationHost(Plugin.Instance));
        serviceCollection.AddSingleton<SettingsStore>();

        // Hosted services stop in reverse order: persistence flushes first, then the gate releases state.lock.
        serviceCollection.AddSingleton<InstanceGate>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<InstanceGate>());
        serviceCollection.AddHostedService<PersistenceService>();
    }

    /// <summary>
    /// Picks the settings host. If Jellyfin could not create the plugin instance, a detached host keeps service
    /// resolution from throwing at host start, which would stop the whole server (F3).
    /// </summary>
    /// <param name="instance">The plugin instance, if Jellyfin created one.</param>
    /// <returns>The host to use.</returns>
    internal static IConfigurationHost ResolveConfigurationHost(Plugin? instance)
        => (IConfigurationHost?)instance ?? new DetachedConfigurationHost();
}
