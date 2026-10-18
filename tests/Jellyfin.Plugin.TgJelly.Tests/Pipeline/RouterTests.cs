using System;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Pipeline;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Pipeline;

public class RouterTests
{
    private static readonly Guid L1 = Guid.NewGuid();
    private static readonly Guid L2 = Guid.NewGuid();
    private static readonly Guid Gone = Guid.NewGuid();

    [Fact]
    public void ExactKeyMatch_Only()
    {
        var d = Dest(["media.movie"]);

        Assert.Single(Router.Route("media.movie", [L1], [d]));
        Assert.Empty(Router.Route("media.movies", [L1], [d]));
        Assert.Empty(Router.Route("MEDIA.MOVIE", [L1], [d]));
    }

    [Fact]
    public void Disabled_GetsNothing()
    {
        var d = Dest(["server.started"]);
        d.Enabled = false;

        Assert.Empty(Router.Route("server.started", null, [d]));
    }

    [Fact]
    public void LibraryFilter_AppliesToMediaOnly()
    {
        var filtered = Dest(["media.movie", "security.lockout"], [L1.ToString("N")]);
        var all = Dest(["media.movie"]);

        Assert.Equal([all], Router.Route("media.movie", [L2], [filtered, all]));
        Assert.Equal([filtered, all], Router.Route("media.movie", [L1, L2], [filtered, all]));
        Assert.Equal([filtered], Router.Route("security.lockout", null, [filtered, all]));
    }

    [Fact]
    public void RemovedLibraries_MatchNothing_ButAreKept()
    {
        var d = Dest(["media.movie"], [Gone.ToString("N")]);

        Assert.Empty(Router.Route("media.movie", [L1], [d]));
        Assert.Equal([Gone.ToString("N")], d.LibraryIds);
    }

    [Fact]
    public void UnparseableLibraryId_MatchesNothing()
        => Assert.Empty(Router.Route("media.movie", [L1], [Dest(["media.movie"], ["not-a-guid"])]));

    [Fact]
    public void LibraryFilter_RemovedAndRefersOnlyToRemoved()
    {
        var existing = new[] { L1, L2 };
        var mixed = Dest([], [L1.ToString("N"), Gone.ToString("N")]);
        var broken = Dest([], [Gone.ToString("N")]);
        var all = Dest([]);

        Assert.Equal([Gone.ToString("N")], LibraryFilter.Removed(mixed, existing));
        Assert.False(LibraryFilter.RefersOnlyToRemoved(mixed, existing));
        Assert.True(LibraryFilter.RefersOnlyToRemoved(broken, existing));
        Assert.False(LibraryFilter.RefersOnlyToRemoved(all, existing));
        Assert.Empty(LibraryFilter.Removed(all, existing));
    }

    private static Destination Dest(string[] events, string[]? libraries = null) => new()
    {
        Id = Guid.NewGuid().ToString("N")[..8],
        Events = events,
        LibraryIds = libraries ?? [],
    };
}
