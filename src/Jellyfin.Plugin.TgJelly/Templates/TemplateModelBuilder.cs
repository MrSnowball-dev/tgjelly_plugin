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
    public IReadOnlyDictionary<string, object?> Build(RenderModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["event"] = model.EventKey,
            ["at_unix"] = model.At.ToUnixTimeSeconds(),
            ["at_text"] = Text(model.At),
        };
        if (TemplateFieldCatalog.IsMedia(model.EventKey))
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

        return result;
    }

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
                values["jellyfin_url"] = Open(first?.Id);
                return;
            case "media.book":
                AddBook(values, model, items);
                return;
        }

        values["title"] = first?.Name;
        values["year"] = (long?)first?.Year;
        values["overview"] = first?.Overview;
        values["rating"] = first?.CommunityRating is { } rating ? Math.Round((double)rating, 1) : null;
        values["runtime_minutes"] = first?.RunTime is { } runtime && runtime > TimeSpan.Zero ? (long)Math.Round(runtime.TotalMinutes) : null;
        values["genres"] = first is null ? new List<object?>() : first.Genres.Cast<object?>().ToList();
        values["imdb_id"] = Provider(first, "Imdb");
        values["tmdb_id"] = Provider(first, "Tmdb");
        values["jellyfin_url"] = Open(first?.Id);
    }

    private void AddEpisodes(Dictionary<string, object?> values, RenderModel model, List<MediaItem> items)
    {
        var sorted = items.OrderBy(i => i.EpisodeNumber ?? int.MaxValue).ThenBy(i => i.Name, StringComparer.Ordinal).ToList();
        var first = sorted.Count > 0 ? sorted[0] : null;
        var series = first is null || first.SeriesId == Guid.Empty ? null : _library.GetItem(first.SeriesId);
        values["series"] = first?.SeriesName;
        values["season"] = (long?)first?.SeasonNumber;
        values["count"] = (long)sorted.Count;
        values["episodes"] = sorted
            .Select(i => (object?)new Dictionary<string, object?>(StringComparer.Ordinal) { ["number"] = (long?)i.EpisodeNumber, ["title"] = i.Name })
            .ToList();
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
        values["parts"] = audiobook ? items.Count : 0L;
        values["jellyfin_url"] = Open(audiobook ? folder?.Id ?? first!.Id : first?.Id);
    }

    private void AddAlert(Dictionary<string, object?> values, RenderModel model)
    {
        string? Get(string name) => model.Values.TryGetValue(name, out var value) ? NonEmpty(value) : null;

        foreach (var name in new[] { "user", "client", "device", "ip", "version", "server", "current", "url", "plugin", "changelog", "error", "task" })
        {
            values[name] = Get(name);
        }

        values["attempts"] = Number(Get("attempts"));
        values["ip_count"] = Number(Get("ip_count"));
        values["last_ip"] = Get("ip");
        values["last_attempt_unix"] = model.At.ToUnixTimeSeconds();
        values["last_attempt_text"] = Text(model.At);
        values["locked"] = string.Equals(Get("locked"), "true", StringComparison.OrdinalIgnoreCase);
        values["reasons"] = model.Lines.Select(l => (object?)HtmlText.ToPlain(l)).ToList();
    }
}
