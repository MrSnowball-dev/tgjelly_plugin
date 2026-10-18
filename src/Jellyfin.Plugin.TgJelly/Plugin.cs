using System;
using Jellyfin.Plugin.TgJelly.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.TgJelly;

/// <summary>
/// The TgJelly plugin entry point.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>
{
    /// <summary>
    /// The plugin name. Fixed forever (spec §5.1).
    /// </summary>
    public const string PluginName = "TgJelly";

    /// <summary>
    /// The plugin GUID. Fixed forever (spec §5.1).
    /// </summary>
    public static readonly Guid PluginId = new("e547b901-0dc4-42ae-8999-3871dd141270");

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
}
