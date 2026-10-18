using System;

namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// Event data for <see cref="SettingsStore.SettingsChanged"/>.
/// </summary>
public sealed class SettingsChangedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsChangedEventArgs"/> class.
    /// </summary>
    /// <param name="configuration">A copy of the new settings.</param>
    public SettingsChangedEventArgs(PluginConfiguration configuration)
    {
        Configuration = configuration;
    }

    /// <summary>Gets a copy of the new settings.</summary>
    public PluginConfiguration Configuration { get; }
}
