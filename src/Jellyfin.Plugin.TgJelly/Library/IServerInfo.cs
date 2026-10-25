using System;

namespace Jellyfin.Plugin.TgJelly.Library;

/// <summary>
/// The server facts messages and links need.
/// </summary>
public interface IServerInfo
{
    /// <summary>Gets the server ID used in web links.</summary>
    string SystemId { get; }

    /// <summary>Gets the server's display name.</summary>
    string ServerName { get; }

    /// <summary>Gets the version text Jellyfin shows (e.g. <c>12.2.0</c>).</summary>
    string VersionString { get; }

    /// <summary>Gets the server version.</summary>
    Version Version { get; }

    /// <summary>Gets Jellyfin's configured base URL (e.g. <c>/jf</c>), or an empty string.</summary>
    string BaseUrl { get; }

    /// <summary>
    /// Returns a candidate public URL (an <c>external=</c> or <c>all=</c> published URL, otherwise Jellyfin's smart
    /// URL); it is validated by <c>LinkBuilder</c>.
    /// </summary>
    /// <returns>The candidate, or <c>null</c>.</returns>
    string? SuggestPublicUrl();
}
