using System;

namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// Event data for <see cref="IConfigurationHost.ExternalConfigurationSaved"/>.
/// </summary>
public sealed class ConfigurationSavedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationSavedEventArgs"/> class.
    /// </summary>
    /// <param name="configuration">The settings that were saved.</param>
    public ConfigurationSavedEventArgs(PluginConfiguration configuration)
    {
        Configuration = configuration;
    }

    /// <summary>Gets the settings that were saved.</summary>
    public PluginConfiguration Configuration { get; }
}
