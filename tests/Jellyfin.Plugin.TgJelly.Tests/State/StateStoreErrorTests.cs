using Jellyfin.Plugin.TgJelly.State;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.State;

public class StateStoreErrorTests
{
    [Fact]
    public void RecordError_KeepsTheNewest20_InOrder()
    {
        using var dir = new TempDir();
        var store = new StateStore(new DataPaths(dir.Path), new FakeTimeProvider(), NullLogger<StateStore>.Instance);
        store.Load();

        for (var i = 0; i < 25; i++)
        {
            store.RecordError("outbox", $"error {i}");
        }

        var errors = store.LastErrorsSnapshot();
        Assert.Equal(StateStore.MaxLastErrors, errors.Count);
        Assert.Equal("error 5", errors[0].Message);
        Assert.Equal("error 24", errors[^1].Message);
    }

    [Fact]
    public void RecordError_BeforeLoad_IsKeptInMemory_AndSurvivesLoad()
    {
        using var dir = new TempDir();
        var store = new StateStore(new DataPaths(dir.Path), new FakeTimeProvider(), NullLogger<StateStore>.Instance);

        store.RecordError("persistence", "disk full");
        Assert.Contains(store.LastErrorsSnapshot(), e => e.Message == "disk full");
        store.Load();

        Assert.Single(store.LastErrorsSnapshot(), e => e.Message == "disk full");
    }

    [Fact]
    public void RecordError_RedactsTokens()
    {
        using var dir = new TempDir();
        var store = new StateStore(new DataPaths(dir.Path), new FakeTimeProvider(), NullLogger<StateStore>.Instance);
        store.Load();

        store.RecordError("bot", "oops 123456789:AAEhBP0av28KrBd9xL4Zq3Wm7Tn5Ys1Cv6QpZ");

        Assert.Equal("oops <token>", Assert.Single(store.LastErrorsSnapshot()).Message);
    }
}
