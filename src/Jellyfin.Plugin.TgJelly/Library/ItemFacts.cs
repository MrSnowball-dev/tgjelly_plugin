using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TgJelly.Library;

/// <summary>
/// What an item-change callback learns about an item, taken in O(1) without touching the library.
/// </summary>
/// <param name="Id">The item ID.</param>
/// <param name="Kind">The item kind.</param>
/// <param name="PendingId">The ID readiness tracks: the album for <see cref="MediaKind.Audio"/> (<see cref="Guid.Empty"/> when it has none), the item otherwise.</param>
/// <param name="IsVirtual">Whether the item is virtual (e.g. a missing episode).</param>
/// <param name="HasOwner">Whether the item belongs to another item.</param>
/// <param name="IsExtra">Whether the item is an extra (trailer, featurette…).</param>
/// <param name="IsAlternateVersion">Whether the item is an alternate version of another video.</param>
/// <param name="IsItemByName">Whether the item is a person, genre, studio or similar.</param>
/// <param name="DateLastRefreshed">When its metadata was last refreshed; <see cref="DateTime.MinValue"/> until the first refresh succeeds.</param>
/// <param name="Identities">Its identity set.</param>
public sealed record ItemFacts(
    Guid Id,
    MediaKind Kind,
    Guid PendingId,
    bool IsVirtual,
    bool HasOwner,
    bool IsExtra,
    bool IsAlternateVersion,
    bool IsItemByName,
    DateTime DateLastRefreshed,
    IReadOnlyList<string> Identities);
