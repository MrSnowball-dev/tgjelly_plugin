using Jellyfin.Plugin.TgJelly.Configuration;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Delivery;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Destinations;

public sealed class DestinationMigrationTests : System.IDisposable
{
    private readonly BotTestHarness _h = new();
    private readonly Outbox _outbox;
    private readonly HealthService _health;
    private readonly DestinationMigration _migration;

    public DestinationMigrationTests()
    {
        _h.Connect();
        var gate = new Jellyfin.Plugin.TgJelly.State.InstanceGate(_h.Paths, _h.Time, NullLogger<Jellyfin.Plugin.TgJelly.State.InstanceGate>.Instance);
        _h.Track(gate);
        _health = new HealthService(gate, _h.Connection, new DestinationChecker(_h.Connection, _h.Strings), _h.Settings, _h.State, _h.Time, NullLogger<HealthService>.Instance);
        _h.Track(_health);
        _outbox = new Outbox(_h.State, _h.Strings, _h.Time);
        _migration = new DestinationMigration(_h.Settings, _outbox, _health);
    }

    public void Dispose() => _h.Dispose();

    [Fact]
    public void Rewrite_MovesEveryTopicOfTheChat()
    {
        var general = _h.AddDestination(-1001, ChatKind.Group);
        var topic = _h.AddDestination(-1001, ChatKind.Group, threadId: 7);
        var unrelated = _h.AddDestination(-1002, ChatKind.Group);

        var first = _migration.Migrate(-1001, -1009);

        Assert.Equal(general.Id, first!.Id);
        var all = _h.Settings.Current.Destinations;
        Assert.All(new[] { general.Id, topic.Id }, id => Assert.Equal((-1009L, ChatKind.Supergroup), (all.Single(d => d.Id == id).ChatId, all.Single(d => d.Id == id).Kind)));
        Assert.Equal((-1002L, ChatKind.Group), (all.Single(d => d.Id == unrelated.Id).ChatId, all.Single(d => d.Id == unrelated.Id).Kind));
    }

    [Fact]
    public void NothingToMigrate_DoesNotBumpTheRevision()
    {
        _h.AddDestination(-1002, ChatKind.Group);
        var revision = _h.Settings.Current.Revision;

        Assert.Null(_migration.Migrate(-1001, -1009));

        Assert.Equal(revision, _h.Settings.Current.Revision);
    }

    [Fact]
    public void Collision_MergesEventsSilentGroupsAndLibraries_RepointsTheOutbox()
    {
        var old = _h.AddDestination(-1001, ChatKind.Group, preset: PresetKind.NewMedia);
        var existing = _h.AddDestination(-1009, preset: PresetKind.AdminAlerts);
        var a = System.Guid.NewGuid().ToString("N");
        var b = System.Guid.NewGuid().ToString("N");
        _h.Settings.Update(null, c =>
        {
            c.Destinations.Single(d => d.Id == old.Id).SilentGroups = ["media"];
            c.Destinations.Single(d => d.Id == old.Id).LibraryIds = [a];
            c.Destinations.Single(d => d.Id == existing.Id).LibraryIds = [b];
        });
        _health.MarkProblem(old.Id, HealthStatus.Ok, string.Empty);
        _outbox.Enqueue(new OutboxEntry { EntryId = "e1", DestinationId = old.Id, EventKey = "media.movie", QueuedAt = _h.Time.GetUtcNow(), Message = new OutboxMessage { Text = "x", Caption = "x" } });

        var result = _migration.Migrate(-1001, -1009);

        Assert.Equal(existing.Id, result!.Id);
        var remaining = Assert.Single(_h.Settings.Current.Destinations);
        Assert.Equal(PresetKind.Custom, remaining.Preset);
        Assert.Contains("media.movie", remaining.Events);
        Assert.Contains("server.started", remaining.Events);
        Assert.Equal(["media"], remaining.SilentGroups);
        Assert.Equal(2, remaining.LibraryIds.Length);
        Assert.Contains(a, remaining.LibraryIds);
        Assert.Contains(b, remaining.LibraryIds);
        Assert.Equal(existing.Id, Assert.Single(_outbox.Snapshot()).DestinationId);
        Assert.False(_h.State.Read(s => s.Health.ContainsKey(old.Id)));
    }

    [Fact]
    public void UnknownChat_ReturnsNull()
    {
        _h.AddDestination(-1001);

        Assert.Null(_migration.Migrate(-5, -9));
        Assert.Equal(-1001, Assert.Single(_h.Settings.Current.Destinations).ChatId);
    }
}
