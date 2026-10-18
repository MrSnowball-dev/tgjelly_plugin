namespace Jellyfin.Plugin.TgJelly.Rendering;

/// <summary>
/// A poster to upload.
/// </summary>
/// <param name="Path">The local file.</param>
/// <param name="CacheKey">The file_id cache key: <c>{itemId:N}:{DateModified.Ticks}</c>.</param>
public sealed record PosterRef(string Path, string CacheKey);
