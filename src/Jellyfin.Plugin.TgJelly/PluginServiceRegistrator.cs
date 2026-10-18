using System;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Compat;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Delivery;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.Hosting;
using Jellyfin.Plugin.TgJelly.Logging;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.Strings;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

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

        // Every TgJelly type that takes an ILogger<T> gets a redacting one (spec §12); a test enforces the list.
        serviceCollection.AddRedactedLogger<PluginServiceRegistrator>();
        serviceCollection.AddRedactedLogger<SecretsStore>();
        serviceCollection.AddRedactedLogger<StateStore>();
        serviceCollection.AddRedactedLogger<SettingsStore>();
        serviceCollection.AddRedactedLogger<InstanceGate>();
        serviceCollection.AddRedactedLogger<PersistenceService>();
        serviceCollection.AddRedactedLogger<HealthService>();
        serviceCollection.AddRedactedLogger<OutboxDelivery>();
        serviceCollection.AddRedactedLogger<OutboxService>();
        serviceCollection.AddRedactedLogger<BasicUpdateHandler>();
        serviceCollection.AddRedactedLogger<ProfileCalls>();
        serviceCollection.AddRedactedLogger<BotSupervisor>();
        serviceCollection.AddRedactedLogger<BotTokenService>();
        serviceCollection.AddRedactedLogger<DestinationTester>();
        serviceCollection.AddRedactedLogger<Api.DestinationsController>();
        serviceCollection.AddSingleton(sp => new DataPaths(sp.GetRequiredService<IApplicationPaths>().PluginConfigurationsPath));
        serviceCollection.AddSingleton(sp => CompatGuard.Evaluate(sp.GetRequiredService<IServerApplicationHost>().ApplicationVersion));
        serviceCollection.AddSingleton(_ => StringTable.LoadEmbedded());
        serviceCollection.AddSingleton<SecretsStore>();
        serviceCollection.AddSingleton<StateStore>();
        serviceCollection.AddSingleton(sp => ResolveConfigurationHost(Plugin.Instance, sp.GetRequiredService<ILogger<PluginServiceRegistrator>>()));
        serviceCollection.AddSingleton<SettingsStore>();
        serviceCollection.AddSingleton(_ => BotApiEnvironment.FromEnvironment());
        serviceCollection.AddSingleton<ITelegramClientFactory, TelegramClientFactory>();
        serviceCollection.AddSingleton<BotConnection>();
        serviceCollection.AddSingleton<DestinationChecker>();
        serviceCollection.AddSingleton<HealthService>();
        serviceCollection.AddSingleton<Outbox>();
        serviceCollection.AddSingleton<PacingGate>();
        serviceCollection.AddSingleton<OutboxDelivery>();
        serviceCollection.AddSingleton<IServerStartup, ServerStartup>();
        serviceCollection.AddSingleton<IUpdateHandler, BasicUpdateHandler>();
        serviceCollection.AddSingleton<ProfileCalls>();
        serviceCollection.AddSingleton<BotSupervisor>();
        serviceCollection.AddSingleton<ISupervisorControl>(sp => sp.GetRequiredService<BotSupervisor>());
        serviceCollection.AddSingleton<BotTokenService>();
        serviceCollection.AddSingleton<DestinationTester>();

        // Hosted services stop in reverse order: the Outbox and health loops first, then the bot (confirming the
        // last update batch), then persistence flushes, and the gate releases state.lock last.
        serviceCollection.AddSingleton<InstanceGate>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<InstanceGate>());
        serviceCollection.AddHostedService<PersistenceService>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<BotSupervisor>());
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<HealthService>());
        serviceCollection.AddHostedService<OutboxService>();
    }

    /// <summary>
    /// Picks the settings host. If Jellyfin could not create the plugin instance, a detached host keeps service
    /// resolution from throwing at host start, which would stop the whole server (F3).
    /// </summary>
    /// <param name="instance">The plugin instance, if Jellyfin created one.</param>
    /// <param name="logger">Where the fallback is reported.</param>
    /// <returns>The host to use.</returns>
    internal static IConfigurationHost ResolveConfigurationHost(Plugin? instance, ILogger logger)
    {
        if (instance is not null)
        {
            return instance;
        }

        logger.LogWarning("TgJelly's plugin instance is missing; settings can't be saved until Jellyfin restarts");
        return new DetachedConfigurationHost();
    }
}
