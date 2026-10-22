using System;
using System.Collections.Generic;
using System.Threading;
using Jellyfin.Plugin.TgJelly.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.TgJelly;

/// <summary>
/// The TgJelly plugin entry point.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IConfigurationHost, IHasWebPages
{
    /// <summary>
    /// The plugin name. Fixed forever (spec §5.1).
    /// </summary>
    public const string PluginName = "TgJelly";

    /// <summary>
    /// The plugin GUID. Fixed forever (spec §5.1).
    /// </summary>
    public static readonly Guid PluginId = new("e547b901-0dc4-42ae-8999-3871dd141270");

    // True only while SettingsStore is saving, so outside saves can be told apart (spec §10.4).
    private static readonly AsyncLocal<bool> SavingFromStore = new();

    private readonly Lock _saveGate = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public event EventHandler<ConfigurationSavedEventArgs>? ExternalConfigurationSaved;

    /// <summary>
    /// Gets the current plugin instance. Set by Jellyfin when it creates the plugin.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => PluginName;

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public override string Description => "Telegram notifications for Jellyfin.";

    /// <summary>
    /// Lists the dashboard pages (spec §11.1). The HTML page must come first: the plugin's Settings button
    /// opens the first page, and a JS page there would crash the view.
    /// </summary>
    /// <returns>The page descriptors.</returns>
    public IEnumerable<PluginPageInfo> GetPages()
    {
        var resourcePrefix = typeof(Plugin).Namespace + ".Web.";
        return
        [
            new PluginPageInfo
            {
                Name = "tgjelly",
                DisplayName = "Telegram",
                EmbeddedResourcePath = resourcePrefix + "tgjelly.html",
                EnableInMainMenu = true,
                MenuIcon = "send",
            },
            new PluginPageInfo
            {
                Name = "tgjelly.js",
                EmbeddedResourcePath = resourcePrefix + "tgjelly.js",
            },
            new PluginPageInfo
            {
                Name = "tgjelly-ui.js",
                EmbeddedResourcePath = resourcePrefix + "tgjelly-ui.js",
            },
            new PluginPageInfo
            {
                Name = "tgjelly.css",
                EmbeddedResourcePath = resourcePrefix + "tgjelly.css",
            },
        ];
    }

    /// <inheritdoc />
    public T RunExclusive<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_saveGate)
        {
            return action();
        }
    }

    /// <inheritdoc />
    public void SaveFromStore(PluginConfiguration configuration)
    {
        SavingFromStore.Value = true;
        try
        {
            UpdateConfiguration(configuration);
        }
        finally
        {
            SavingFromStore.Value = false;
        }
    }

    /// <inheritdoc />
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (SavingFromStore.Value)
        {
            SaveOrRollBack(configuration);
            return;
        }

        // An outside save (e.g. Jellyfin's generic configuration endpoint, which replaces the whole object):
        // serialise it with SettingsStore's saves, fill nulls with defaults, and keep revisions increasing.
        var incoming = (PluginConfiguration)configuration;
        lock (_saveGate)
        {
            incoming.Normalize();
            // Clamped, so a hostile or broken revision near long.MaxValue can't wrap later saves around and
            // suppress every SettingsChanged after them.
            var current = Configuration.Revision;
            incoming.Revision = Math.Min(Math.Max(current, incoming.Revision), PluginConfiguration.MaxOutsideRevision) + 1;
            SaveOrRollBack(incoming);
        }

        ExternalConfigurationSaved?.Invoke(this, new ConfigurationSavedEventArgs(incoming));
    }

    // Jellyfin replaces the live settings before it writes the XML. If the write fails, put the previous settings
    // back, so memory never runs ahead of disk and of SettingsChanged.
    private void SaveOrRollBack(BasePluginConfiguration configuration)
    {
        var previous = Configuration;
        try
        {
            base.UpdateConfiguration(configuration);
        }
        catch
        {
            Configuration = previous;
            throw;
        }
    }
}
