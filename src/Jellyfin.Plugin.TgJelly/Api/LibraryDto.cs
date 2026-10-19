namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// A library, for the per-destination library filter (spec §11.2).
/// </summary>
public sealed class LibraryDto
{
    /// <summary>Gets the library ID (Guid <c>N</c>).</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets the library name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the collection type (e.g. <c>movies</c>), when known.</summary>
    public string? CollectionType { get; init; }
}
