using System;

namespace Jellyfin.Plugin.TgJelly.Library;

/// <summary>
/// An item's image as Jellyfin records it.
/// </summary>
/// <param name="Path">The file path, or a URL for remote images.</param>
/// <param name="IsLocal">Whether the image is a local file.</param>
/// <param name="DateModified">When the image last changed; part of the file_id cache key.</param>
public sealed record ImageRef(string Path, bool IsLocal, DateTime DateModified);
