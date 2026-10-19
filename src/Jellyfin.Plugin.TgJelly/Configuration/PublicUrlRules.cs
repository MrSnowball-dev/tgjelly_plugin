using System;

namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// The public server URL rule (spec §8.3, F37).
/// </summary>
public static class PublicUrlRules
{
    /// <summary>
    /// Returns whether <paramref name="url"/> can be used for "Open in Jellyfin" buttons: an absolute <c>http</c> or
    /// <c>https</c> URL whose host contains a dot and is not an IPv6 literal. Telegram rejects a whole message whose
    /// button host has no dot (<c>localhost</c>, a Docker service name) or is an IPv6 literal (<c>BUTTON_URL_INVALID</c>,
    /// spec §15 Q2). <c>http</c> and private IPv4 hosts are fine.
    /// </summary>
    /// <param name="url">The URL.</param>
    /// <returns><c>true</c> when valid.</returns>
    public static bool IsValid(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
           && uri.HostNameType != UriHostNameType.IPv6
           && uri.Host.Contains('.', StringComparison.Ordinal);
}
