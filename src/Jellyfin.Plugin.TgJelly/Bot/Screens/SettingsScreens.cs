using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Strings;
using Telegram.Bot.Types.ReplyMarkups;

namespace Jellyfin.Plugin.TgJelly.Bot.Screens;

/// <summary>
/// The settings screens (spec §8.3, §8.4): message style switches, update checks and the public URL. Every change is
/// applied to the settings as they are at save time, so a change made in the dashboard meanwhile is not lost.
/// </summary>
public sealed class SettingsScreens
{
    private const string InputKey = "input";
    private const string PublicUrlInput = "publicUrl";

    /// <summary>The kinds of release the update checker can announce, in the order shown.</summary>
    private static readonly string[] UpdateKinds = [nameof(UpdateCheckOptions.Fixes), nameof(UpdateCheckOptions.NewReleases), nameof(UpdateCheckOptions.NewMajor)];

    private readonly SettingsStore _settings;
    private readonly StateStore _state;
    private readonly LinkBuilder _links;
    private readonly BotMemory _memory;
    private readonly StringTable _strings;
    private readonly Formatter _formatter;

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsScreens"/> class.
    /// </summary>
    /// <param name="settings">The settings store.</param>
    /// <param name="state">The state store, for the public URL notice.</param>
    /// <param name="links">Suggests and normalizes the public URL.</param>
    /// <param name="memory">The bot's memory, for the typed-URL flow.</param>
    /// <param name="strings">The string table.</param>
    /// <param name="formatter">The formatter, for escaping.</param>
    public SettingsScreens(SettingsStore settings, StateStore state, LinkBuilder links, BotMemory memory, StringTable strings, Formatter formatter)
    {
        _settings = settings;
        _state = state;
        _links = links;
        _memory = memory;
        _strings = strings;
        _formatter = formatter;
    }

    /// <summary>
    /// Runs a settings button and builds the screen to show.
    /// </summary>
    /// <param name="action">The callback action.</param>
    /// <param name="args">The callback arguments.</param>
    /// <param name="chatId">The private chat.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>The screen, or <c>null</c> when <paramref name="action"/> is not a settings action.</returns>
    public async Task<Screen?> HandleAsync(string action, IReadOnlyList<string> args, long chatId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(args);
        var arg = args.Count > 0 ? args[0] : string.Empty;
        switch (action)
        {
            case "s":
                return Menu();
            case "sm":
                return MessageSections();
            case "sms":
                return Array.IndexOf(EventCatalog.Groups.ToArray(), arg) >= 0 ? MessageList(arg) : MessageSections();
            case "smk":
                return MessageOptions.IsKey(arg) ? MessageSwitchesScreen(arg) : MessageSections();
            case "smt":
                var name = args.Count > 1 ? args[1] : string.Empty;
                if (!MessageOptions.IsKey(arg))
                {
                    return MessageSections();
                }

                if (MessageSwitches.Applicable(arg).Contains(name))
                {
                    _settings.Update(null, c => c.Message.Change(arg, m => m.Set(name, !m.Get(name))));
                }

                return MessageSwitchesScreen(arg);
            case "smu":
                _settings.Update(null, c => c.Message.AnnounceUpgrades = !c.Message.AnnounceUpgrades);
                return MessageList("media");
            case "su":
                return Updates();
            case "sut":
                _settings.Update(null, c => c.UpdateCheck.Enabled = !c.UpdateCheck.Enabled);
                return Updates();
            case "suk":
                if (Array.IndexOf(UpdateKinds, arg) >= 0)
                {
                    _settings.Update(null, c => FlipUpdateKind(c.UpdateCheck, arg));
                }

                return Updates();
            case "sp":
                return PublicUrl();
            case "spu":
                if (_links.Suggest() is { } suggested)
                {
                    SetPublicUrl(suggested);
                }

                return PublicUrl();
            case "spt":
                return UrlPrompt(chatId, null);
            case "spc":
                SetPublicUrl(string.Empty);
                return PublicUrl();
            default:
                return null;
        }
    }

    /// <summary>
    /// Handles a private text message while a public URL is awaited.
    /// </summary>
    /// <param name="chatId">The private chat.</param>
    /// <param name="text">The text.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns><c>Handled</c> is false when no URL was awaited (the text is ordinary). Otherwise a valid URL is saved
    /// and <c>Screen</c> is the public URL screen to send; an invalid one is explained and asked for again: <c>Screen</c> is the prompt, to send as a new message.</returns>
    public async Task<(bool Handled, Screen? Screen)> OnTextAsync(long chatId, string text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!AwaitsUrl(chatId))
        {
            return (false, null);
        }

        if (LinkBuilder.Normalize(text) is not { } url)
        {
            return (true, UrlPrompt(chatId, _strings.Get("settings.url.invalid")));
        }

        SetPublicUrl(url);
        CancelInput(chatId);
        return (true, PublicUrl());
    }

    /// <summary>Saves the public URL; a changed one clears the "public URL flagged" notice (spec §9.4 row 7).</summary>
    private void SetPublicUrl(string url)
    {
        var before = _settings.Current.PublicUrl;
        _settings.Update(null, c => c.PublicUrl = url);
        if (!string.Equals(before, url, StringComparison.Ordinal))
        {
            _state.Update(s => s.Status.PublicUrlFlag = null);
        }
    }

    /// <summary>
    /// Stops waiting for a typed public URL in a chat, if it was.
    /// </summary>
    /// <param name="chatId">The private chat.</param>
    public void CancelInput(long chatId)
    {
        if (_memory.GetFlow(chatId) is { } flow && flow.Params.Remove(InputKey))
        {
            _memory.SaveFlow(chatId, flow);
        }
    }

    private static void FlipUpdateKind(UpdateCheckOptions o, string kind)
    {
        switch (kind)
        {
            case nameof(UpdateCheckOptions.Fixes):
                o.Fixes = !o.Fixes;
                break;
            case nameof(UpdateCheckOptions.NewReleases):
                o.NewReleases = !o.NewReleases;
                break;
            default:
                o.NewMajor = !o.NewMajor;
                break;
        }
    }

    private static bool Get(UpdateCheckOptions o, string kind) => kind switch
    {
        nameof(UpdateCheckOptions.Fixes) => o.Fixes,
        nameof(UpdateCheckOptions.NewReleases) => o.NewReleases,
        _ => o.NewMajor,
    };

    private static string Mark(bool on) => on ? "✅ " : "⬜ ";

    private static InlineKeyboardButton[] Row(string text, string action, params string[] args)
        => [InlineKeyboardButton.WithCallbackData(text, CallbackData.Format(action, args))];

    private InlineKeyboardButton[] Button(string labelKey, string action)
        => [Buttons.Callback(_strings, labelKey, CallbackData.Format(action))];

    private bool AwaitsUrl(long chatId)
        => _memory.GetFlow(chatId) is { } flow && flow.Params.TryGetValue(InputKey, out var input) && input == PublicUrlInput;

    /// <summary>The inline prompt for a typed public URL, with Cancel back to the public URL screen; it starts waiting for the text.</summary>
    private Screen UrlPrompt(long chatId, string? problem)
    {
        var flow = _memory.GetFlow(chatId) ?? new FlowState();
        flow.Screen = "settings";
        flow.Params[InputKey] = PublicUrlInput;
        _memory.SaveFlow(chatId, flow);
        var html = problem is null ? string.Empty : _formatter.Escape(problem) + "\n\n";
        html += _formatter.Escape(_strings.Get("settings.url.prompt")) + "\n\n" + _formatter.Escape(_strings.Get("settings.url.rule"))
            + ButtonGuide.Lines(_strings, _formatter, ("input.cancel", "input.cancel.about"));
        return new Screen(html, new InlineKeyboardMarkup(Button("input.cancel", "sp")));
    }

    private Screen Menu()
    {
        var guide = ButtonGuide.Lines(
            _strings,
            _formatter,
            ("settings.messages", "settings.messages.about"),
            ("settings.updates", "settings.updates.about"),
            ("settings.url", "settings.url.about"));
        return Titled(
            "settings.title",
            null,
            [Button("settings.messages", "sm"), Button("settings.updates", "su"), Button("settings.url", "sp"), Button("menu.back", "m")],
            guide);
    }

    /// <summary>The first step of Message style: the section (Media, Security, Server).</summary>
    private Screen MessageSections()
    {
        var rows = new List<InlineKeyboardButton[]>();
        var shown = new List<(string, string)>();
        foreach (var group in EventCatalog.Groups)
        {
            rows.Add(Row(_strings.Get("events.group." + group), "sms", group));
            shown.Add(("events.group." + group, "events.group." + group + ".about"));
        }

        rows.Add(Button("menu.back", "s"));
        return Titled("settings.messages.title", "settings.messages.hint", rows, ButtonGuide.Lines(_strings, _formatter, [.. shown]));
    }

    /// <summary>The second step: the messages of a section. The media list also holds the global Announce upgrades switch.</summary>
    private Screen MessageList(string group)
    {
        var options = _settings.Current.Message;
        var rows = new List<InlineKeyboardButton[]>();
        foreach (var key in MessageOptions.Keys.Where(k => EventCatalog.GroupOf(k) == group))
        {
            rows.Add(Row(_strings.Get("settings.msg.of." + key), "smk", key));
        }

        var guide = "\n\n" + _formatter.Escape(_strings.Get("settings.messages.pickMessage"));
        if (group == "media")
        {
            rows.Add(Row(Mark(options.AnnounceUpgrades) + _strings.Get("settings.msg.AnnounceUpgrades"), "smu"));
            guide += ButtonGuide.Lines(_strings, _formatter, ("settings.msg.AnnounceUpgrades", "settings.msg.AnnounceUpgrades.about"));
        }

        rows.Add(Button("menu.back", "sm"));
        return Titled("settings.messages.title", "settings.messages.hint.list", rows, guide);
    }

    /// <summary>The third step: one message's applicable switches.</summary>
    private Screen MessageSwitchesScreen(string key)
    {
        var switches = _settings.Current.Message.For(key);
        var applicable = MessageSwitches.Applicable(key);
        var rows = new List<InlineKeyboardButton[]>();
        foreach (var name in applicable)
        {
            rows.Add(Row(Mark(switches.Get(name)) + _strings.Get("settings.msg." + name), "smt", key, name));
        }

        rows.Add(Row(_strings.Get("menu.back"), "sms", EventCatalog.GroupOf(key)));
        var message = _strings.Get("settings.msg.of." + key);
        var guide = applicable.Count == 0
            ? "\n\n" + _formatter.Escape(_strings.Get("settings.messages.none"))
            : "\n\n" + _formatter.Escape(_strings.Get("settings.messages.pick"))
                + ButtonGuide.Lines(_strings, _formatter, [.. applicable.Select(n => ("settings.msg." + n, "settings.msg." + n + ".about"))]);
        var html = "<b>" + Icons.Heading(_strings, _formatter, "settings.messages.title") + "</b>\n"
            + _formatter.Escape(_strings.Get("settings.messages.of", ("message", message)));
        return new Screen(html + guide, new InlineKeyboardMarkup(rows));
    }

    private Screen Updates()
    {
        var check = _settings.Current.UpdateCheck;
        var rows = new List<InlineKeyboardButton[]> { Row(Mark(check.Enabled) + _strings.Get("settings.updates.enabled"), "sut") };
        var shown = new List<(string, string)> { ("settings.updates.enabled", "settings.updates.enabled.about") };
        foreach (var kind in UpdateKinds)
        {
            rows.Add(Row(Mark(Get(check, kind)) + _strings.Get("settings.updates.kind." + kind), "suk", kind));
            shown.Add(("settings.updates.kind." + kind, "settings.updates.kind." + kind + ".about"));
        }

        rows.Add(Button("menu.back", "s"));
        return Titled("settings.updates.title", "settings.updates.hint", rows, ButtonGuide.Lines(_strings, _formatter, [.. shown]));
    }

    private Screen PublicUrl()
    {
        var current = _settings.Current.PublicUrl;
        var suggested = _links.Suggest();
        var html = new StringBuilder("<b>").Append(Icons.Heading(_strings, _formatter, "settings.url.title")).Append("</b>\n")
            .Append(_formatter.Escape(_strings.Get("settings.url.hint"))).Append("\n\n");
        if (current.Length == 0)
        {
            html.Append(_formatter.Escape(_strings.Get("settings.url.none")));
        }
        else
        {
            html.Append(_formatter.Escape(_strings.Get("settings.url.current", ("url", current))));
            if (!PublicUrlRules.IsValid(current))
            {
                html.Append(" ⚠️ ").Append(_formatter.Escape(_strings.Get("settings.url.invalid")));
            }
        }

        var rows = new List<InlineKeyboardButton[]>();
        var shown = new List<(string, string)>();
        if (suggested is not null && suggested != current)
        {
            html.Append("\n\n").Append(_formatter.Escape(_strings.Get("settings.url.suggested", ("url", suggested))));
            rows.Add(Button("settings.url.use", "spu"));
            shown.Add(("settings.url.use", "settings.url.use.about"));
        }

        rows.Add(Button("settings.url.type", "spt"));
        shown.Add(("settings.url.type", "settings.url.type.about"));
        if (current.Length > 0)
        {
            rows.Add(Button("settings.url.clear", "spc"));
            shown.Add(("settings.url.clear", "settings.url.clear.about"));
        }

        rows.Add(Button("menu.back", "s"));
        html.Append(ButtonGuide.Lines(_strings, _formatter, [.. shown]));
        return new Screen(html.ToString(), new InlineKeyboardMarkup(rows));
    }

    private Screen Titled(string titleKey, string? hintKey, IEnumerable<InlineKeyboardButton[]> rows, string guide)
    {
        var html = "<b>" + Icons.Heading(_strings, _formatter, titleKey) + "</b>";
        if (hintKey is not null)
        {
            html += "\n" + _formatter.Escape(_strings.Get(hintKey));
        }

        return new Screen(html + guide, new InlineKeyboardMarkup(rows));
    }
}
