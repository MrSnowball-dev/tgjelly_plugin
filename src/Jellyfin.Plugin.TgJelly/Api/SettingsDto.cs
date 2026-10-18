using Jellyfin.Plugin.TgJelly.Configuration;

namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// <c>GET TgJelly/Settings</c> (spec §11.2): the dashboard-editable settings, plus the proxy for display.
/// </summary>
public sealed class SettingsDto
{
    /// <summary>Gets the settings revision, for <c>expectedRevision</c>.</summary>
    public long Revision { get; init; }

    /// <summary>Gets the public server URL.</summary>
    public string PublicUrl { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether the public URL can be used for buttons (spec §8.3).</summary>
    public bool PublicUrlValid { get; init; }

    /// <summary>Gets the message toggles.</summary>
    public MessageOptions Message { get; init; } = new();

    /// <summary>Gets the batching values.</summary>
    public BatchingOptions Batching { get; init; } = new();

    /// <summary>Gets the update-check settings.</summary>
    public UpdateCheckOptions UpdateCheck { get; init; } = new();

    /// <summary>Gets the proxy mode.</summary>
    public string ProxyMode { get; init; } = string.Empty;

    /// <summary>Gets the proxy URL.</summary>
    public string ProxyUrl { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether a proxy username is stored.</summary>
    public bool HasProxyUsername { get; init; }

    /// <summary>Gets a value indicating whether a proxy password is stored.</summary>
    public bool HasProxyPassword { get; init; }
}
