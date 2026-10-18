using System;
using System.Globalization;

namespace Jellyfin.Plugin.TgJelly.Compat;

/// <summary>
/// Decides whether this build may run on the server it was loaded into (spec §5.3, F8).
/// </summary>
public static class CompatGuard
{
    /// <summary>
    /// Evaluates this build against the running server version.
    /// </summary>
    /// <param name="serverVersion">The Jellyfin server version (<c>IServerApplicationHost.ApplicationVersion</c>).</param>
    /// <returns>The compatibility result.</returns>
    public static CompatResult Evaluate(Version serverVersion)
        => Evaluate(serverVersion, BuildInfo.TargetJellyfinMajor, BuildInfo.TargetJellyfinLabel);

    /// <summary>
    /// Evaluates a build target against a server version.
    /// </summary>
    /// <param name="serverVersion">The Jellyfin server version.</param>
    /// <param name="buildMajor">The Jellyfin major version the build targets.</param>
    /// <param name="buildLabel">The human-readable Jellyfin line the build targets.</param>
    /// <returns>The compatibility result.</returns>
    public static CompatResult Evaluate(Version serverVersion, int buildMajor, string buildLabel)
    {
        ArgumentNullException.ThrowIfNull(serverVersion);

        if (serverVersion.Major == buildMajor)
        {
            return new CompatResult(true, buildLabel, serverVersion, null);
        }

        var wanted = serverVersion.Major >= 12
            ? serverVersion.Major.ToString(CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{serverVersion.Major}.{serverVersion.Minor}");
        var message = string.Create(
            CultureInfo.InvariantCulture,
            $"This TgJelly build is for Jellyfin {buildLabel}. Update the plugin (Dashboard → Plugins) to get the Jellyfin {wanted} build.");
        return new CompatResult(false, buildLabel, serverVersion, message);
    }
}
