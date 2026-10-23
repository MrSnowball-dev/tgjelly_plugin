using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Library;
using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Telegram.Bot.Extensions;

namespace Jellyfin.Plugin.TgJelly.Templates;

/// <summary>
/// Builds the data-only template model of a message from its <see cref="RenderModel"/> (spec §8.5): strings, numbers,
/// booleans and arrays of these, never a Jellyfin or Telegram object. The result has exactly the fields of
/// <see cref="TemplateFieldCatalog"/>; missing data is <c>null</c>, or an empty array.
/// </summary>
public sealed class TemplateModelBuilder
{
    private readonly ILibraryAccess _library;
    private readonly LinkBuilder _links;
    private readonly SettingsStore _settings;
    private readonly Formatter _formatter;
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of the <see cref="TemplateModelBuilder"/> class.
    /// </summary>
    /// <param name="library">The library.</param>
    /// <param name="links">The link builder.</param>
    /// <param name="settings">The settings (the public URL).</param>
    /// <param name="formatter">The formatter, for time text.</param>
    /// <param name="time">The clock.</param>
    public TemplateModelBuilder(ILibraryAccess library, LinkBuilder links, SettingsStore settings, Formatter formatter, TimeProvider time)
    {
        _library = library;
        _links = links;
        _settings = settings;
        _formatter = formatter;
        _time = time;
    }

    /// <summary>
    /// Builds the model of a message.
    /// </summary>
    /// <param name="model">What the message is rendered from.</param>
    /// <returns>The template model, with the catalog's fields for the event key in catalog order.</returns>
    public TemplateModel Build(RenderModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["event"] = model.EventKey,
            ["at_unix"] = model.At.ToUnixTimeSeconds(),
            ["at_text"] = Text(model.At),
            ["at_html"] = new RawHtml(_formatter.Time(model.At, _time.GetUtcNow())),
        };
        List<DigestEntry>? entries = null;
        if (model.EventKey == TemplateKeys.Digest)
        {
            entries = DigestEntries(model);
            values["count"] = (long)entries.Count;
            values["count_text"] = _formatter.Number(entries.Count);
            values["libraries"] = Libraries(entries, entries.Count);
            values["more"] = 0L;
            values["more_text"] = _formatter.Number(0);
        }
        else if (TemplateFieldCatalog.IsMedia(model.EventKey))
        {
            AddMedia(values, model);
        }
        else
        {
            AddAlert(values, model);
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in TemplateFieldCatalog.For(model.EventKey))
        {
            result[field.Name] = values.TryGetValue(field.Name, out var value)
                ? value
                : field.Type.EndsWith("[]", StringComparison.Ordinal) ? new List<object?>() : null;
        }

        ApplyToggles(result, _settings.Current.Message.For(model.EventKey));
        if (entries is not null)
        {
            // v0.2 kept the first entries that fit, grouped again by library, and could keep none at all.
            return TemplateModel.Custom(
                result,
                null,
                entries.Count,
                (source, _, kept) =>
                {
                    var keep = Math.Clamp(kept, 0, entries.Count);
                    return new Dictionary<string, object?>(source, StringComparer.Ordinal)
                    {
                        ["libraries"] = Libraries(entries, keep),
                        ["more"] = (long)(entries.Count - keep),
                        ["more_text"] = _formatter.Number(entries.Count - keep),
                    };
                },
                minListKept: 0);
        }

        var (text, list, more) = TemplateFieldCatalog.Shrink(model.EventKey);
        return TemplateModel.Of(result, text, list, more, _formatter.Number);
    }

    /// <summary>
    /// Returns the sample model of an event key, as its message's switches shape it.
    /// </summary>
    /// <param name="eventKey">The event key.</param>
    /// <returns>The sample model.</returns>
    public TemplateModel Sample(string eventKey)
    {
        ArgumentNullException.ThrowIfNull(eventKey);
        var values = new Dictionary<string, object?>(TemplateFieldCatalog.SampleFull(eventKey), StringComparer.Ordinal);
        ApplyToggles(values, _settings.Current.Message.For(eventKey));
        var (text, list, more) = TemplateFieldCatalog.Shrink(eventKey);
        return TemplateModel.Of(values, text, list, more, _formatter.Number);
    }

    private static void ApplyToggles(Dictionary<string, object?> values, MessageSwitches options)
    {
        if (!options.ShowRatingGenres)
        {
            SetIfPresent(values, "rating", null);
            SetIfPresent(values, "rating_text", null);
            SetIfPresent(values, "genres", new List<object?>());
        }

        if (!options.ShowOverview)
        {
            SetIfPresent(values, "overview", null);
        }
    }

    private static void SetIfPresent(Dictionary<string, object?> values, string name, object? value)
    {
        if (values.ContainsKey(name))
        {
            values[name] = value;
        }
    }

    private static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string? Two(long? n) => n is { } v ? v.ToString("00", CultureInfo.InvariantCulture) : null;

    private string? NumberText(long? n) => n is { } v ? _formatter.Number(v) : null;

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

    private static long? Number(string? text)
        => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static string? NonEmpty(string? text) => string.IsNullOrEmpty(text) ? null : text;

    private static Guid? ParseId(string id) => Guid.TryParseExact(id, "N", out var guid) ? guid : null;

    private string Text(DateTimeOffset at) => HtmlText.ToPlain(_formatter.Time(at, _time.GetUtcNow()));

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

    private string? Open(Guid? id) => id is { } guid && guid != Guid.Empty ? _links.OpenInJellyfin(_settings.Current.PublicUrl, guid) : null;

    private void AddMedia(Dictionary<string, object?> values, RenderModel model)
    {
        var items = Items(model);
        var first = items.Count > 0 ? items[0] : null;
        values["library"] = NonEmpty(model.LibraryName);
        values["upgraded"] = model.Upgraded;
        switch (model.EventKey)
        {
            case "media.episodes":
                AddEpisodes(values, model, items);
                return;
            case "media.album":
                values["artist"] = first?.AlbumArtist;
                values["album"] = first?.Name;
                values["year"] = (long?)first?.Year;
                values["tracks"] = first is null ? null : (long)first.ChildCount;
                values["tracks_text"] = NumberText(first is null ? null : first.ChildCount);
                values["jellyfin_url"] = Open(first?.Id);
                return;
            case "media.book":
                AddBook(values, model, items);
                return;
        }

        values["title"] = first?.Name;
        values["year"] = (long?)first?.Year;
        values["overview"] = first?.Overview;
        AddRatingGenres(values, first);
        var hasRuntime = first?.RunTime is { } runtime && runtime > TimeSpan.Zero;
        values["runtime_minutes"] = hasRuntime ? (long)Math.Round(first!.RunTime!.Value.TotalMinutes) : null;
        values["runtime_text"] = hasRuntime ? _formatter.Runtime(first!.RunTime!.Value) : null;
        values["imdb_id"] = Provider(first, "Imdb");
        values["tmdb_id"] = Provider(first, "Tmdb");
        values["jellyfin_url"] = Open(first?.Id);
    }

    private void AddRatingGenres(Dictionary<string, object?> values, MediaItem? item)
    {
        values["rating"] = item?.CommunityRating is { } rating ? Math.Round((double)rating, 1) : null;
        values["rating_text"] = item?.CommunityRating is { } text ? _formatter.Rating(text) : null;
        values["genres"] = item is null ? new List<object?>() : item.Genres.Cast<object?>().ToList();
    }

    private void AddEpisodes(Dictionary<string, object?> values, RenderModel model, List<MediaItem> items)
    {
        var sorted = items.OrderBy(i => i.EpisodeNumber ?? int.MaxValue).ThenBy(i => i.Name, StringComparer.Ordinal).ToList();
        var first = sorted.Count > 0 ? sorted[0] : null;
        var series = first is null || first.SeriesId == Guid.Empty ? null : _library.GetItem(first.SeriesId);
        values["series"] = first?.SeriesName;
        values["season"] = (long?)first?.SeasonNumber;
        values["count"] = (long)sorted.Count;
        values["count_text"] = _formatter.Number(sorted.Count);
        values["episodes"] = sorted
            .Select(i => (object?)new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["number"] = (long?)i.EpisodeNumber,
                ["number_text"] = Two(i.EpisodeNumber),
                ["title"] = i.Name,
            })
            .ToList();
        values["episodes_more"] = 0L;
        values["episodes_more_text"] = _formatter.Number(0);
        values["new_series"] = model.NewSeries;
        values["imdb_id"] = Provider(series, "Imdb");
        values["tmdb_id"] = Provider(series, "Tmdb");
        values["jellyfin_url"] = Open(series?.Id ?? first?.Id);
    }

    private void AddBook(Dictionary<string, object?> values, RenderModel model, List<MediaItem> items)
    {
        var first = items.Count > 0 ? items[0] : null;
        var audiobook = first?.Kind == MediaKind.AudioBook;
        var folderId = first is null ? null : ParseId(model.ContainerId) ?? (first.ParentId == Guid.Empty ? null : first.ParentId);
        var folder = audiobook && folderId is { } id ? _library.GetItem(id) : null;
        values["title"] = audiobook ? first!.ParentName ?? folder?.Name ?? first.Name : first?.Name;
        values["year"] = (long?)first?.Year;
        values["overview"] = first?.Overview;
        AddRatingGenres(values, first);
        var parts = audiobook ? items.Count : 0L;
        values["parts"] = parts;
        values["parts_text"] = _formatter.Number(parts);
        values["jellyfin_url"] = Open(audiobook ? folder?.Id ?? first!.Id : first?.Id);
    }

    // The libraries of the first entries, in order of first appearance; items without a library share the null name.
    private static List<object?> Libraries(List<DigestEntry> entries, int kept)
    {
        var libraries = new List<object?>();
        var byName = new Dictionary<string, List<object?>>(StringComparer.Ordinal);
        List<object?>? unnamed = null;
        foreach (var (library, entry) in entries.Take(kept))
        {
            var list = library is null ? unnamed : byName.GetValueOrDefault(library);
            if (list is null)
            {
                list = [];
                if (library is null)
                {
                    unnamed = list;
                }
                else
                {
                    byName[library] = list;
                }

                libraries.Add(new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = library, ["entries"] = list });
            }

            list.Add(entry);
        }

        return libraries;
    }

    private List<DigestEntry> DigestEntries(RenderModel digest)
    {
        var entries = new List<DigestEntry>(digest.Groups.Count);
        foreach (var group in digest.Groups)
        {
            var items = Items(group);
            if (items.Count > 0)
            {
                entries.Add(new DigestEntry(NonEmpty(group.LibraryName), Entry(items)));
            }
        }

        return entries;
    }

    private Dictionary<string, object?> Entry(List<MediaItem> items)
    {
        var first = items[0];
        (string Kind, string? Title, int? Year) shape = first.Kind switch
        {
            MediaKind.Episode when items.Count == 1 => ("episode", first.Name, null),
            MediaKind.Episode => ("season", null, null),
            MediaKind.MusicAlbum => ("album", first.Name, first.Year),
            MediaKind.AudioBook => ("audiobook", first.ParentName ?? first.Name, null),
            MediaKind.Movie => ("movie", first.Name, first.Year),
            _ => ("other", first.Name, first.Year),
        };
        var (kind, title, year) = shape;
        var episode = first.Kind == MediaKind.Episode;
        var count = kind == "season" ? items.Count : 1;
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["kind"] = kind,
            ["title"] = title,
            ["year"] = (long?)year,
            ["series"] = episode ? first.SeriesName : null,
            ["season_text"] = episode ? Two(first.SeasonNumber) ?? "?" : null,
            ["episode_text"] = kind == "episode" ? Two(first.EpisodeNumber) ?? "?" : null,
            ["count"] = (long)count,
            ["count_text"] = _formatter.Number(count),
            ["artist"] = kind == "album" ? first.AlbumArtist : null,
            ["album"] = kind == "album" ? first.Name : null,
        };
    }

    private void AddAlert(Dictionary<string, object?> values, RenderModel model)
    {
        string? Get(string name) => model.Values.TryGetValue(name, out var value) ? NonEmpty(value) : null;

        foreach (var name in new[] { "user", "client", "device", "ip", "version", "server", "current", "url", "plugin", "task" })
        {
            values[name] = Get(name);
        }

        values["changelog"] = Trimmed(Get("changelog"));
        values["error"] = Trimmed(Get("error"));
        values["attempts"] = Number(Get("attempts"));
        values["attempts_text"] = NumberText(Number(Get("attempts")));
        values["ip_count"] = Number(Get("ip_count"));
        values["ip_count_text"] = NumberText(Number(Get("ip_count")));
        values["last_ip"] = Get("ip");
        values["last_attempt_unix"] = model.At.ToUnixTimeSeconds();
        values["last_attempt_text"] = Text(model.At);
        values["locked"] = string.Equals(Get("locked"), "true", StringComparison.OrdinalIgnoreCase);
        values["reasons"] = model.Lines.Cast<object?>().ToList();
        values["reasons_more"] = 0L;
        values["reasons_more_text"] = _formatter.Number(0);
    }

    private readonly record struct DigestEntry(string? Library, Dictionary<string, object?> Entry);
}
