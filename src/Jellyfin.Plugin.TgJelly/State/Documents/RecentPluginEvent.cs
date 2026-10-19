using System;

namespace Jellyfin.Plugin.TgJelly.State.Documents;

/// <summary>
/// A plugin event kept for 10 minutes as a possible reason for a pending restart (spec §7.5).
/// </summary>
public sealed class RecentPluginEvent
{
    /// <summary>Gets or sets when it happened.</summary>
    public DateTimeOffset At { get; set; }

    /// <summary>Gets or sets the event key (<c>server.plugin_*</c>).</summary>
    public string EventKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the plugin name.</summary>
    public string Plugin { get; set; } = string.Empty;

    /// <summary>Gets or sets the plugin version.</summary>
    public string Version { get; set; } = string.Empty;
}
