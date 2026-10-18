using System;

namespace Jellyfin.Plugin.TgJelly.Library;

/// <summary>
/// A library (a virtual folder / collection folder).
/// </summary>
/// <param name="Id">The library ID.</param>
/// <param name="Name">The library name.</param>
/// <param name="CollectionType">Its collection type (e.g. <c>movies</c>), when known.</param>
public sealed record LibraryRef(Guid Id, string Name, string? CollectionType);
