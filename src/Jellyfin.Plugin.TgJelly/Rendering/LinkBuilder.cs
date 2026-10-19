using System;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Library;

namespace Jellyfin.Plugin.TgJelly.Rendering;

/// <summary>
/// Builds the links in message buttons (spec §8.3, F23, F37).
/// </summary>
public sealed class LinkBuilder
{
    private readonly IServerInfo _server;

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkBuilder"/> class.
    /// </summary>
    /// <param name="server">The server info (system ID, base URL, suggestion).</param>
    public LinkBuilder(IServerInfo server) => _server = server;

    /// <summary>
    /// Normalizes a public URL: trimmed, without query, fragment or trailing slash. Invalid URLs (no dot in the host,
    /// not http(s)) give <c>null</c>.
    /// </summary>
    /// <param name="url">The URL.</param>
    /// <returns>The normalized URL, or <c>null</c>.</returns>
    public static string? Normalize(string? url)
    {
        var trimmed = url?.Trim();
        return PublicUrlRules.IsValid(trimmed) ? new Uri(trimmed!).GetLeftPart(UriPartial.Path).TrimEnd('/') : null;
    }

    /// <summary>
    /// The IMDb page of a title.
    /// </summary>
    /// <param name="id">The IMDb ID.</param>
    /// <returns>The URL.</returns>
    public static string Imdb(string id) => "https://www.imdb.com/title/" + Uri.EscapeDataString(id) + "/";

    /// <summary>
    /// The TMDb page of a movie or show.
    /// </summary>
    /// <param name="tv">Whether it is a show.</param>
    /// <param name="id">The TMDb ID.</param>
    /// <returns>The URL.</returns>
    public static string Tmdb(bool tv, string id) => "https://www.themoviedb.org/" + (tv ? "tv/" : "movie/") + Uri.EscapeDataString(id);

    /// <summary>
    /// The "Open in Jellyfin" link of an item. Jellyfin's base URL is appended unless the public URL's path already
    /// ends with it (case-insensitive).
    /// </summary>
    /// <param name="publicUrl">The configured public URL.</param>
    /// <param name="itemId">The item.</param>
    /// <returns>The link, or <c>null</c> when the public URL is empty or invalid.</returns>
    public string? OpenInJellyfin(string publicUrl, Guid itemId)
    {
        if (Normalize(publicUrl) is not { } root)
        {
            return null;
        }

        var baseUrl = _server.BaseUrl.Trim().Trim('/');
        if (baseUrl.Length > 0)
        {
            baseUrl = "/" + baseUrl;
            if (!new Uri(root).AbsolutePath.TrimEnd('/').EndsWith(baseUrl, StringComparison.OrdinalIgnoreCase))
            {
                root += baseUrl;
            }
        }

        return root + "/web/#/details?id=" + itemId.ToString("N") + "&serverId=" + Uri.EscapeDataString(_server.SystemId);
    }

    /// <summary>
    /// The public URL suggested in the dashboard, normalized and validated by the same rule.
    /// </summary>
    /// <returns>The suggestion, or <c>null</c>.</returns>
    public string? Suggest()
    {
        try
        {
            return Normalize(_server.SuggestPublicUrl());
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
