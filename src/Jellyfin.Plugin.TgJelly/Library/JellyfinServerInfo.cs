using System;
using System.Net;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;

namespace Jellyfin.Plugin.TgJelly.Library;

/// <summary>
/// <see cref="IServerInfo"/> over Jellyfin's host and network configuration (F23).
/// </summary>
public sealed class JellyfinServerInfo : IServerInfo
{
    private readonly IServerApplicationHost _host;
    private readonly IServerConfigurationManager _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinServerInfo"/> class.
    /// </summary>
    /// <param name="host">The server host.</param>
    /// <param name="config">The server configuration.</param>
    public JellyfinServerInfo(IServerApplicationHost host, IServerConfigurationManager config)
    {
        _host = host;
        _config = config;
    }

    /// <inheritdoc />
    public string SystemId => _host.SystemId;

    /// <inheritdoc />
    public string ServerName => _host.FriendlyName;

    /// <inheritdoc />
    public string VersionString => _host.ApplicationVersionString;

    /// <inheritdoc />
    public Version Version => _host.ApplicationVersion;

    /// <inheritdoc />
    public string BaseUrl => _config.GetNetworkConfiguration().BaseUrl ?? string.Empty;

    /// <inheritdoc />
    public string? SuggestPublicUrl()
    {
        var published = _config.GetNetworkConfiguration().PublishedServerUriBySubnet ?? [];
        foreach (var key in new[] { "external", "all" })
        {
            foreach (var entry in published)
            {
                var eq = entry.IndexOf('=', StringComparison.Ordinal);
                if (eq > 0 && string.Equals(entry[..eq].Trim(), key, StringComparison.OrdinalIgnoreCase))
                {
                    return entry[(eq + 1)..].Trim();
                }
            }
        }

        return _host.GetSmartApiUrl(IPAddress.Parse("8.8.8.8"));
    }
}
