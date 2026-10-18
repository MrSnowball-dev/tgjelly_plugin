using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TgJelly.Intake;

/// <summary>
/// The IDs waiting for readiness (spec §7.4 step 2), shared between <c>MediaReadiness</c> (which owns and persists
/// them) and the ItemUpdated callback (which reads them in O(1)). Thread-safe.
/// </summary>
public sealed class PendingIdSet
{
    private readonly ConcurrentDictionary<Guid, byte> _ids = new();

    /// <summary>Gets the number of pending IDs.</summary>
    public int Count => _ids.Count;

    /// <summary>
    /// Adds an ID.
    /// </summary>
    /// <param name="id">The ID.</param>
    public void Add(Guid id) => _ids.TryAdd(id, 0);

    /// <summary>
    /// Returns whether an ID is pending.
    /// </summary>
    /// <param name="id">The ID.</param>
    /// <returns><c>true</c> when pending.</returns>
    public bool Contains(Guid id) => _ids.ContainsKey(id);

    /// <summary>
    /// Removes an ID.
    /// </summary>
    /// <param name="id">The ID.</param>
    public void Remove(Guid id) => _ids.TryRemove(id, out _);

    /// <summary>
    /// Replaces the contents (e.g. from queue.json at start).
    /// </summary>
    /// <param name="ids">The IDs.</param>
    public void Reset(IEnumerable<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        _ids.Clear();
        foreach (var id in ids)
        {
            _ids.TryAdd(id, 0);
        }
    }
}
