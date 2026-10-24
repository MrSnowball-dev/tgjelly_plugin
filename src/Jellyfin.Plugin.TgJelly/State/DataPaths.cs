using System;
using System.IO;

namespace Jellyfin.Plugin.TgJelly.State;

/// <summary>
/// Locations of TgJelly's own files: <c>plugins/configurations/TgJelly/</c> (spec §5.1).
/// </summary>
public sealed class DataPaths
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DataPaths"/> class.
    /// </summary>
    /// <param name="pluginConfigurationsPath">Jellyfin's <c>IApplicationPaths.PluginConfigurationsPath</c>.</param>
    public DataPaths(string pluginConfigurationsPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginConfigurationsPath);
        Root = Path.Combine(pluginConfigurationsPath, "TgJelly");
    }

    /// <summary>Gets the data folder.</summary>
    public string Root { get; }

    /// <summary>Gets the path of secrets.json.</summary>
    public string Secrets => Path.Combine(Root, "secrets.json");

    /// <summary>Gets the path of state.json.</summary>
    public string State => Path.Combine(Root, "state.json");

    /// <summary>Gets the path of queue.json.</summary>
    public string Queue => Path.Combine(Root, "queue.json");

    /// <summary>Gets the path of settings.bak.json.</summary>
    public string SettingsBackup => Path.Combine(Root, "settings.bak.json");

    /// <summary>Gets the path of state.lock.</summary>
    public string Lock => Path.Combine(Root, "state.lock");

    /// <summary>Gets the path of event-log.jsonl, the Log (kept apart from settings, so it never touches <c>Revision</c>).</summary>
    public string EventLog => Path.Combine(Root, "event-log.jsonl");

    /// <summary>Gets the path of logo.png, the poster of Security and Server messages.</summary>
    public string LogoPath => Path.Combine(Root, "logo.png");
}
