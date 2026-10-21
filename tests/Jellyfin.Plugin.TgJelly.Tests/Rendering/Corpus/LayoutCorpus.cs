using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Intake;
using Jellyfin.Plugin.TgJelly.Library;
using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Templates;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Rendering.Corpus;

/// <summary>One frozen input of the message layouts.</summary>
internal sealed record CorpusCase(string Name, RenderModel Model, IReadOnlyList<MediaItem> Items, MessageOptions Options, DateTimeOffset Now);

/// <summary>Renders corpus cases through the production renderer.</summary>
internal static class CorpusRig
{
    /// <summary>Renders a case through the production renderer and its built-in templates.</summary>
    public static (string Caption, string Text) RenderTemplates(CorpusCase c)
    {
        ArgumentNullException.ThrowIfNull(c);
        using var h = new BotTestHarness();
        var (library, time, formatter, link) = Setup(h, c);
        var engine = new TemplateEngine(TimeProvider.System);
        var models = new TemplateModelBuilder(library, link, h.Settings, formatter, time);
        var flags = new TemplateFlags(h.State, h.Settings, time);
        var renderer = new NotificationRenderer(
            new MediaLayouts(formatter, link, library, h.Settings),
            new AlertLayouts(formatter),
            new TemplateRenderer(engine, models, flags, h.Settings, h.Strings, NullLogger<TemplateRenderer>.Instance));
        var message = renderer.Render(c.Model, new Destination { Id = "d1" })
            ?? throw new InvalidOperationException($"Case {c.Name} rendered nothing.");
        Assert.Empty(h.State.Read(s => s.Status.TemplateFlags.Keys.ToList()));
        return (message.Caption, message.Text);
    }

    private static (FakeLibrary Library, FakeTimeProvider Time, Formatter Formatter, LinkBuilder Link) Setup(BotTestHarness h, CorpusCase c)
    {
        var library = new FakeLibrary();
        foreach (var item in c.Items)
        {
            library.Add(item);
        }

        var saved = h.Settings.Update(null, s =>
        {
            s.PublicUrl = "https://jf.example.com";
            s.Message.ShowPoster = c.Options.ShowPoster;
            s.Message.ShowOverview = c.Options.ShowOverview;
            s.Message.ShowRatingGenres = c.Options.ShowRatingGenres;
            s.Message.ShowOpenInJellyfin = c.Options.ShowOpenInJellyfin;
            s.Message.ShowProviderLinks = c.Options.ShowProviderLinks;
            s.Message.AnnounceUpgrades = c.Options.AnnounceUpgrades;
        });
        Assert.Equal(SettingsUpdateStatus.Saved, saved.Status);
        return (library, new FakeTimeProvider(c.Now), new Formatter(h.Strings), new LinkBuilder(new FakeServerInfo()));
    }
}

internal static class LayoutCorpus
{
    private static readonly DateTimeOffset At = new(2026, 10, 18, 14, 5, 9, TimeSpan.Zero);
    private static readonly DateTimeOffset SameDay = At + TimeSpan.FromHours(1);
    private static readonly DateTimeOffset OtherDay = At + TimeSpan.FromDays(3);
    private static readonly Lazy<IReadOnlyList<CorpusCase>> All = new(Build);

    public static IReadOnlyList<CorpusCase> Cases => All.Value;

    public static string GoldenPath(string caseName, string part)
        => Path.Combine(AppContext.BaseDirectory, "Rendering", "Golden", "Corpus", $"{caseName}.{part}.txt");

    private static List<CorpusCase> Build()
    {
        var cases = new List<CorpusCase>();
        AddMovies(cases);
        AddOtherMedia(cases);
        AddEpisodes(cases);
        AddDigests(cases);
        AddAlerts(cases);
        var dup = cases.GroupBy(c => c.Name).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null)
        {
            throw new InvalidOperationException($"Duplicate corpus case {dup.Key}.");
        }

        return cases;
    }

    private static string Words(int length, string prefix = "word")
    {
        var sb = new StringBuilder();
        for (var i = 1; sb.Length < length; i++)
        {
            sb.Append(prefix).Append(i.ToString("0000", System.Globalization.CultureInfo.InvariantCulture)).Append(' ');
        }

        return sb.ToString(0, length).TrimEnd();
    }

    private static Guid NewId(ref int counter) => new(++counter, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1);

    private sealed class Ids
    {
        private int _n;

        public Guid Next() => NewId(ref _n);
    }

    private static MediaItem Movie(Ids ids, string name = "Arrival", string? overview = "A linguist works with the military to talk to alien visitors.", int? year = 2016, float? rating = 7.9f, int? minutes = 116, string[]? genres = null)
        => new()
        {
            Id = ids.Next(), Kind = MediaKind.Movie, Name = name, Year = year, Overview = overview, CommunityRating = rating,
            RunTime = minutes is { } m ? TimeSpan.FromMinutes(m) : null, Genres = genres ?? ["Drama", "Sci-Fi"],
            ProviderIds = new Dictionary<string, string> { ["Imdb"] = "tt2543164", ["Tmdb"] = "329865" },
        };

    private static RenderModel Group(string library, string eventKey, params MediaItem[] items) => new()
    {
        Kind = RenderKind.Media, EventKey = eventKey, LibraryName = library, At = At, ItemIds = items.Select(i => i.Id.ToString("N")).ToList(),
    };

    private static RenderModel DigestOf(List<RenderModel> groups) => new() { Kind = RenderKind.Digest, EventKey = "media.digest", At = At, Groups = groups };

    private static CorpusCase Add(string name, RenderModel model, IEnumerable<MediaItem> items, MessageOptions? options = null, DateTimeOffset? now = null)
        => new(name, model, items.ToList(), options ?? new MessageOptions(), now ?? SameDay);

    private static void AddMovies(List<CorpusCase> cases)
    {
        void Movie1(string name, Func<Ids, MediaItem> make, MessageOptions? options = null, Action<RenderModel>? tweak = null, string library = "Movies")
        {
            var ids = new Ids();
            var movie = make(ids);
            var model = Group(library, "media.movie", movie);
            tweak?.Invoke(model);
            cases.Add(Add(name, model, [movie], options));
        }

        Movie1("movie-full", i => Movie(i));
        Movie1("movie-no-year", i => Movie(i, year: null));
        Movie1("movie-no-rating", i => Movie(i, rating: null));
        Movie1("movie-no-genres", i => Movie(i, genres: []));
        Movie1("movie-no-runtime", i => Movie(i, minutes: null));
        Movie1("movie-overview-off", i => Movie(i), new MessageOptions { ShowOverview = false });
        Movie1("movie-rating-genres-off", i => Movie(i), new MessageOptions { ShowRatingGenres = false });
        Movie1("movie-poster-off", i => Movie(i), new MessageOptions { ShowPoster = false });
        Movie1("movie-overview-3000", i => Movie(i, overview: Words(3000)));
        Movie1("movie-overview-5000", i => Movie(i, overview: Words(5000)));
        Movie1("movie-overview-20000", i => Movie(i, overview: Words(20000)));
        Movie1("movie-overview-dropped", i => Movie(i, name: Words(915, "title"), overview: Words(300)));
        Movie1("movie-upgraded", i => Movie(i), tweak: m => m.Upgraded = true);
        Movie1("movie-no-library", i => Movie(i), library: string.Empty);
        Movie1("movie-hostile-title", i => Movie(i, name: "Tom & Jerry <b>\"Live\"</b> > 5", overview: "R&D <script> & \"quotes\""));
    }

    private static void AddOtherMedia(List<CorpusCase> cases)
    {
        var ids = new Ids();
        var video = new MediaItem
        {
            Id = ids.Next(), Kind = MediaKind.MusicVideo, Name = "Paranoid Android", Year = 1997, Overview = "A video.",
            CommunityRating = 8.5f, RunTime = TimeSpan.FromMinutes(6), Genres = ["Rock"], AlbumArtist = "Radiohead",
        };
        cases.Add(Add("musicvideo-full", Group("Music Videos", "media.musicvideo", video), [video]));

        ids = new Ids();
        var book = new MediaItem
        {
            Id = ids.Next(), Kind = MediaKind.Book, Name = "Dune", Year = 1965, Overview = "A desert planet and a boy.",
            CommunityRating = 8.7f, Genres = ["Sci-Fi"], ProviderIds = new Dictionary<string, string> { ["Isbn"] = "9780441013593" },
        };
        cases.Add(Add("book-full", Group("Books", "media.book", book), [book]));
        var noOverview = new MediaItem { Id = ids.Next(), Kind = MediaKind.Book, Name = "Emma", Year = 1815, Genres = ["Classic"] };
        cases.Add(Add("book-no-overview", Group("Books", "media.book", noOverview), [noOverview]));

        // Audiobooks.
        ids = new Ids();
        var folder = new MediaItem { Id = ids.Next(), Kind = MediaKind.Other, Name = "The Hobbit (folder)" };
        var parts = Enumerable.Range(1, 3)
            .Select(i => new MediaItem { Id = ids.Next(), Kind = MediaKind.AudioBook, Name = $"Part {i}", ParentId = folder.Id, ParentName = "The Hobbit" })
            .ToList();
        var withFolder = Group("Audiobooks", "media.book", [.. parts]);
        withFolder.ContainerId = folder.Id.ToString("N");
        cases.Add(Add("audiobook-three-parts-folder", withFolder, [folder, .. parts]));

        var noFolder = Group("Audiobooks", "media.book", [.. parts]);
        cases.Add(Add("audiobook-no-folder-parent-name", noFolder, parts));

        var anon = parts.Select(p => new MediaItem { Id = p.Id, Kind = p.Kind, Name = p.Name }).ToList();
        cases.Add(Add("audiobook-no-folder-item-name", Group("Audiobooks", "media.book", [.. anon]), anon));

        // Albums.
        MediaItem Album(string name, int? year, int tracks) => new()
        {
            Id = new Ids().Next(), Kind = MediaKind.MusicAlbum, Name = name, AlbumArtist = "Radiohead", Year = year, ChildCount = tracks,
        };

        var withYear = Album("OK Computer", 1997, 12);
        cases.Add(Add("album-year", Group("Music", "media.album", withYear), [withYear]));
        var noYear = Album("Kid A", null, 10);
        cases.Add(Add("album-no-year", Group("Music", "media.album", noYear), [noYear]));
        var single = Album("Pablo Honey", 1993, 1);
        cases.Add(Add("album-one-track", Group("Music", "media.album", single), [single]));
    }

    private sealed class Show
    {
        public Show(Ids ids, string name = "Severance", string seasonName = "Season 2")
        {
            Series = new MediaItem { Id = ids.Next(), Kind = MediaKind.Series, Name = name };
            Season = new MediaItem { Id = ids.Next(), Kind = MediaKind.Other, Name = seasonName };
            Ids = ids;
        }

        public MediaItem Series { get; }

        public MediaItem Season { get; }

        public Ids Ids { get; }

        public MediaItem Episode(int? number, string title, int? seasonNumber = 2) => new()
        {
            Id = Ids.Next(), Kind = MediaKind.Episode, Name = title, SeriesId = Series.Id, SeriesName = Series.Name, SeasonId = Season.Id,
            SeasonNumber = seasonNumber, EpisodeNumber = number,
        };

        public CorpusCase Case(string name, List<MediaItem> episodes, bool newSeries = false)
        {
            var model = Group("Shows", "media.episodes", [.. episodes]);
            model.NewSeries = newSeries;
            return Add(name, model, [Series, Season, .. episodes]);
        }
    }

    private static void AddEpisodes(List<CorpusCase> cases)
    {
        var s = new Show(new Ids());
        cases.Add(s.Case("episodes-three", [s.Episode(1, "Hello, Ms. Cobel"), s.Episode(2, "Goodbye, Mrs. Selvig"), s.Episode(3, "Who Is Alive?")]));

        s = new Show(new Ids());
        cases.Add(s.Case("episodes-new-series", [s.Episode(1, "Good News About Hell"), s.Episode(2, "Half Loop")], newSeries: true));

        s = new Show(new Ids(), seasonName: "Specials");
        cases.Add(s.Case("episodes-season-zero", [s.Episode(1, "Behind the scenes", 0), s.Episode(2, "Deleted & <scenes>", 0)]));

        s = new Show(new Ids());
        cases.Add(s.Case("episodes-null-season", [s.Episode(1, "One", null), s.Episode(2, "Two", null)]));

        s = new Show(new Ids());
        cases.Add(s.Case("episodes-unnumbered", [s.Episode(1, "Hello, Ms. Cobel"), s.Episode(null, "Bonus"), s.Episode(3, "Who Is Alive?")]));

        s = new Show(new Ids());
        cases.Add(s.Case("episodes-unsorted", [s.Episode(10, "Ten"), s.Episode(2, "Two"), s.Episode(1, "One"), s.Episode(9, "Nine")]));

        s = new Show(new Ids());
        cases.Add(s.Case("episodes-300", Enumerable.Range(1, 300).Select(i => s.Episode(i, $"Episode title that goes on and on number {i}")).ToList()));

        s = new Show(new Ids());
        cases.Add(s.Case("episodes-1500", Enumerable.Range(1, 1500).Select(i => s.Episode(i, $"Ep {i}")).ToList()));
    }

    private static void AddDigests(List<CorpusCase> cases)
    {
        var ids = new Ids();
        var show = new Show(ids);
        var bear = new Show(ids, "The Bear", "Season 3");
        var multi = Enumerable.Range(1, 8).Select(i => show.Episode(i, $"Episode {i}")).ToList();
        var bearEp = bear.Episode(5, "Ice Chips", 3);
        var arrival = Movie(ids);
        var dune = Movie(ids, "Dune", year: 2021);
        var primer = Movie(ids, "Primer", year: null);
        var orphan = Movie(ids, "A & B", year: 2000);
        var album = new MediaItem { Id = ids.Next(), Kind = MediaKind.MusicAlbum, Name = "OK Computer", AlbumArtist = "Radiohead", Year = 1997, ChildCount = 12 };
        var folder = new MediaItem { Id = ids.Next(), Kind = MediaKind.Other, Name = "The Hobbit (folder)" };
        var book = new MediaItem { Id = ids.Next(), Kind = MediaKind.AudioBook, Name = "Part 1", ParentId = folder.Id, ParentName = "The Hobbit" };
        var book2 = new MediaItem { Id = ids.Next(), Kind = MediaKind.AudioBook, Name = "Part 2", ParentId = folder.Id, ParentName = "The Hobbit" };
        var all = new List<MediaItem> { show.Series, show.Season, bear.Series, bear.Season, folder, album, book, book2, bearEp, arrival, dune, primer, orphan };
        all.AddRange(multi);

        var audiobook = Group("Audiobooks", "media.book", book, book2);
        audiobook.ContainerId = folder.Id.ToString("N");

        cases.Add(Add(
            "digest-three-groups-two-libraries-other",
            DigestOf([Group("Movies", "media.movie", arrival), Group("Shows", "media.episodes", [.. multi]), Group(string.Empty, "media.movie", orphan)]),
            all));
        cases.Add(Add("digest-single-episode", DigestOf([Group("Shows", "media.episodes", bearEp), Group("Movies", "media.movie", arrival)]), all));
        cases.Add(Add("digest-multi-episode", DigestOf([Group("Shows", "media.episodes", [.. multi]), Group("Movies", "media.movie", arrival)]), all));
        cases.Add(Add("digest-album", DigestOf([Group("Music", "media.album", album), Group("Movies", "media.movie", arrival)]), all));
        cases.Add(Add("digest-audiobook", DigestOf([audiobook, Group("Movies", "media.movie", arrival)]), all));
        cases.Add(Add("digest-year", DigestOf([Group("Movies", "media.movie", dune), Group("Movies", "media.movie", primer)]), all));

        cases.Add(DigestMovies("digest-overflow-60", 60, i => $"A very long movie title number {i:000} " + new string('x', 60)));
        cases.Add(DigestMovies("digest-1200", 1200, i => $"Movie {i}"));
        AddMoreDigests(cases);
    }

    private static void AddMoreDigests(List<CorpusCase> cases)
    {
        // Episodes with unknown or zero season and episode numbers.
        var ids = new Ids();
        var show = new Show(ids);
        var lone = show.Episode(null, "Lone", null);
        var unknown = new[] { show.Episode(1, "One", null), show.Episode(2, "Two", null) };
        var specials = new[] { show.Episode(1, "Extra 1", 0), show.Episode(2, "Extra 2", 0) };
        var zeroOne = show.Episode(0, "Pilot", 0);
        cases.Add(Add(
            "digest-episode-unknown-numbers",
            DigestOf([Group("Shows", "media.episodes", lone), Group("Shows", "media.episodes", unknown), Group("Shows", "media.episodes", specials), Group("Shows", "media.episodes", zeroOne)]),
            [show.Series, show.Season, lone, zeroOne, .. unknown, .. specials]));

        // Audiobook parts without a parent name, a book, a music video without a year, a group whose items are all
        // gone and an episodes group with one item left.
        ids = new Ids();
        show = new Show(ids, "Andor", "Season 1");
        var left = show.Episode(4, "Aldhani", 1);
        var goneEpisode = show.Episode(5, "The Axe Forgets", 1);
        var part = new MediaItem { Id = ids.Next(), Kind = MediaKind.AudioBook, Name = "Chapter One" };
        var book = new MediaItem { Id = ids.Next(), Kind = MediaKind.Book, Name = "Emma", Year = 1815 };
        var video = new MediaItem { Id = ids.Next(), Kind = MediaKind.MusicVideo, Name = "Karma Police", AlbumArtist = "Radiohead" };
        var ghost = Movie(ids, "Ghost");
        cases.Add(Add(
            "digest-mixed-kinds",
            DigestOf([
                Group("Audiobooks", "media.book", part),
                Group("Movies", "media.movie", ghost),
                Group("Books", "media.book", book),
                Group("Music Videos", "media.musicvideo", video),
                Group("Shows", "media.episodes", left, goneEpisode),
            ]),
            [show.Series, show.Season, left, part, book, video]));

        // Libraries interleaved: grouped in order of first appearance, names escaped.
        ids = new Ids();
        var a = Movie(ids, "Alpha", year: 2001);
        var b = Movie(ids, "Beta", year: 2002);
        var c = Movie(ids, "Gamma", year: 2003);
        var d = Movie(ids, "Delta", year: 2004);
        cases.Add(Add(
            "digest-interleaved-libraries",
            DigestOf([Group("Kids & <Teens>", "media.movie", a), Group("Movies", "media.movie", b), Group("Kids & <Teens>", "media.movie", c), Group(string.Empty, "media.movie", d)]),
            [a, b, c, d]));

        // One entry alone is longer than the message: v0.2 keeps no entry at all.
        ids = new Ids();
        var huge = Movie(ids, Words(5000, "title"), year: 2000);
        var small = Movie(ids, "Small", year: 2000);
        cases.Add(Add("digest-huge-first-entry", DigestOf([Group("Movies", "media.movie", huge), Group("Movies", "media.movie", small)]), [huge, small]));
        ids = new Ids();
        var alone = Movie(ids, Words(5000, "title"), year: 2000);
        cases.Add(Add("digest-huge-only-entry", DigestOf([Group("Movies", "media.movie", alone)]), [alone]));

        // Every group is gone.
        ids = new Ids();
        var gone = Movie(ids, "Gone");
        cases.Add(Add("digest-all-gone", DigestOf([Group("Movies", "media.movie", gone)]), []));

        // Exactly one entry left out.
        cases.Add(DigestMovies("digest-overflow-one-more", 39, i => $"A very long movie title number {i:000} " + new string('x', 60)));
    }

    private static CorpusCase DigestMovies(string name, int count, Func<int, string> title)
    {
        var ids = new Ids();
        var movies = Enumerable.Range(1, count).Select(i => Movie(ids, title(i), year: 2000)).ToList();
        return Add(name, DigestOf(movies.Select(m => Group("Movies", "media.movie", m)).ToList()), movies);
    }

    private static CorpusCase Alert(string name, string key, Dictionary<string, string> values, DateTimeOffset? now = null, List<string>? lines = null)
    {
        var model = new RenderModel { Kind = RenderKind.Alert, EventKey = key, At = At, Values = values, Lines = lines ?? [] };
        return Add(name, model, [], now: now);
    }

    private static void AddAlerts(List<CorpusCase> cases)
    {
        Dictionary<string, string> Login(string attempts, string ips, bool locked) => new()
        {
            [EventFields.User] = "alice", [EventFields.Attempts] = attempts, [EventFields.IpCount] = ips, [EventFields.Ip] = "203.0.113.5",
            [EventFields.Client] = "Infuse", [EventFields.Device] = "iPhone", [EventFields.Locked] = locked ? "true" : "false",
        };

        cases.Add(Alert("login-failed-one", "security.login_failed", Login("1", "1", false)));
        cases.Add(Alert("login-failed-five-two-ips-locked", "security.login_failed", Login("5", "2", true)));
        cases.Add(Alert("login-failed-today", "security.login_failed", Login("1", "1", false), SameDay));
        cases.Add(Alert("login-failed-other-day", "security.login_failed", Login("1", "1", false), OtherDay));
        cases.Add(Alert("lockout-one", "security.lockout", new() { [EventFields.User] = "bob", [EventFields.Attempts] = "1" }));
        cases.Add(Alert("lockout-five", "security.lockout", new() { [EventFields.User] = "bob", [EventFields.Attempts] = "5" }));
        cases.Add(Alert("login-succeeded-ip", "security.login_succeeded", new()
        {
            [EventFields.User] = "alice", [EventFields.Client] = "Infuse", [EventFields.Device] = "iPhone", [EventFields.Ip] = "203.0.113.5",
        }));
        cases.Add(Alert("login-succeeded-no-ip", "security.login_succeeded", new()
        {
            [EventFields.User] = "alice", [EventFields.Client] = "Infuse", [EventFields.Device] = "iPhone",
        }));
        cases.Add(Alert("login-succeeded-bare-ip", "security.login_succeeded", new() { [EventFields.User] = "alice", [EventFields.Ip] = "203.0.113.5" }));
        cases.Add(Alert("login-succeeded-bare", "security.login_succeeded", new() { [EventFields.User] = "alice" }));
        cases.Add(Alert("login-succeeded-client-only", "security.login_succeeded", new() { [EventFields.User] = "alice", [EventFields.Client] = "Infuse" }));
        cases.Add(Alert("user-created", "security.user_created", new() { [EventFields.User] = "carol <&>" }));
        cases.Add(Alert("user-deleted", "security.user_deleted", new() { [EventFields.User] = "carol" }));
        cases.Add(Alert("password-changed", "security.password_changed", new() { [EventFields.User] = "carol" }));
        cases.Add(Alert("server-started", "server.started", new() { [EventFields.Version] = "12.2.0", [EventFields.Server] = "Home" }));
        cases.Add(Alert("update-available-url", "server.update_available", new()
        {
            [EventFields.Version] = "12.3.0", [EventFields.Current] = "12.2.0", [EventFields.Url] = "https://github.com/jellyfin/jellyfin/releases/tag/v12.3.0",
        }));
        cases.Add(Alert("update-available-no-url", "server.update_available", new() { [EventFields.Version] = "12.3.0", [EventFields.Current] = "12.2.0" }));
        cases.Add(Alert("plugin-installed", "server.plugin_installed", new() { [EventFields.Plugin] = "Trakt", [EventFields.Version] = "26.0.0.0" }));
        cases.Add(Alert("plugin-uninstalled", "server.plugin_uninstalled", new() { [EventFields.Plugin] = "Trakt", [EventFields.Version] = "26.0.0.0" }));
        cases.Add(Alert("plugin-updated-changelog-2000-lines", "server.plugin_updated", new()
        {
            [EventFields.Plugin] = "Trakt", [EventFields.Version] = "26.0.0.0",
            [EventFields.Changelog] = string.Join('\n', Enumerable.Range(1, 2000).Select(i => $"- change {i} with <tags> & more")),
        }));
        cases.Add(Alert("plugin-updated-changelog-6000", "server.plugin_updated", new()
        {
            [EventFields.Plugin] = "Trakt", [EventFields.Version] = "26.0.0.0", [EventFields.Changelog] = Words(6000, "fix&"),
        }));
        cases.Add(Alert("plugin-updated-blank-changelog", "server.plugin_updated", new()
        {
            [EventFields.Plugin] = "Trakt", [EventFields.Version] = "26.0.0.0", [EventFields.Changelog] = "  \n\t  ",
        }));
        cases.Add(Alert("plugin-install-failed", "server.plugin_install_failed", new()
        {
            [EventFields.Plugin] = "Trakt", [EventFields.Version] = "26.0.0.0", [EventFields.Error] = "Checksum <mismatch> & retry",
        }));
        cases.Add(Alert("task-failed-error", "server.task_failed", new() { [EventFields.Task] = "Scan media library", [EventFields.Error] = "Disk is <full> & read-only" }));
        cases.Add(Alert("task-failed-no-error", "server.task_failed", new() { [EventFields.Task] = "Scan media library" }));
        cases.Add(Alert("restart-pending-two", "server.restart_pending", new(), lines: ["Plugin Trakt updated", "Plugin <Other> & installed"]));
        cases.Add(Alert("restart-pending-400", "server.restart_pending", new(), lines: Enumerable.Range(1, 400).Select(i => $"Plugin number {i} changed").ToList()));
    }
}
