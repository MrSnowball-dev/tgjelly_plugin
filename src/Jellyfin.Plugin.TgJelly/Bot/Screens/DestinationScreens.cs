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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _creating = new(StringComparer.Ordinal);

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
                return Events(id);
            case "deg":
                return EventGroup(id, arg);
            case "det":
                ToggleEvent(id, arg);
                return EventCatalog.IsKnown(arg) ? EventGroup(id, EventCatalog.GroupOf(arg)) : Events(id);
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
            case "dtp":
                return Topics(id);
            case "dts":
                return TopicPick(id, arg);
            case "dtc":
                return await SetTopicAsync(id, arg, args.Count > 2 ? args[2] : string.Empty, ct).ConfigureAwait(false);
            default:
                return null;
        }
    }

    /// <summary>
    /// Builds one page of the destination list.
    /// </summary>
    /// <param name="page">The zero-based page.</param>
    /// <returns>The screen.</returns>
    public Screen List(int page) => Listing(page, "menu.destinations.title", "menu.destinations.pick", "d", "dl", "m", offerAdd: true);

    /// <summary>
    /// Builds one page of the <c>/test</c> picker.
    /// </summary>
    /// <param name="page">The zero-based page.</param>
    /// <returns>The screen.</returns>
    public Screen Picker(int page) => Listing(page, "dest.picker.title", "dest.picker.pick", "dt", "tp", "m");

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

    private Screen Listing(int page, string titleKey, string pickKey, string pick, string pageAction, string backAction, bool offerAdd = false)
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

        if (offerAdd)
        {
            // Adding lives with the destinations (owner, 2026-10-25), as the primary action of the screen.
            var add = Buttons.Callback(_strings, "menu.add", CallbackData.Format("oa"));
            add.Style = KeyboardButtonStyle.Primary;
            rows.Add([add]);
        }

        rows.Add([Button("menu.back", backAction)]);
        if (destinations.Length > 0)
        {
            html.Append("\n\n").Append(_formatter.Escape(_strings.Get(pickKey)));
        }

        if (offerAdd)
        {
            html.Append(ButtonGuide.Block([ButtonGuide.Line(_strings, _formatter, "menu.add", "menu.add.about")]));
        }

        return new Screen(html.ToString(), new InlineKeyboardMarkup(rows));
    }

    private string TopicName(Destination d, int thread)
    {
        var known = Array.Find(d.KnownTopics, k => k.ThreadId == thread);
        return known is { Name.Length: > 0 } ? known.Name
            : thread == 0 ? _strings.Get("web.topics.general") : _strings.Get("web.topics.number", ("id", thread));
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
        html.Append(_formatter.Escape(_strings.Get("dest.line.kind", ("kind", _strings.Get(d.KindKey))))).Append('\n');
        if (d.KindKey == "dest.kind.Forum")
        {
            // Which topic it posts to, and the slots in use that have their own (Topics changes them).
            html.Append(_formatter.Escape(_strings.Get("dest.line.topic", ("topic", TopicName(d, d.ThreadId))))).Append('\n');
            foreach (var route in d.TopicRoutes.Where(r => r.Slot.StartsWith("media.", StringComparison.Ordinal) == d.SeparateMediaTopics || !r.Slot.StartsWith("media", StringComparison.Ordinal)))
            {
                var what = _strings.Get(route.Slot.Contains('.', StringComparison.Ordinal) ? "web.event." + route.Slot : "web.event.group." + route.Slot);
                html.Append(_formatter.Escape(_strings.Get("dest.line.route", ("what", what), ("topic", TopicName(d, route.ThreadId))))).Append('\n');
            }
        }

        html.Append(_formatter.Escape(_strings.Get("dest.line.status", ("status", HealthText(health))))).Append('\n');
        html.Append(_formatter.Escape(_strings.Get("dest.line.groups", ("groups", sends)))).Append('\n');
        html.Append(_formatter.Escape(_strings.Get("dest.line.enabled", ("state", _strings.Get(d.Enabled ? "dest.enabled.yes" : "dest.enabled.no")))));
        if (note is not null)
        {
            html.Append("\n\n").Append(note);
        }

        var pauseKey = d.Enabled ? "dest.btn.pause" : "dest.btn.resume";
        var forum = d.KindKey == "dest.kind.Forum";
        html.Append(ButtonGuide.Lines(
            _strings,
            _formatter,
            [.. forum ? new[] { ("dest.btn.topics", "dest.btn.topics.about") } : [],
            ("dest.btn.events", "dest.btn.events.about"),
            ("dest.btn.libraries", "dest.btn.libraries.about"),
            ("dest.btn.silent", "dest.btn.silent.about"),
            ("dest.btn.test", "dest.btn.test.about"),
            ("dest.btn.check", "dest.btn.check.about"),
            (pauseKey, pauseKey + ".about"),
            ("dest.btn.remove", "dest.btn.remove.about")]));
        var rows = new List<InlineKeyboardButton[]>
        {
            new[] { Button("dest.btn.events", "de", d.Id), Button("dest.btn.libraries", "dlb", d.Id, "0") },
            new[] { Button("dest.btn.silent", "dsg", d.Id), Button("dest.btn.test", "dt", d.Id) },
            new[] { Button("dest.btn.check", "dc", d.Id), Button(pauseKey, "dp", d.Id) },
            new[] { Button("dest.btn.remove", "dr", d.Id) },
            new[] { Button("menu.back", "dl", "0") },
        };
        if (forum)
        {
            rows.Insert(0, [Button("dest.btn.topics", "dtp", d.Id)]);
        }

        return new Screen(html.ToString(), new InlineKeyboardMarkup(rows));
    }

    /// <summary>
    /// A forum destination's topics (owner, 2026-10-25): one line per group, and per media event with separate media
    /// topics; a button for each opens its choice.
    /// </summary>
    private Screen Topics(string id, string? note = null)
    {
        if (Find(id) is not { } d)
        {
            return Gone();
        }

        var slots = new List<string> { "media" };
        if (d.SeparateMediaTopics)
        {
            slots.AddRange(TopicSlots.All.Where(slot => slot.StartsWith("media.", StringComparison.Ordinal)));
        }

        slots.AddRange(["security", "server"]);
        var html = new StringBuilder(Heading("dest.topics.title", d)).Append("\n\n");
        foreach (var slot in slots)
        {
            var where = slot == "media" && d.SeparateMediaTopics ? _strings.Get("web.topics.separate") : TopicName(d, RouteOf(d, slot) ?? d.ThreadId);
            html.Append(_formatter.Escape(_strings.Get("dest.line.route", ("what", SlotLabel(slot)), ("topic", where)))).Append('\n');
        }

        if (note is not null)
        {
            html.Append('\n').Append(note).Append('\n');
        }

        html.Append(ButtonGuide.Block([.. slots.Select(slot => ButtonGuide.Line(_formatter, SlotLabel(slot), _strings.Get("dest.topics.slot.about", ("what", SlotLabel(slot))))),
            ButtonGuide.Line(_strings, _formatter, "menu.back", "dest.topics.back.about")]));
        var rows = slots.Select(slot => new[] { InlineKeyboardButton.WithCallbackData(SlotLabel(slot), CallbackData.Format("dts", d.Id, slot)) }).ToList();
        rows.Add([Button("menu.back", "d", d.Id)]);
        return new Screen(html.ToString(), new InlineKeyboardMarkup(rows));
    }

    /// <summary>
    /// The choice for one slot: the destination's own topic, General, the topics TgJelly knows, Separate topics for
    /// each event (Media only), or a new topic named after the slot.
    /// </summary>
    private Screen TopicPick(string id, string slot)
    {
        if (Find(id) is not { } d)
        {
            return Gone();
        }

        if (!TopicSlots.All.Contains(slot, StringComparer.Ordinal))
        {
            return Topics(id);
        }

        var what = SlotLabel(slot);
        var choices = new List<(string Label, string Value, string About)>
        {
            (_strings.Get("web.topics.default", ("topic", TopicName(d, d.ThreadId))), "d", _strings.Get("dest.topics.default.about")),
        };
        var threads = new[] { 0 }.Concat(d.KnownTopics.Select(t => t.ThreadId)).Distinct().Where(t => t != d.ThreadId);
        choices.AddRange(threads.Select(t => (TopicName(d, t), t.ToString(CultureInfo.InvariantCulture), _strings.Get("dest.topics.known.about", ("what", what)))));
        if (slot == "media")
        {
            choices.Add((_strings.Get("web.topics.separate"), "s", _strings.Get("dest.topics.separate.about")));
        }

        choices.Add((_strings.Get("dest.topics.create", ("name", what)), "n", _strings.Get("dest.topics.create.about")));
        var html = Heading("dest.topics.title", d) + "\n\n" + _formatter.Escape(_strings.Get("dest.topics.pick", ("what", what)))
            + ButtonGuide.Block([.. choices.Select(c => ButtonGuide.Line(_formatter, c.Label, c.About)), ButtonGuide.Line(_strings, _formatter, "menu.back", "dest.topics.pickBack.about")]);
        var rows = choices.Select(c => new[] { InlineKeyboardButton.WithCallbackData(MenuScreens.Label(c.Label), CallbackData.Format("dtc", d.Id, slot, c.Value)) }).ToList();
        rows.Add([Button("menu.back", "dtp", d.Id)]);
        return new Screen(html, new InlineKeyboardMarkup(rows));
    }

    /// <summary>Applies a topic choice, creating the topic first for "n", and shows the topics again.</summary>
    private async Task<Screen> SetTopicAsync(string id, string slot, string choice, CancellationToken ct)
    {
        if (Find(id) is not { } d || !TopicSlots.All.Contains(slot, StringComparer.Ordinal))
        {
            return Find(id) is null ? Gone() : Topics(id);
        }

        int? thread;
        var name = string.Empty;
        switch (choice)
        {
            case "d":
                thread = null;
                break;
            case "s" when slot == "media":
                Change(id, x => x.SeparateMediaTopics = true);
                return Topics(id);
            case "n":
                name = SlotLabel(slot);
                // A double tap, or a redelivered press, must not create a second topic.
                if (!_creating.TryAdd(id + "|" + slot, 0))
                {
                    return Topics(id);
                }

                try
                {
                    thread = await _connection.RequireSender().CreateForumTopicAsync(d.ChatId, name, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var error = TelegramErrorClassifier.ClassifyDelivery(ex);
                    _logger.LogWarning("TgJelly could not create a topic in {Destination}: {Error}", d.Title, error.Description);
                    return Topics(id, _formatter.Escape(_strings.Get("web.topics.createFailed", ("error", error.Description))));
                }
                finally
                {
                    _creating.TryRemove(id + "|" + slot, out _);
                }

                break;
            default:
                if (!int.TryParse(choice, NumberStyles.None, CultureInfo.InvariantCulture, out var picked))
                {
                    return Topics(id);
                }

                thread = picked;
                break;
        }

        var created = thread;
        Change(id, x =>
        {
            if (choice == "n" && created is { } t)
            {
                x.RememberTopic(t, name);
            }

            x.SetRoutes([(slot, created)]);
            if (slot == "media")
            {
                // One topic for all media again ends separate topics; the events' own choices are kept for next time.
                x.SeparateMediaTopics = false;
            }
        });
        return Topics(id);
    }

    private static int? RouteOf(Destination d, string slot)
        => Array.Find(d.TopicRoutes, r => string.Equals(r.Slot, slot, StringComparison.Ordinal))?.ThreadId;

    private string SlotLabel(string slot)
        => _strings.Get(slot.Contains('.', StringComparison.Ordinal) ? "web.event." + slot : "web.event.group." + slot);

    private Screen Events(string id)
    {
        if (Find(id) is not { } d)
        {
            return Gone();
        }

        var rows = EventPicker.GroupRows(_strings, d.Events, g => CallbackData.Format("deg", d.Id, g));
        rows.Add([Button("menu.back", "d", d.Id)]);
        var html = Heading("dest.events.title", d) + "\n\n" + _formatter.Escape(_strings.Get("events.choose"))
            + ButtonGuide.Lines(_strings, _formatter, [.. EventCatalog.Groups.Select(g => ("events.group." + g, "events.group." + g + ".about"))]);
        return new Screen(html, new InlineKeyboardMarkup(rows));
    }

    private Screen EventGroup(string id, string group)
    {
        if (Find(id) is not { } d)
        {
            return Gone();
        }

        if (!EventCatalog.Groups.Contains(group, StringComparer.Ordinal))
        {
            return Events(id);
        }

        var rows = EventPicker.ToggleRows(_strings, group, d.Events, key => CallbackData.Format("det", d.Id, key));
        rows.Add([Button("menu.back", "de", d.Id)]);
        var name = Icons.Heading(_strings, _formatter, "events.group." + group);
        return new Screen(Heading("dest.events.title", d) + "\n\n<b>" + name + "</b>\n\n" + _formatter.Escape(_strings.Get("dest.events.pick")), new InlineKeyboardMarkup(rows));
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

        html.Append(ButtonGuide.Lines(_strings, _formatter, ("dest.libraries.all", "dest.libraries.all.about")));
        if (folders.Count > 0)
        {
            html.Append("\n\n").Append(_formatter.Escape(_strings.Get("dest.libraries.pick")));
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

        var html = Heading("dest.silent.title", d) + "\n\n" + _strings.Get("dest.silent.hint") + "\n\n" + _formatter.Escape(_strings.Get("dest.silent.pick"));
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

        var html = "<b>" + _formatter.Escape(d.Title) + "</b>\n\n" + _formatter.Escape(_strings.Get("dest.remove.ask"))
            + ButtonGuide.Lines(_strings, _formatter, ("dest.btn.remove", "dest.remove.only.about"), ("dest.btn.removeLeave", "dest.btn.removeLeave.about"), ("dest.btn.cancel", "dest.btn.cancel.about"));
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

    /// <summary>Flips an event; an unknown key changes nothing.</summary>
    private void ToggleEvent(string id, string key)
    {
        if (!EventCatalog.IsKnown(key))
        {
            return;
        }

        Change(id, d =>
        {
            var on = d.Events.Contains(key, StringComparer.Ordinal);
            d.Events = [.. EventCatalog.AllKeys.Where(k => k == key ? !on : d.Events.Contains(k, StringComparer.Ordinal))];
            d.FollowsNewEvents = EventCatalog.HasAll(d.Events);
        });
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
