using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Delivery;

namespace Jellyfin.Plugin.TgJelly.Destinations;

/// <summary>
/// Moves a group's destinations to its supergroup chat ID, shared by delivery and the update router.
/// </summary>
public sealed class DestinationMigration
{
    private readonly SettingsStore _settings;
    private readonly Outbox _outbox;
    private readonly HealthService _health;

    /// <summary>
    /// Initializes a new instance of the <see cref="DestinationMigration"/> class.
    /// </summary>
    /// <param name="settings">The settings store.</param>
    /// <param name="outbox">The Outbox, repointed when destinations merge.</param>
    /// <param name="health">The health service.</param>
    public DestinationMigration(SettingsStore settings, Outbox outbox, HealthService health)
    {
        _settings = settings;
        _outbox = outbox;
        _health = health;
    }

    /// <summary>
    /// Rewrites every destination of the old chat to the new chat ID, or merges it into the destination that already
    /// has the new chat and the same topic.
    /// </summary>
    /// <param name="oldChatId">The chat that migrated.</param>
    /// <param name="newChatId">Its new supergroup ID.</param>
    /// <returns>The destination the first one became, or null when none matched.</returns>
    public Destination? Migrate(long oldChatId, long newChatId)
    {
        var map = MigrateAll(oldChatId, newChatId);
        return map.Count == 0 ? null : _settings.Current.Destinations.FirstOrDefault(x => x.Id == map.First().Value);
    }

    /// <summary>
    /// Like <see cref="Migrate"/>, but says what each destination became.
    /// </summary>
    /// <param name="oldChatId">The chat that migrated.</param>
    /// <param name="newChatId">Its new supergroup ID.</param>
    /// <returns>Each migrated destination's old ID mapped to the ID it has now (the same, or the one it merged into).</returns>
    public IReadOnlyDictionary<string, string> MigrateAll(long oldChatId, long newChatId)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        _settings.UpdateIf(null, c =>
        {
            map.Clear();
            foreach (var d in c.Destinations.Where(x => x.ChatId == oldChatId).ToList())
            {
                var other = c.Destinations.FirstOrDefault(x => x.Id != d.Id && x.ChatId == newChatId && x.ThreadId == d.ThreadId);
                if (other is null)
                {
                    d.ChatId = newChatId;
                    d.Kind = ChatKind.Supergroup;
                    map[d.Id] = d.Id;
                    continue;
                }

                other.Events = [.. other.Events.Union(d.Events, StringComparer.Ordinal)];
                other.SilentGroups = [.. other.SilentGroups.Union(d.SilentGroups, StringComparer.Ordinal)];
                other.LibraryIds = other.LibraryIds.Length == 0 || d.LibraryIds.Length == 0 ? [] : [.. other.LibraryIds.Union(d.LibraryIds, StringComparer.Ordinal)];
                other.FollowsNewEvents = EventCatalog.HasAll(other.Events);

                c.Destinations = [.. c.Destinations.Where(x => x.Id != d.Id)];
                map[d.Id] = other.Id;
            }

            return map.Count > 0;
        });

        foreach (var (from, to) in map.Where(p => p.Key != p.Value))
        {
            _outbox.Repoint(from, to);
            _health.Remove(from);
        }

        return map;
    }
}
