using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.TgJelly.Updates;

/// <summary>
/// Lists Jellyfin's releases.
/// </summary>
public interface IReleaseFeed
{
    /// <summary>
    /// Reads the latest releases.
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The releases, newest first as GitHub lists them.</returns>
    Task<IReadOnlyList<GitHubRelease>> GetReleasesAsync(CancellationToken ct);
}
