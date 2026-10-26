using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TgJelly.Library;

/// <summary>
/// Read-only access to Jellyfin's library for the pipeline and the layouts.
/// </summary>
public interface ILibraryAccess
{
    /// <summary>
    /// Reads an item.
    /// </summary>
    /// <param name="id">The item ID.</param>
    /// <returns>The item, or <c>null</c> when it doesn't exist (or can't be read).</returns>
    MediaItem? GetItem(Guid id);

    /// <summary>
    /// Returns the libraries an item belongs to.
    /// </summary>
    /// <param name="itemId">The item ID.</param>
    /// <returns>The libraries.</returns>
    IReadOnlyList<LibraryRef> GetLibraries(Guid itemId);

    /// <summary>
    /// Returns whether another non-virtual item of the same type shares a provider ID with the item, or (for episodes)
    /// its series, season and number.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns><c>true</c> when another copy exists.</returns>
    bool HasOtherCopy(MediaItem item);

    /// <summary>
    /// Returns every library.
    /// </summary>
    /// <returns>The libraries.</returns>
    IReadOnlyList<LibraryRef> GetVirtualFolders();
}
