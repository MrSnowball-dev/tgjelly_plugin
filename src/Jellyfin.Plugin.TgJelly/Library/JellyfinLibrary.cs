using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.Library;

/// <summary>
/// <see cref="ILibraryAccess"/> over <see cref="ILibraryManager"/>. Reads only; never calls anything that writes items
/// or images.
/// </summary>
public sealed class JellyfinLibrary : ILibraryAccess
{
    private static readonly string[] UpgradeProviders = ["Imdb", "Tmdb", "Tvdb", "MusicBrainzAlbum"];

    private readonly ILibraryManager _library;
    private readonly ILogger<JellyfinLibrary> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinLibrary"/> class.
    /// </summary>
    /// <param name="library">Jellyfin's library manager.</param>
    /// <param name="logger">The logger.</param>
    public JellyfinLibrary(ILibraryManager library, ILogger<JellyfinLibrary> logger)
    {
        _library = library;
        _logger = logger;
    }

    /// <inheritdoc />
    public MediaItem? GetItem(Guid id)
    {
        try
        {
            return _library.GetItemById(id) is { } item ? Map(item) : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TgJelly could not read library item {Item}", id);
            return null;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<LibraryRef> GetLibraries(Guid itemId)
    {
        try
        {
            return _library.GetItemById(itemId) is { } item
                ? [.. _library.GetCollectionFolders(item).Select(f => new LibraryRef(f.Id, f.Name, null))]
                : [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TgJelly could not read the libraries of item {Item}", itemId);
            return [];
        }
    }

    /// <inheritdoc />
    public bool HasOtherCopy(MediaItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Identities.Count == 0 || KindOf(item.Kind) is not { } kind)
        {
            return false;
        }

        var providers = item.ProviderIds
            .Where(p => UpgradeProviders.Contains(p.Key, StringComparer.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(p.Value))
            .ToDictionary(p => p.Key, p => p.Value.Trim(), StringComparer.OrdinalIgnoreCase);
        if (providers.Count > 0 && _library.GetCount(new InternalItemsQuery
        {
            IncludeItemTypes = [kind],
            HasAnyProviderId = providers,
            IsVirtualItem = false,
            ExcludeItemIds = [item.Id],
            Recursive = true,
        }) > 0)
        {
            return true;
        }

        return item.Kind == MediaKind.Episode && item.SeriesId != Guid.Empty && item.SeasonNumber is { } season && item.EpisodeNumber is { } episode
            && _library.GetCount(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Episode],
                AncestorIds = [item.SeriesId],
                ParentIndexNumber = season,
                IndexNumber = episode,
                IsVirtualItem = false,
                ExcludeItemIds = [item.Id],
                Recursive = true,
            }) > 0;
    }

    /// <inheritdoc />
    public IReadOnlyList<LibraryRef> GetVirtualFolders()
    {
        var result = new List<LibraryRef>();
        foreach (var folder in _library.GetVirtualFolders())
        {
            if (Guid.TryParse(folder.ItemId, out var id))
            {
                result.Add(new LibraryRef(id, folder.Name, folder.CollectionType?.ToString()));
            }
        }

        return result;
    }

    private static BaseItemKind? KindOf(MediaKind kind) => kind switch
    {
        MediaKind.Movie => BaseItemKind.Movie,
        MediaKind.Episode => BaseItemKind.Episode,
        MediaKind.MusicAlbum => BaseItemKind.MusicAlbum,
        MediaKind.MusicVideo => BaseItemKind.MusicVideo,
        MediaKind.Book => BaseItemKind.Book,
        MediaKind.AudioBook => BaseItemKind.AudioBook,
        _ => null,
    };

    private MediaItem Map(BaseItem item)
    {
        var facts = Compat.JellyfinItems.Describe(item, _ => Guid.Empty);
        var episode = item as Episode;
        var image = item.GetImageInfo(ImageType.Primary, 0);
        return new MediaItem
        {
            Id = item.Id,
            Kind = facts.Kind,
            Name = item.Name ?? string.Empty,
            Year = item.ProductionYear,
            Overview = item.Overview,
            CommunityRating = item.CommunityRating,
            RunTime = item.RunTimeTicks is { } ticks ? TimeSpan.FromTicks(ticks) : null,
            Genres = item.Genres ?? [],
            ProviderIds = item.ProviderIds is { } ids ? new Dictionary<string, string>(ids, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(),
            SeriesId = episode?.SeriesId ?? Guid.Empty,
            SeriesName = episode?.SeriesName,
            SeasonId = episode?.SeasonId ?? Guid.Empty,
            SeasonNumber = episode is null ? null : item.ParentIndexNumber,
            EpisodeNumber = episode is null ? null : item.IndexNumber,
            AlbumArtist = (item as MusicAlbum)?.AlbumArtist,
            ParentId = item.ParentId,
            ParentName = item.ParentId == Guid.Empty ? null : _library.GetItemById(item.ParentId)?.Name,
            ChildCount = item is MusicAlbum ? CountTracks(item.Id) : 0,
            Primary = image is null ? null : new ImageRef(image.Path, image.IsLocalFile, image.DateModified),
            Identities = facts.Identities,
        };
    }

    private int CountTracks(Guid albumId) => _library.GetCount(new InternalItemsQuery
    {
        AncestorIds = [albumId],
        IncludeItemTypes = [BaseItemKind.Audio],
        IsVirtualItem = false,
        Recursive = true,
    });
}
