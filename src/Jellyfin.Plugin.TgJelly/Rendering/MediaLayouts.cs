using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Library;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Telegram.Bot.Extensions;

namespace Jellyfin.Plugin.TgJelly.Rendering;

/// <summary>
/// The default media layouts and the digest (spec §8.1–§8.3): each group is rendered again from its item IDs, so an
/// update or a resend shows what the library holds now.
/// </summary>
public sealed class MediaLayouts
{
    private const string Separator = " · ";

    private readonly Formatter _formatter;
    private readonly LinkBuilder _links;
    private readonly ILibraryAccess _library;
    private readonly SettingsStore _settings;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaLayouts"/> class.
    /// </summary>
    /// <param name="formatter">The formatter.</param>
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
    /// Renders one media group.
    /// </summary>
    /// <param name="model">The group's model.</param>
    /// <returns>The message, or <c>null</c> when no item of the group exists any more.</returns>
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
            MediaKind.Episode => Episodes(model, items),
            MediaKind.MusicAlbum => Album(items[0]),
            MediaKind.AudioBook => Audiobook(model, items),
            _ => Single(items[0], options),
        };

        var tail = new List<string>(2);
        if (model.Upgraded)
        {
            tail.Add(_formatter.Strings.Get("media.upgraded"));
        }

        if (model.LibraryName.Length > 0)
        {
            tail.Add(_formatter.Strings.Get("media.library", ("library", _formatter.Escape(model.LibraryName))));
        }

        var parts = new MessageParts(layout.Head, layout.List, options.ShowOverview ? layout.Overview : null, string.Join('\n', tail));
        var poster = options.ShowPoster ? PosterPicker.Pick(layout.Posters) : null;
        return new OutboxMessage
        {
            Caption = MessageFitter.Fit(parts, MessageFitter.CaptionLimit, More),
            Text = MessageFitter.Fit(parts, MessageFitter.TextLimit, More),
            PosterPath = poster?.Path,
            PosterCacheKey = poster?.CacheKey,
            Buttons = Buttons(layout, options, config.PublicUrl),
        };
    }

    /// <summary>
    /// Renders a digest: text only, one line per library, shortened from the end with "…and N more".
    /// </summary>
    /// <param name="digest">The digest model, whose <see cref="RenderModel.Groups"/> are the groups.</param>
    /// <returns>The message.</returns>
    public OutboxMessage RenderDigest(RenderModel digest)
    {
        ArgumentNullException.ThrowIfNull(digest);
        var entries = new List<(string Library, string Entry)>();
        foreach (var group in digest.Groups)
        {
            var items = Items(group);
            if (items.Count > 0)
            {
                var library = group.LibraryName.Length > 0 ? group.LibraryName : _formatter.Strings.Get("digest.noLibrary");
                entries.Add((library, DigestEntry(items)));
            }
        }

        var head = _formatter.Strings.Plural("digest.heading", entries.Count);
        var budget = MessageFitter.TextLimit - MessageFitter.DelayedReserve;
        var text = Digest(head, entries, entries.Count);
        if (HtmlText.PlainLength(text) > budget)
        {
            var (lo, hi) = (0, entries.Count - 1);
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                if (HtmlText.PlainLength(Digest(head, entries, mid)) <= budget)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            text = Digest(head, entries, lo);
            if (HtmlText.PlainLength(text) > budget)
            {
                text = HtmlText.Truncate(text, budget, "…");
            }
        }

        return new OutboxMessage { Text = text };
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

    private static string Two(int? number) => number is { } n ? n.ToString("00", CultureInfo.InvariantCulture) : "?";

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

    private string More(int count) => _formatter.Strings.Get("media.more", ("count", _formatter.Number(count)));

    private string Heading(string title, int? year) => year is { } y
        ? _formatter.Strings.Get("media.headingYear", ("title", _formatter.Escape(title)), ("year", y))
        : _formatter.Strings.Get("media.heading", ("title", _formatter.Escape(title)));

    private Layout Single(MediaItem item, MessageOptions options)
    {
        var meta = new List<string>(3);
        if (options.ShowRatingGenres && item.CommunityRating is { } rating)
        {
            meta.Add(_formatter.Strings.Get("media.rating", ("rating", _formatter.Rating(rating))));
        }

        if (item.Kind != MediaKind.Book && item.RunTime is { } runtime && runtime > TimeSpan.Zero)
        {
            meta.Add(_formatter.Runtime(runtime));
        }

        if (options.ShowRatingGenres && item.Genres.Count > 0)
        {
            meta.Add(_formatter.Escape(string.Join(", ", item.Genres)));
        }

        var head = Heading(item.Name, item.Year);
        if (meta.Count > 0)
        {
            head += "\n" + string.Join(Separator, meta);
        }

        var providers = item.Kind == MediaKind.Movie ? item : null;
        return new Layout(head, [], item.Overview, [(item.Id, item.Primary)], item.Id, providers, Tv: false);
    }

    private Layout Episodes(RenderModel model, List<MediaItem> items)
    {
        var sorted = items.OrderBy(i => i.EpisodeNumber ?? int.MaxValue).ThenBy(i => i.Name, StringComparer.Ordinal).ToList();
        var first = sorted[0];
        var season = first.SeasonNumber switch
        {
            0 => _formatter.Strings.Get("media.specials"),
            var number => _formatter.Strings.Get("media.season", ("number", number?.ToString(CultureInfo.InvariantCulture) ?? "?")),
        };
        var head = _formatter.Strings.Get(
            "media.episodes.heading",
            ("series", _formatter.Escape(first.SeriesName)),
            ("season", season),
            ("count", _formatter.Strings.Plural("media.episodes.count", sorted.Count)));
        if (model.NewSeries)
        {
            head = _formatter.Strings.Get("media.newSeries") + "\n" + head;
        }

        var list = sorted.Select(i => i.EpisodeNumber is { } n
            ? _formatter.Strings.Get("media.episode", ("number", Two(n)), ("title", _formatter.Escape(i.Name)))
            : _formatter.Strings.Get("media.episodeNoNumber", ("title", _formatter.Escape(i.Name)))).ToList();
        var series = first.SeriesId == Guid.Empty ? null : _library.GetItem(first.SeriesId);
        var seasonItem = first.SeasonId == Guid.Empty ? null : _library.GetItem(first.SeasonId);
        return new Layout(
            head,
            list,
            null,
            [(first.SeasonId, seasonItem?.Primary), (first.SeriesId, series?.Primary), (first.Id, first.Primary)],
            series?.Id ?? first.Id,
            series,
            Tv: true);
    }

    private Layout Album(MediaItem album)
    {
        var args = new (string Name, object? Value)[]
        {
            ("artist", _formatter.Escape(album.AlbumArtist)), ("album", _formatter.Escape(album.Name)), ("year", album.Year),
        };
        var head = _formatter.Strings.Get(album.Year is null ? "media.album.heading" : "media.album.headingYear", args)
            + "\n" + _formatter.Strings.Plural("media.album.tracks", album.ChildCount);
        return new Layout(head, [], null, [(album.Id, album.Primary)], album.Id, null, Tv: false);
    }

    private Layout Audiobook(RenderModel model, List<MediaItem> parts)
    {
        var first = parts[0];
        var folderId = ParseId(model.ContainerId) ?? first.ParentId;
        var folder = folderId == Guid.Empty ? null : _library.GetItem(folderId);
        var head = _formatter.Strings.Get("media.heading", ("title", _formatter.Escape(first.ParentName ?? folder?.Name ?? first.Name)))
            + "\n" + _formatter.Strings.Plural("media.audiobook.parts", parts.Count);
        return new Layout(head, [], null, [(first.Id, first.Primary), (folderId, folder?.Primary)], folder?.Id ?? first.Id, null, Tv: false);
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

    private string DigestEntry(List<MediaItem> items)
    {
        var first = items[0];
        switch (first.Kind)
        {
            case MediaKind.Episode:
                var series = _formatter.Escape(first.SeriesName);
                if (items.Count == 1)
                {
                    return _formatter.Strings.Get("digest.episode", ("series", series), ("season", Two(first.SeasonNumber)), ("episode", Two(first.EpisodeNumber)));
                }

                var entry = _formatter.Strings.Get("digest.season", ("series", series), ("season", Two(first.SeasonNumber)));
                return _formatter.Strings.Plural("digest.episodes", items.Count, ("entry", entry));
            case MediaKind.MusicAlbum:
                return _formatter.Strings.Get("digest.album", ("artist", _formatter.Escape(first.AlbumArtist)), ("album", _formatter.Escape(first.Name)));
            case MediaKind.AudioBook:
                return _formatter.Escape(first.ParentName ?? first.Name);
            default:
                return first.Year is { } year
                    ? _formatter.Strings.Get("digest.titleYear", ("title", _formatter.Escape(first.Name)), ("year", year))
                    : _formatter.Escape(first.Name);
        }
    }

    private string Digest(string head, List<(string Library, string Entry)> entries, int kept)
    {
        var lines = entries.Take(kept)
            .GroupBy(e => e.Library, StringComparer.Ordinal)
            .Select(g => _formatter.Strings.Get("digest.library", ("library", _formatter.Escape(g.Key)), ("entries", string.Join(", ", g.Select(e => e.Entry)))))
            .ToList();
        if (kept < entries.Count)
        {
            lines.Add(_formatter.Strings.Plural("digest.more", entries.Count - kept));
        }

        return head + "\n\n" + string.Join('\n', lines);
    }

    private sealed record Layout(
        string Head,
        IReadOnlyList<string> List,
        string? Overview,
        IReadOnlyList<(Guid ItemId, ImageRef? Image)> Posters,
        Guid OpenId,
        MediaItem? Providers,
        bool Tv);
}
