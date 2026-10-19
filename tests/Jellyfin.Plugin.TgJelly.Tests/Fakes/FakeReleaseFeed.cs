using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Updates;

namespace Jellyfin.Plugin.TgJelly.Tests.Fakes;

public sealed class FakeReleaseFeed : IReleaseFeed
{
    public List<GitHubRelease> Releases { get; } = [];

    public Exception? Error { get; set; }

    public int Calls { get; private set; }

    public Task<IReadOnlyList<GitHubRelease>> GetReleasesAsync(CancellationToken ct)
    {
        Calls++;
        return Error is { } error
            ? Task.FromException<IReadOnlyList<GitHubRelease>>(error)
            : Task.FromResult<IReadOnlyList<GitHubRelease>>(Releases.ToList());
    }
}
