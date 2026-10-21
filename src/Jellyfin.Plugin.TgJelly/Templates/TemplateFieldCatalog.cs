using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.TgJelly.Templates;

/// <summary>
/// The fields a template can use for each event key, with sample values (spec §8.5). The model builder produces
/// exactly these names; the docs and the dashboard's field list come from here.
/// </summary>
public static class TemplateFieldCatalog
{
    private static readonly Dictionary<string, IReadOnlyList<TemplateField>> Fields = Build();

    /// <summary>
    /// Returns the fields of an event key: the common ones first.
    /// </summary>
    /// <param name="eventKey">The event key.</param>
    /// <returns>The fields; empty for an unknown key.</returns>
    public static IReadOnlyList<TemplateField> For(string eventKey)
    {
        ArgumentNullException.ThrowIfNull(eventKey);
        return Fields.TryGetValue(eventKey, out var fields) ? fields : [];
    }

    /// <summary>
    /// Returns whether an event key is a media event.
    /// </summary>
    /// <param name="eventKey">The event key.</param>
    /// <returns><c>true</c> for <c>media.*</c>.</returns>
    public static bool IsMedia(string eventKey)
    {
        ArgumentNullException.ThrowIfNull(eventKey);
        return eventKey.StartsWith("media.", StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns a model with a sample value for every field.
    /// </summary>
    /// <param name="eventKey">The event key.</param>
    /// <returns>The model.</returns>
    public static IReadOnlyDictionary<string, object?> SampleFull(string eventKey)
        => For(eventKey).ToDictionary(f => f.Name, f => Copy(f.Sample), StringComparer.Ordinal);

    /// <summary>
    /// Returns a model as it looks when data is missing: optional fields are <c>null</c> and arrays are empty.
    /// </summary>
    /// <param name="eventKey">The event key.</param>
    /// <returns>The model.</returns>
    public static IReadOnlyDictionary<string, object?> SampleSparse(string eventKey)
        => For(eventKey).ToDictionary(
            f => f.Name,
            f => f.Type.EndsWith("[]", StringComparison.Ordinal) ? new List<object?>() : f.Optional ? null : Copy(f.Sample),
            StringComparer.Ordinal);

    private static Dictionary<string, IReadOnlyList<TemplateField>> Build()
    {
        var fields = new Dictionary<string, TemplateField[]>(StringComparer.Ordinal);
        var url = Str("jellyfin_url", "A link that opens the item in Jellyfin. Empty when no public URL is set.", "https://jf.example.com/web/#/details?id=1a2b3c&serverId=a1b2c3", true);
        var library = Str("library", "The library name.", "Movies", true);
        var upgraded = Bool("upgraded", "True when the item replaces an older file.", false);
        var imdb = Str("imdb_id", "The IMDb ID.", "tt2543164", true);
        var tmdb = Str("tmdb_id", "The TMDb ID.", "329865", true);

        var movie = new[]
        {
            Str("title", "The title.", "Arrival", false),
            Num("year", "The production year.", 2016L, true),
            Str("overview", "The overview text.", "A linguist works with the military to talk to alien visitors.", true),
            new TemplateField("rating", "number", "The community rating, from 0 to 10.", 7.9, true),
            Str("rating_text", "The rating with one decimal, e.g. 7.9.", "7.9", true),
            Num("runtime_minutes", "The runtime in minutes.", 116L, true),
            Str("runtime_text", "The runtime as hours and minutes, e.g. 1h 56m.", "1h 56m", true),
            Arr("genres", "The genres.", new List<object?> { "Drama", "Sci-Fi" }),
            library,
            imdb,
            tmdb,
            url,
            upgraded,
        };
        fields["media.movie"] = Media(movie);
        fields["media.musicvideo"] = Media(movie);
        fields["media.episodes"] = Media(
            Str("series", "The series name.", "Severance", true),
            Num("season", "The season number. Specials are 0.", 2L, true),
            Num("count", "How many episodes the message covers.", 3L, false),
            Str("count_text", "The episode count with digit grouping.", "3", false),
            new TemplateField(
                "episodes",
                "episode[]",
                "The episodes, in number order. Each has a number (empty for an episode without one), number_text (the number with two digits, empty without a number) and a title.",
                new List<object?> { Episode(1, "Hello, Ms. Cobel"), Episode(2, "Goodbye, Mrs. Selvig"), Episode(3, "Who Is Alive?") },
                false),
            Num("episodes_more", "How many episodes were left out of the list to fit the message. 0 when none.", 0L, false),
            Bool("new_series", "True when the series is new to the library.", false),
            library,
            imdb,
            tmdb,
            url,
            upgraded);
        fields["media.album"] = Media(
            Str("artist", "The album artist.", "Daft Punk", true),
            Str("album", "The album title.", "Discovery", false),
            Num("year", "The release year.", 2001L, true),
            Num("tracks", "The number of tracks.", 14L, false),
            Str("tracks_text", "The track count with digit grouping.", "14", false),
            library,
            url,
            upgraded);
        fields["media.book"] = Media(
            Str("title", "The title. For an audiobook, the name of the book.", "The Hobbit", false),
            Num("year", "The year.", 1937L, true),
            Str("overview", "The overview text.", "A hobbit sets out on a long journey.", true),
            new TemplateField("rating", "number", "The community rating, from 0 to 10.", 8.2, true),
            Str("rating_text", "The rating with one decimal, e.g. 8.2.", "8.2", true),
            Arr("genres", "The genres.", new List<object?> { "Fantasy", "Adventure" }),
            Num("parts", "The number of audiobook parts. 0 for a book.", 3L, false),
            Str("parts_text", "The part count with digit grouping.", "3", false),
            library,
            url,
            upgraded);

        fields["security.login_failed"] = Alert(
            Str("user", "The user name that was tried.", "ann", false),
            Num("attempts", "How many attempts failed in a row.", 5L, true),
            Str("attempts_text", "The attempt count with digit grouping.", "5", true),
            Num("ip_count", "How many different addresses the attempts came from.", 2L, true),
            Str("ip_count_text", "The address count with digit grouping.", "2", true),
            Str("last_ip", "The address of the latest attempt.", "203.0.113.7", true),
            Str("ip", "The address of the latest attempt. Same as last_ip.", "203.0.113.7", true),
            Str("client", "The client app.", "Jellyfin Web", true),
            Str("device", "The device name.", "Firefox", true),
            Num("last_attempt_unix", "When the latest attempt happened, as Unix seconds.", Sample.AtUnix, false),
            Str("last_attempt_text", "When the latest attempt happened, as text in UTC.", Sample.AtText, false),
            Bool("locked", "True when the account is now locked.", true));
        fields["security.lockout"] = Alert(
            Str("user", "The user name.", "ann", false),
            Num("attempts", "How many attempts failed.", 5L, true),
            Str("attempts_text", "The attempt count with digit grouping.", "5", true));
        fields["security.login_succeeded"] = Alert(
            Str("user", "The user name.", "ann", false),
            Str("client", "The client app.", "Jellyfin Web", true),
            Str("device", "The device name.", "Firefox", true),
            Str("ip", "The address of the sign-in.", "203.0.113.7", true));
        foreach (var key in new[] { "security.user_created", "security.user_deleted", "security.password_changed" })
        {
            fields[key] = Alert(Str("user", "The user name.", "ann", false));
        }

        fields["server.started"] = Alert(
            Str("version", "The Jellyfin version.", "10.11.0", true),
            Str("server", "The server name.", "Home", true));
        fields["server.update_available"] = Alert(
            Str("version", "The new Jellyfin version.", "10.11.1", false),
            Str("current", "The version that is running.", "10.11.0", true),
            Str("url", "A link to the release notes.", "https://github.com/jellyfin/jellyfin/releases/tag/v10.11.1", true));
        var plugin = Str("plugin", "The plugin name.", "TgJelly", false);
        var version = Str("version", "The plugin version.", "1.2.0", true);
        fields["server.plugin_installed"] = Alert(plugin, version);
        fields["server.plugin_uninstalled"] = Alert(plugin, version);
        fields["server.plugin_updated"] = Alert(plugin, version, Str("changelog", "The plugin's change notes.", "Fixed a bug.", true));
        fields["server.plugin_install_failed"] = Alert(plugin, version, Str("error", "The error message.", "The package could not be downloaded.", true));
        fields["server.task_failed"] = Alert(
            Str("task", "The name of the scheduled task.", "Scan Media Library", false),
            Str("error", "The error message.", "The disk is full.", true));
        fields["server.restart_pending"] = Alert(
            Arr("reasons", "Why a restart is needed, as plain text.", new List<object?> { "Plugin TgJelly was updated", "Plugin Foo was installed" }),
            Num("reasons_more", "How many reasons were left out of the list to fit the message. 0 when none.", 0L, false));

        fields[TemplateKeys.Digest] = Media(
            Num("count", "How many items the digest covers.", 4L, false),
            Str("count_text", "The item count with digit grouping.", "4", false),
            new TemplateField(
                "libraries",
                "library[]",
                "The libraries, each with a name (empty for items without a library) and entries. An entry has kind (movie, episode, season, album, audiobook or other), title, year, series, season_text, episode_text (two digits, or ?), count, count_text, artist and album.",
                new List<object?>
                {
                    Library("Movies", Entry("movie", title: "Arrival", year: 2016L)),
                    Library(
                        "Shows",
                        Entry("episode", title: "Who Is Alive?", series: "Severance", season: "02", episode: "03"),
                        Entry("season", series: "Andor", season: "01", count: 12L)),
                    Library(null, Entry("album", title: "Discovery", artist: "Daft Punk", album: "Discovery", count: 14L)),
                },
                false),
            Num("more", "How many entries were left out to fit the message. 0 when none.", 0L, false),
            Str("more_text", "The left-out count with digit grouping.", "0", false));

        var missing = TemplateKeys.All.Except(fields.Keys, StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException("No template fields for " + string.Join(", ", missing));
        }

        return fields.ToDictionary(p => p.Key, p => (IReadOnlyList<TemplateField>)[.. Common(p.Key), .. p.Value], StringComparer.Ordinal);
    }

    /// <summary>
    /// Returns the parts of an event key's model that the fitter may shorten when the message is too long.
    /// </summary>
    /// <param name="eventKey">The event key.</param>
    /// <returns>The text field, the list field and the field for the number of removed list items; each may be null.</returns>
    public static (string? Text, string? List, string? More) Shrink(string eventKey)
    {
        ArgumentNullException.ThrowIfNull(eventKey);
        return eventKey switch
        {
            "media.movie" or "media.musicvideo" or "media.book" => ("overview", null, null),
            "media.episodes" => (null, "episodes", "episodes_more"),
            "server.plugin_updated" => ("changelog", null, null),
            "server.plugin_install_failed" or "server.task_failed" => ("error", null, null),
            "server.restart_pending" => (null, "reasons", "reasons_more"),
            TemplateKeys.Digest => (null, "libraries", "more"),
            _ => (null, null, null),
        };
    }

    private static object? Copy(object? value) => value switch
    {
        IReadOnlyDictionary<string, object?> d => d.ToDictionary(p => p.Key, p => Copy(p.Value), StringComparer.Ordinal),
        IReadOnlyList<object?> l => l.Select(Copy).ToList(),
        _ => value,
    };

    private static TemplateField[] Media(params TemplateField[] fields) => fields;

    private static TemplateField[] Alert(params TemplateField[] fields) => fields;

    private static TemplateField[] Common(string eventKey) =>
    [
        Str("event", "The event key.", eventKey, false),
        Num("at_unix", "When the event happened, as Unix seconds.", Sample.AtUnix, false),
        Str("at_text", "When the event happened, as text in UTC.", Sample.AtText, false),
        new TemplateField("at_html", "string", "When the event happened, as a time each reader sees in their own time zone. Write it as it is: it is not escaped.", new RawHtml(Sample.AtHtml), false),
    ];

    private static Dictionary<string, object?> Episode(long number, string title)
        => new(StringComparer.Ordinal) { ["number"] = number, ["number_text"] = number.ToString("00", System.Globalization.CultureInfo.InvariantCulture), ["title"] = title };

    private static Dictionary<string, object?> Library(string? name, params Dictionary<string, object?>[] entries)
        => new(StringComparer.Ordinal) { ["name"] = name, ["entries"] = entries.Cast<object?>().ToList() };

    private static Dictionary<string, object?> Entry(
        string kind,
        string? title = null,
        long? year = null,
        string? series = null,
        string? season = null,
        string? episode = null,
        long count = 1L,
        string? artist = null,
        string? album = null)
        => new(StringComparer.Ordinal)
        {
            ["kind"] = kind,
            ["title"] = title,
            ["year"] = year,
            ["series"] = series,
            ["season_text"] = season,
            ["episode_text"] = episode,
            ["count"] = count,
            ["count_text"] = count.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("en-US")),
            ["artist"] = artist,
            ["album"] = album,
        };

    private static TemplateField Str(string name, string description, string sample, bool optional) => new(name, "string", description, sample, optional);

    private static TemplateField Num(string name, string description, long sample, bool optional) => new(name, "number", description, sample, optional);

    private static TemplateField Bool(string name, string description, bool sample) => new(name, "bool", description, sample, false);

    private static TemplateField Arr(string name, string description, List<object?> sample) => new(name, "string[]", description, sample, false);

    private static class Sample
    {
        public const string AtText = "14:05 UTC";

        public static readonly string AtHtml = $"<tg-time unix=\"{AtUnix}\" format=\"t\">{AtText}</tg-time>";

        public static readonly long AtUnix = new DateTimeOffset(2026, 10, 20, 14, 5, 0, TimeSpan.Zero).ToUnixTimeSeconds();
    }
}
