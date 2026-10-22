using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Library;
using Jellyfin.Plugin.TgJelly.State.Documents;

namespace Jellyfin.Plugin.TgJelly.Rendering;

/// <summary>
/// The poster and buttons of a media message (spec §8.2, §8.3): each group is looked up again from its item IDs, so an
/// update or a resend shows what the library holds now. The text comes from the message's template.
/// </summary>
public sealed class MediaLayouts
{
    private readonly Formatter _formatter;
    private readonly LinkBuilder _links;
    private readonly ILibraryAccess _library;
    private readonly SettingsStore _settings;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaLayouts"/> class.
    /// </summary>
    /// <param name="formatter">The formatter, for the button labels.</param>
    /// <param name="links">The link builder.</param>
    /// <param name="library">The library.</param>
    /// <param name="settings">The settings (message toggles and the public URL).</param>
    public MediaLayouts(Formatter formatter, LinkBuilder links, ILibraryAccess library, SettingsStore settings)
    {
        _formatter = formatter;
        _links = links;
        _library = library;
        _settings = settings;
    }

    /// <summary>
    /// Picks the poster and builds the buttons of one media group.
    /// </summary>
    /// <param name="model">The group's model.</param>
    /// <returns>The message without text, or <c>null</c> when no item of the group exists any more.</returns>
    public OutboxMessage? Render(RenderModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var items = Items(model);
        if (items.Count == 0)
        {
            return null;
        }

        var config = _settings.Current;
        var options = config.Message;
        var layout = items[0].Kind switch
        {
            MediaKind.Episode => Episodes(items),
            MediaKind.MusicAlbum => Album(items[0]),
            MediaKind.AudioBook => Audiobook(model, items),
            _ => Single(items[0]),
        };

        var poster = options.ShowPoster ? PosterPicker.Pick(layout.Posters) : null;
        return new OutboxMessage
        {
            PosterPath = poster?.Path,
            PosterCacheKey = poster?.CacheKey,
            Buttons = Buttons(layout, options, config.PublicUrl),
        };
    }

    private static Guid? ParseId(string id) => Guid.TryParseExact(id, "N", out var guid) ? guid : null;

    private static string? Provider(MediaItem? item, string provider)
    {
        if (item is null)
        {
            return null;
        }

        foreach (var (key, value) in item.ProviderIds)
        {
            if (string.Equals(key, provider, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static Layout Single(MediaItem item)
        => new([(item.Id, item.Primary)], item.Id, item.Kind == MediaKind.Movie ? item : null, Tv: false);

    private static Layout Album(MediaItem album) => new([(album.Id, album.Primary)], album.Id, null, Tv: false);

    private List<MediaItem> Items(RenderModel model)
    {
        var items = new List<MediaItem>(model.ItemIds.Count);
        foreach (var id in model.ItemIds)
        {
            if (ParseId(id) is { } guid && _library.GetItem(guid) is { } item)
            {
                items.Add(item);
            }
        }

        return items;
    }

    private Layout Episodes(List<MediaItem> items)
    {
        var first = items.OrderBy(i => i.EpisodeNumber ?? int.MaxValue).ThenBy(i => i.Name, StringComparer.Ordinal).First();
        var series = first.SeriesId == Guid.Empty ? null : _library.GetItem(first.SeriesId);
        var seasonItem = first.SeasonId == Guid.Empty ? null : _library.GetItem(first.SeasonId);
        return new Layout(
            [(first.SeasonId, seasonItem?.Primary), (first.SeriesId, series?.Primary), (first.Id, first.Primary)],
            series?.Id ?? first.Id,
            series,
            Tv: true);
    }

    private Layout Audiobook(RenderModel model, List<MediaItem> parts)
    {
        var first = parts[0];
        var folderId = ParseId(model.ContainerId) ?? first.ParentId;
        var folder = folderId == Guid.Empty ? null : _library.GetItem(folderId);
        return new Layout([(first.Id, first.Primary), (folderId, folder?.Primary)], folder?.Id ?? first.Id, null, Tv: false);
    }

    private List<List<OutboxButton>> Buttons(Layout layout, MessageOptions options, string publicUrl)
    {
        var rows = new List<List<OutboxButton>>(2);
        if (options.ShowOpenInJellyfin && _links.OpenInJellyfin(publicUrl, layout.OpenId) is { } open)
        {
            rows.Add([new OutboxButton { Text = _formatter.Strings.Get("button.open"), Url = open }]);
        }

        if (options.ShowProviderLinks && layout.Providers is { } providers)
        {
            var row = new List<OutboxButton>(2);
            if (Provider(providers, "Imdb") is { } imdb)
            {
                row.Add(new OutboxButton { Text = _formatter.Strings.Get("button.imdb"), Url = LinkBuilder.Imdb(imdb) });
            }

            if (Provider(providers, "Tmdb") is { } tmdb)
            {
                row.Add(new OutboxButton { Text = _formatter.Strings.Get("button.tmdb"), Url = LinkBuilder.Tmdb(layout.Tv, tmdb) });
            }

            if (row.Count > 0)
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    private sealed record Layout(
        IReadOnlyList<(Guid ItemId, ImageRef? Image)> Posters,
        Guid OpenId,
        MediaItem? Providers,
        bool Tv);
}
