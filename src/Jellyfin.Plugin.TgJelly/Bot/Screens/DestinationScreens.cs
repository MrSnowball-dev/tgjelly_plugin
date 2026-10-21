using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Delivery;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.Library;
using Jellyfin.Plugin.TgJelly.Pipeline;
using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Strings;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Types.ReplyMarkups;

namespace Jellyfin.Plugin.TgJelly.Bot.Screens;

/// <summary>
/// The destination list, the per-destination screens and their switches (spec §6.3), and the <c>/test</c> picker. Every
/// change re-reads the settings and applies itself to the destination found by ID at save time, so a change made in the
/// dashboard while a screen is open is not lost.
/// </summary>
public sealed class DestinationScreens
{
    private readonly SettingsStore _settings;
    private readonly HealthService _health;
    private readonly DestinationTester _tester;
    private readonly ILibraryAccess _library;
    private readonly Outbox _outbox;
    private readonly BotConnection _connection;
    private readonly StringTable _strings;
    private readonly Formatter _formatter;
    private readonly ILogger<DestinationScreens> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DestinationScreens"/> class.
    /// </summary>
    /// <param name="settings">The settings store.</param>
    /// <param name="health">The health service.</param>
    /// <param name="tester">Sends test messages.</param>
    /// <param name="library">The library, for the library switches.</param>
    /// <param name="outbox">The Outbox, to forget a removed destination's entries.</param>
    /// <param name="connection">The bot connection, to leave a chat.</param>
    /// <param name="strings">The string table.</param>
    /// <param name="formatter">The formatter, for escaping.</param>
    /// <param name="logger">The logger.</param>
    public DestinationScreens(SettingsStore settings, HealthService health, DestinationTester tester, ILibraryAccess library, Outbox outbox, BotConnection connection, StringTable strings, Formatter formatter, ILogger<DestinationScreens> logger)
    {
        _settings = settings;
        _health = health;
        _tester = tester;
        _library = library;
        _outbox = outbox;
        _connection = connection;
        _strings = strings;
        _formatter = formatter;
        _logger = logger;
    }

    /// <summary>
    /// Runs a destination button and builds the screen to show.
    /// </summary>
    /// <param name="action">The callback action.</param>
    /// <param name="args">The callback arguments.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>The screen, or <c>null</c> when <paramref name="action"/> is not a destination action.</returns>
    public async Task<Screen?> HandleAsync(string action, IReadOnlyList<string> args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(args);
        var id = args.Count > 0 ? args[0] : string.Empty;
        var arg = args.Count > 1 ? args[1] : string.Empty;
        switch (action)
        {
            case "dl":
                return List(PageOf(args.Count > 0 ? args[0] : null));
            case "tp":
                return Picker(PageOf(args.Count > 0 ? args[0] : null));
            case "d":
                return Detail(id);
            case "de":
                return Events(id, PageOf(arg));
            case "det":
                return Events(id, ToggleEvent(id, arg));
            case "dlb":
                return Libraries(id, PageOf(arg));
            case "dlt":
                return Libraries(id, ToggleLibrary(id, arg));
            case "dsg":
                return SilentGroups(id);
            case "dsgt":
                Change(id, d => d.SilentGroups = Toggle(d.SilentGroups, arg, EventCatalog.Groups.Contains(arg)));
                return SilentGroups(id);
            case "dt":
                return await TestAsync(id, ct).ConfigureAwait(false);
            case "dc":
                return await CheckAsync(id, ct).ConfigureAwait(false);
            case "dp":
                Change(id, d => d.Enabled = !d.Enabled);
                return Detail(id);
            case "dr":
                return ConfirmRemove(id);
            case "drx":
                return await RemoveAsync(id, arg == "1", ct).ConfigureAwait(false);
            default:
                return null;
        }
    }

    /// <summary>
    /// Builds one page of the destination list.
    /// </summary>
    /// <param name="page">The zero-based page.</param>
    /// <returns>The screen.</returns>
    public Screen List(int page) => Listing(page, "menu.destinations.title", "d", "dl", "m");

    /// <summary>
    /// Builds one page of the <c>/test</c> picker.
    /// </summary>
    /// <param name="page">The zero-based page.</param>
    /// <returns>The screen.</returns>
    public Screen Picker(int page) => Listing(page, "dest.picker.title", "dt", "tp", "m");

    private static int PageOf(string? text)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var page) ? page : 0;

    private static string[] Toggle(string[] values, string value, bool valid)
    {
        if (!valid)
        {
            return values;
        }

        return values.Contains(value, StringComparer.Ordinal) ? [.. values.Where(v => v != value)] : [.. values, value];
    }

    private static InlineKeyboardButton[] Row(string text, string action, params string[] args)
        => [InlineKeyboardButton.WithCallbackData(text, CallbackData.Format(action, args))];

    private Screen Listing(int page, string titleKey, string pick, string pageAction, string backAction)
    {
        var destinations = _settings.Current.Destinations;
        var shown = Paging.Page(destinations, page);
        var html = new StringBuilder("<b>").Append(Icons.Heading(_strings, _formatter, titleKey)).Append("</b>");
        if (destinations.Length == 0)
        {
            html.Append("\n\n").Append(_strings.Get("menu.destinations.empty"));
        }

        foreach (var destination in shown)
        {
            html.Append('\n').Append(_formatter.Escape(Badge(destination) + " " + destination.Title));
        }

        var rows = shown.Select(d => Row(MenuScreens.Label(Badge(d) + " " + d.Title), pick, d.Id)).ToList();
        if (Paging.NavRow(page, destinations.Length, p => CallbackData.Format(pageAction, p.ToString(CultureInfo.InvariantCulture)), _strings.Get("menu.prev"), _strings.Get("menu.next")) is { } nav)
        {
            rows.Add(nav);
        }

        rows.Add([Button("menu.back", backAction)]);
        return new Screen(html.ToString(), new InlineKeyboardMarkup(rows));
    }

    private Screen Detail(string id, string? note = null)
    {
        if (Find(id) is not { } d)
        {
            return Gone();
        }

        var health = _health.Get(d.Id);
        var sends = DestinationTester.GroupsText(_strings, d.Events);
        var html = new StringBuilder("<b>").Append(_formatter.Escape(d.Title)).Append("</b>\n");
        html.Append(_formatter.Escape(_strings.Get("dest.line.kind", ("kind", _strings.Get("dest.kind." + d.Kind))))).Append('\n');
        html.Append(_formatter.Escape(_strings.Get("dest.line.status", ("status", HealthText(health))))).Append('\n');
        html.Append(_formatter.Escape(_strings.Get("dest.line.preset", ("preset", _strings.Get("dest.preset." + d.Preset))))).Append('\n');
        html.Append(_formatter.Escape(_strings.Get("dest.line.groups", ("groups", sends)))).Append('\n');
        html.Append(_formatter.Escape(_strings.Get("dest.line.enabled", ("state", _strings.Get(d.Enabled ? "dest.enabled.yes" : "dest.enabled.no")))));
        if (note is not null)
        {
            html.Append("\n\n").Append(note);
        }

        var rows = new List<InlineKeyboardButton[]>
        {
            new[] { Button("dest.btn.events", "de", d.Id, "0"), Button("dest.btn.libraries", "dlb", d.Id, "0") },
            new[] { Button("dest.btn.silent", "dsg", d.Id), Button("dest.btn.test", "dt", d.Id) },
            new[] { Button("dest.btn.check", "dc", d.Id), Button(d.Enabled ? "dest.btn.pause" : "dest.btn.resume", "dp", d.Id) },
            new[] { Button("dest.btn.remove", "dr", d.Id) },
            new[] { Button("menu.back", "dl", "0") },
        };
        return new Screen(html.ToString(), new InlineKeyboardMarkup(rows));
    }

    private Screen Events(string id, int page)
    {
        if (Find(id) is not { } d)
        {
            return Gone();
        }

        var keys = EventCatalog.AllKeys;
        var shown = Paging.Page(keys, page);
        var rows = shown
            .Select(k => Row(Mark(d.Events.Contains(k, StringComparer.Ordinal)) + " " + _strings.Get("dest.event." + k), "det", d.Id, k))
            .ToList();
        AddNav(rows, page, keys.Count, "de", d.Id);
        rows.Add([Button("menu.back", "d", d.Id)]);
        return new Screen(Heading("dest.events.title", d), new InlineKeyboardMarkup(rows));
    }

    private Screen Libraries(string id, int page)
    {
        if (Find(id) is not { } d)
        {
            return Gone();
        }

        var folders = _library.GetVirtualFolders();
        var removed = LibraryFilter.Removed(d, [.. folders.Select(f => f.Id)]);
        var html = new StringBuilder(Heading("dest.libraries.title", d)).Append("\n\n").Append(_strings.Get("dest.libraries.hint"));
        foreach (var gone in removed)
        {
            html.Append('\n').Append(_formatter.Escape(_strings.Get("dest.libraries.removed", ("id", gone))));
        }

        var all = Button("dest.libraries.all", "dlt", d.Id, "all");
        all.Text = Mark(d.LibraryIds.Length == 0) + " " + all.Text;
        var rows = new List<InlineKeyboardButton[]> { new[] { all } };
        rows.AddRange(Paging.Page(folders, page).Select(f => Row(MenuScreens.Label(Mark(d.LibraryIds.Contains(f.Id.ToString("N"), StringComparer.Ordinal)) + " " + f.Name), "dlt", d.Id, f.Id.ToString("N"))));
        AddNav(rows, page, folders.Count, "dlb", d.Id);
        rows.Add([Button("menu.back", "d", d.Id)]);
        return new Screen(html.ToString(), new InlineKeyboardMarkup(rows));
    }

    private Screen SilentGroups(string id)
    {
        if (Find(id) is not { } d)
        {
            return Gone();
        }

        var html = Heading("dest.silent.title", d) + "\n\n" + _strings.Get("dest.silent.hint");
        var rows = EventCatalog.Groups
            .Select(g => Row((d.SilentGroups.Contains(g, StringComparer.Ordinal) ? "🔕" : "🔔") + " " + _strings.Get("groups." + g), "dsgt", d.Id, g))
            .ToList();
        rows.Add([Button("menu.back", "d", d.Id)]);
        return new Screen(html, new InlineKeyboardMarkup(rows));
    }

    private Screen ConfirmRemove(string id)
    {
        if (Find(id) is not { } d)
        {
            return Gone();
        }

        var html = "<b>" + _formatter.Escape(d.Title) + "</b>\n\n" + _formatter.Escape(_strings.Get("dest.remove.ask"));
        var rows = new List<InlineKeyboardButton[]>
        {
            new[] { Button("dest.btn.remove", "drx", d.Id, "0"), Button("dest.btn.removeLeave", "drx", d.Id, "1") },
            new[] { Button("dest.btn.cancel", "d", d.Id) },
        };
        return new Screen(html, new InlineKeyboardMarkup(rows));
    }

    private async Task<Screen> TestAsync(string id, CancellationToken ct)
    {
        if (Find(id) is not { } d)
        {
            return Gone();
        }

        var result = await _tester.SendTestAsync(d, ct).ConfigureAwait(false);
        var note = result.Sent ? _strings.Get("dest.test.ok") : _formatter.Escape(_strings.Get("dest.test.failed", ("error", result.Error ?? string.Empty)));
        return Detail(id, note);
    }

    private async Task<Screen> CheckAsync(string id, CancellationToken ct)
    {
        if (Find(id) is null)
        {
            return Gone();
        }

        try
        {
            await _health.CheckNowAsync(id, ct).ConfigureAwait(false);
            return Detail(id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TgJelly could not re-check a destination");
            return Detail(id, _formatter.Escape(_strings.Get("token.unreachable")));
        }
    }

    private async Task<Screen> RemoveAsync(string id, bool leave, CancellationToken ct)
    {
        Destination? removed = null;
        var result = _settings.Update(null, c =>
        {
            removed = c.Destinations.FirstOrDefault(d => d.Id == id);
            c.Destinations = [.. c.Destinations.Where(d => d.Id != id)];
        });
        if (removed is null)
        {
            return Gone();
        }

        _outbox.RemoveDestination(id);
        _health.Remove(id);
        if (leave && result.Configuration.Destinations.All(d => d.ChatId != removed.ChatId) && _connection.Sender is { } sender)
        {
            try
            {
                await sender.LeaveChatAsync(removed.ChatId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "TgJelly could not leave {Chat}", removed.Title);
            }
        }

        return List(0);
    }

    /// <summary>Flips an event and returns the page the event is on.</summary>
    private int ToggleEvent(string id, string key)
    {
        if (!EventCatalog.IsKnown(key))
        {
            return 0;
        }

        Change(id, d =>
        {
            var on = d.Events.Contains(key, StringComparer.Ordinal);
            d.Events = [.. EventCatalog.AllKeys.Where(k => k == key ? !on : d.Events.Contains(k, StringComparer.Ordinal))];
            d.Preset = new[] { PresetKind.Everything, PresetKind.NewMedia, PresetKind.AdminAlerts }
                .Cast<PresetKind?>()
                .FirstOrDefault(p => EventCatalog.Expand(p!.Value).SequenceEqual(d.Events, StringComparer.Ordinal)) ?? PresetKind.Custom;
        });
        return Math.Max(0, EventCatalog.AllKeys.ToList().IndexOf(key)) / Paging.PageSize;
    }

    /// <summary>Flips a library, or clears the filter for <c>all</c>; returns page 0.</summary>
    private int ToggleLibrary(string id, string arg)
    {
        if (arg == "all")
        {
            Change(id, d => d.LibraryIds = []);
            return 0;
        }

        if (Guid.TryParseExact(arg, "N", out var library))
        {
            var key = library.ToString("N");
            Change(id, d => d.LibraryIds = Toggle(d.LibraryIds, key, true));
            var index = _library.GetVirtualFolders().ToList().FindIndex(f => f.Id == library);
            return Math.Max(0, index) / Paging.PageSize;
        }

        return 0;
    }

    private void Change(string id, Action<Destination> change)
        => _settings.UpdateIf(null, c =>
        {
            if (c.Destinations.FirstOrDefault(d => d.Id == id) is not { } d)
            {
                return false;
            }

            change(d);
            return true;
        });

    private Destination? Find(string id) => _settings.Current.Destinations.FirstOrDefault(d => d.Id == id);

    private Screen Gone()
        => new(_strings.Get("dest.gone"), new InlineKeyboardMarkup(Button("menu.back", "dl", "0")));

    private InlineKeyboardButton Button(string textKey, string action, params string[] args)
        => Buttons.Callback(_strings, textKey, CallbackData.Format(action, args));

    private string Heading(string titleKey, Destination d)
        => "<b>" + Icons.Heading(_strings, _formatter, titleKey) + "</b>\n" + _formatter.Escape(d.Title);

    private void AddNav(List<InlineKeyboardButton[]> rows, int page, int count, string action, string id)
    {
        if (Paging.NavRow(page, count, p => CallbackData.Format(action, id, p.ToString(CultureInfo.InvariantCulture)), _strings.Get("menu.prev"), _strings.Get("menu.next")) is { } nav)
        {
            rows.Add(nav);
        }
    }

    /// <summary>The mark in front of a switch label.</summary>
    /// <param name="on">Whether the switch is on.</param>
    /// <returns>✅ or ▫️.</returns>
    internal static string Mark(bool on) => on ? "✅" : "▫️";

    private string HealthText(DestinationHealth health)
        => _strings.Get("dest.health." + (health.Status == HealthStatus.Ok && health.PhotosBlocked ? "PhotosBlocked" : health.Status.ToString()));

    /// <summary>The list badge: the health glyph, followed by ⏸ when the user paused the destination (the two are separate).</summary>
    private string Badge(Destination d)
    {
        var health = _health.Get(d.Id);
        var glyph = health.Status != HealthStatus.Ok ? "⛔" : health.PhotosBlocked ? "⚠️" : "✅";
        return d.Enabled ? glyph : glyph + "⏸";
    }
}
