using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Library;

namespace Jellyfin.Plugin.TgJelly.Tests.Fakes;

/// <summary>
/// An in-memory library for pipeline tests.
/// </summary>
public sealed class FakeLibrary : ILibraryAccess
{
    public Dictionary<Guid, MediaItem> Items { get; } = [];

    public Dictionary<Guid, List<LibraryRef>> Libraries { get; } = [];

    public HashSet<Guid> OtherCopies { get; } = [];

    public List<LibraryRef> VirtualFolders { get; } = [];

    public Func<Guid, MediaItem?>? GetItemOverride { get; set; }

    public MediaItem Add(MediaItem item, params LibraryRef[] libraries)
    {
        ArgumentNullException.ThrowIfNull(item);
        Items[item.Id] = item;
        Libraries[item.Id] = [.. libraries];
        return item;
    }

    public MediaItem? GetItem(Guid id) => GetItemOverride is { } o ? o(id) : Items.GetValueOrDefault(id);

    public IReadOnlyList<LibraryRef> GetLibraries(Guid itemId) => Libraries.TryGetValue(itemId, out var l) ? l : [];

    public bool HasOtherCopy(MediaItem item) => item is not null && OtherCopies.Contains(item.Id);

    public IReadOnlyList<LibraryRef> GetVirtualFolders() => VirtualFolders.ToList();
}
