using System;
using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Compat;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Delivery;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.Hosting;
using Jellyfin.Plugin.TgJelly.Intake;
using Jellyfin.Plugin.TgJelly.Intake.Consumers;
using Jellyfin.Plugin.TgJelly.Library;
using Jellyfin.Plugin.TgJelly.Logging;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.Status;
using Jellyfin.Plugin.TgJelly.Strings;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Updates;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Events.Authentication;
using MediaBrowser.Controller.Events.Updates;
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
        serviceCollection.AddRedactedLogger<Rendering.LogoFile>();
        serviceCollection.AddRedactedLogger<InstanceGate>();
        serviceCollection.AddRedactedLogger<PersistenceService>();
        serviceCollection.AddRedactedLogger<HealthService>();
        serviceCollection.AddRedactedLogger<OutboxDelivery>();
        serviceCollection.AddRedactedLogger<OutboxService>();
        serviceCollection.AddRedactedLogger<UpdateRouter>();
        serviceCollection.AddRedactedLogger<Bot.Screens.FlowEngine>();
        serviceCollection.AddRedactedLogger<ManagerNotifier>();
        serviceCollection.AddRedactedLogger<Bot.Flows.OnboardingFlow>();
        serviceCollection.AddRedactedLogger<Bot.Flows.TopicFlow>();
        serviceCollection.AddRedactedLogger<Bot.Flows.DetectedChatFlow>();
        serviceCollection.AddRedactedLogger<Bot.Screens.DestinationScreens>();
        serviceCollection.AddRedactedLogger<BotWorkQueue>();
        serviceCollection.AddRedactedLogger<BotWorker>();
        serviceCollection.AddRedactedLogger<ProfileCalls>();
        serviceCollection.AddRedactedLogger<BotSupervisor>();
        serviceCollection.AddRedactedLogger<BotTokenService>();
        serviceCollection.AddRedactedLogger<DestinationTester>();
        serviceCollection.AddRedactedLogger<Destinations.DestinationCreator>();
        serviceCollection.AddRedactedLogger<Api.DestinationsController>();
        serviceCollection.AddRedactedLogger<Api.DetectedChatsController>();
        serviceCollection.AddRedactedLogger<JellyfinLibrary>();
        serviceCollection.AddRedactedLogger<LibraryWatcher>();
        serviceCollection.AddRedactedLogger<TaskWatcher>();
        serviceCollection.AddRedactedLogger<RestartWatcher>();
        serviceCollection.AddRedactedLogger<StartupWatcher>();
        serviceCollection.AddRedactedLogger<Pipeline.MediaReadiness>();
        serviceCollection.AddRedactedLogger<Pipeline.NotificationPipeline>();
        serviceCollection.AddRedactedLogger<Pipeline.PipelineService>();
        serviceCollection.AddRedactedLogger<Updates.UpdateChecker>();
        serviceCollection.AddRedactedLogger<LoginFailedConsumer>();
        serviceCollection.AddRedactedLogger<LoginSucceededConsumer>();
        serviceCollection.AddRedactedLogger<LockoutConsumer>();
        serviceCollection.AddRedactedLogger<UserCreatedConsumer>();
        serviceCollection.AddRedactedLogger<UserDeletedConsumer>();
        serviceCollection.AddRedactedLogger<PasswordChangedConsumer>();
        serviceCollection.AddRedactedLogger<PluginInstalledConsumer>();
        serviceCollection.AddRedactedLogger<PluginUpdatedConsumer>();
        serviceCollection.AddRedactedLogger<PluginUninstalledConsumer>();
        serviceCollection.AddRedactedLogger<PluginInstallFailedConsumer>();
        serviceCollection.AddRedactedLogger<Templates.TemplateRenderer>();
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
        serviceCollection.AddSingleton<NoticeBuilder>();
        serviceCollection.AddSingleton<DiagnosticsReport>();
        serviceCollection.AddSingleton<PacingGate>();
        serviceCollection.AddSingleton<OutboxDelivery>();
        serviceCollection.AddSingleton<IServerStartup, ServerStartup>();
        serviceCollection.AddSingleton<BotWorkQueue>();
        serviceCollection.AddSingleton<ManagerNotifier>();
        serviceCollection.AddSingleton<Bot.Screens.ScreenSender>();
        serviceCollection.AddSingleton<Bot.Screens.MenuScreens>();
        serviceCollection.AddSingleton<Bot.Screens.AdvancedScreen>();
        serviceCollection.AddSingleton<Bot.Screens.StatusScreen>();
        serviceCollection.AddSingleton<Bot.Screens.DestinationScreens>();
        serviceCollection.AddSingleton<Bot.Screens.SettingsScreens>();
        serviceCollection.AddSingleton<Bot.Flows.ChatCheckRetrier>();
        serviceCollection.AddSingleton<Bot.Flows.TopicFlow>();
        serviceCollection.AddSingleton<Bot.Flows.OnboardingFlow>();
        serviceCollection.AddSingleton<Bot.Flows.DetectedChatFlow>();
        serviceCollection.AddSingleton<IBotFlows, Bot.Screens.FlowEngine>();
        serviceCollection.AddSingleton<IUpdateHandler, UpdateRouter>();
        serviceCollection.AddSingleton<BotWorker>();
        serviceCollection.AddSingleton<ProfileCalls>();
        serviceCollection.AddSingleton<BotSupervisor>();
        serviceCollection.AddSingleton<ISupervisorControl>(sp => sp.GetRequiredService<BotSupervisor>());
        serviceCollection.AddSingleton<BotMemory>();
        serviceCollection.AddSingleton<BotTokenService>();
        serviceCollection.AddSingleton<DestinationTester>();
        serviceCollection.AddSingleton<Destinations.DestinationCreator>();
        serviceCollection.AddSingleton<Destinations.DestinationMigration>();
        serviceCollection.AddSingleton<Destinations.SetupGate>();
        serviceCollection.AddSingleton<Api.DestinationDtoMapper>();
        serviceCollection.AddSingleton<ILibraryAccess, JellyfinLibrary>();
        serviceCollection.AddSingleton<IServerInfo, JellyfinServerInfo>();
        serviceCollection.AddSingleton<Rendering.Formatter>();
        serviceCollection.AddSingleton<Rendering.LinkBuilder>();
        serviceCollection.AddSingleton<Rendering.MediaLayouts>();
        serviceCollection.AddSingleton<Rendering.LogoFile>();
        serviceCollection.AddSingleton<Rendering.AlertLayouts>();
        serviceCollection.AddSingleton<Rendering.NotificationRenderer>();
        serviceCollection.AddSingleton<Templates.TemplateEngine>();
        serviceCollection.AddSingleton<Templates.TemplateModelBuilder>();
        serviceCollection.AddSingleton<Templates.TemplateFlags>();
        serviceCollection.AddSingleton<Templates.TemplateRenderer>();
        serviceCollection.AddSingleton<Templates.TemplateSample>();
        serviceCollection.AddSingleton<IntakeQueues>();
        serviceCollection.AddSingleton<PendingIdSet>();
        serviceCollection.AddSingleton<LibraryWatcher>();
        serviceCollection.AddSingleton<IScanState>(sp => sp.GetRequiredService<LibraryWatcher>());
        serviceCollection.AddSingleton<TaskWatcher>();
        serviceCollection.AddSingleton<RestartWatcher>();
        serviceCollection.AddSingleton<StartupWatcher>();
        serviceCollection.AddSingleton<Pipeline.MediaReadiness>();
        serviceCollection.AddSingleton<Pipeline.MediaAggregator>();
        serviceCollection.AddSingleton<Pipeline.AlertCoalescer>();
        serviceCollection.AddSingleton<Pipeline.NotificationPipeline>();
        serviceCollection.AddSingleton<Pipeline.PipelineService>();
        serviceCollection.AddSingleton<Updates.IReleaseFeed, Updates.GitHubReleaseFeed>();
        serviceCollection.AddSingleton<Updates.UpdateChecker>();

        // Consumers are scoped, like Jellyfin's own and the Webhook plugin's; they take only the queue and a logger (F6).
        serviceCollection.AddScoped<IEventConsumer<AuthenticationRequestEventArgs>, LoginFailedConsumer>();
        serviceCollection.AddScoped<IEventConsumer<AuthenticationResultEventArgs>, LoginSucceededConsumer>();
        serviceCollection.AddScoped<IEventConsumer<UserLockedOutEventArgs>, LockoutConsumer>();
        serviceCollection.AddScoped<IEventConsumer<UserCreatedEventArgs>, UserCreatedConsumer>();
        serviceCollection.AddScoped<IEventConsumer<UserDeletedEventArgs>, UserDeletedConsumer>();
        serviceCollection.AddScoped<IEventConsumer<UserPasswordChangedEventArgs>, PasswordChangedConsumer>();
        serviceCollection.AddScoped<IEventConsumer<PluginInstalledEventArgs>, PluginInstalledConsumer>();
        serviceCollection.AddScoped<IEventConsumer<PluginUpdatedEventArgs>, PluginUpdatedConsumer>();
        serviceCollection.AddScoped<IEventConsumer<PluginUninstalledEventArgs>, PluginUninstalledConsumer>();
        serviceCollection.AddScoped<IEventConsumer<InstallationFailedEventArgs>, PluginInstallFailedConsumer>();

        // Hosted services stop in reverse order (spec §12): the Outbox and health loops first, then the bot (confirming
        // the last update batch), then the watchers unsubscribe so nothing new arrives, then the pipeline drains the
        // intake queues and sends held lockouts to the Outbox, then persistence flushes, and the gate releases
        // state.lock last.
        serviceCollection.AddSingleton<InstanceGate>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<InstanceGate>());
        serviceCollection.AddHostedService<PersistenceService>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<Pipeline.PipelineService>());
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<LibraryWatcher>());
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<TaskWatcher>());
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<RestartWatcher>());
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<StartupWatcher>());
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<Updates.UpdateChecker>());
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<BotWorker>());
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
