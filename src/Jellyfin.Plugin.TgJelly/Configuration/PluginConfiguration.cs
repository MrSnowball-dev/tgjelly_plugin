using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// TgJelly settings, stored by Jellyfin as XML (spec §10.1). Task 3 adds the remaining fields.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the settings schema version.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// Gets or sets the settings revision, increased on every save.
    /// </summary>
    public long Revision { get; set; }
}
