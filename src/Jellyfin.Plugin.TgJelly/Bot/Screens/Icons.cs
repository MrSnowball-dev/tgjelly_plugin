using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.Strings;

namespace Jellyfin.Plugin.TgJelly.Bot.Screens;

/// <summary>
/// The custom emoji icons of the bot's buttons and headings, from the Telegram pack <c>TgAndroidIcons</c>
/// ("Telegram Android Icons"), and the map from a string key to its icon. Custom emoji show only when the bot owner
/// has Telegram Premium; <see cref="TelegramSender"/> falls back to plain text when Telegram rejects them.
/// </summary>
internal static partial class Icons
{
    /// <summary>The <c>Menu</c> icon.</summary>
    public const string Menu = "5875271289605722323";

    /// <summary>The <c>Chats</c> icon.</summary>
    public const string Chats = "5891169510483823323";

    /// <summary>The <c>Add</c> icon.</summary>
    public const string Add = "5775937998948404844";

    /// <summary>The <c>Settings</c> icon.</summary>
    public const string Settings = "5877260593903177342";

    /// <summary>The <c>Status</c> icon.</summary>
    public const string Status = "5877613700344450910";

    /// <summary>The <c>Search</c> icon.</summary>
    public const string Search = "5874960879434338403";

    /// <summary>The <c>Back</c> icon.</summary>
    public const string Back = "5875082500023258804";

    /// <summary>The <c>Channel</c> icon.</summary>
    public const string Channel = "5771695636411847302";

    /// <summary>The <c>Group</c> icon.</summary>
    public const string Group = "5942877472163892475";

    /// <summary>The <c>Chat</c> icon.</summary>
    public const string Chat = "5994297722574737553";

    /// <summary>The <c>Cancel</c> icon.</summary>
    public const string Cancel = "5778527486270770928";

    /// <summary>The <c>Key</c> icon.</summary>
    public const string Key = "6005570495603282482";

    /// <summary>The <c>Refresh</c> icon.</summary>
    public const string Refresh = "5845943483382110702";

    /// <summary>The <c>Hash</c> icon.</summary>
    public const string Hash = "5951584964305755220";

    /// <summary>The <c>NewTopic</c> icon.</summary>
    public const string NewTopic = "5883973610606956186";

    /// <summary>The <c>Link</c> icon.</summary>
    public const string Link = "5877465816030515018";

    /// <summary>The <c>Sliders</c> icon.</summary>
    public const string Sliders = "5875431869842985304";

    /// <summary>The <c>Toggles</c> icon.</summary>
    public const string Toggles = "5875033614705495771";

    /// <summary>The <c>Laptop</c> icon.</summary>
    public const string Laptop = "5967816500415827773";

    /// <summary>The <c>Grid</c> icon.</summary>
    public const string Grid = "6008258140108231117";

    /// <summary>The <c>Video</c> icon.</summary>
    public const string Video = "6005986106703613755";

    /// <summary>The <c>ShieldAlert</c> icon.</summary>
    public const string ShieldAlert = "5775887550262546277";

    /// <summary>The <c>Lock</c> icon.</summary>
    public const string Lock = "5879895758202735862";

    /// <summary>The <c>Server</c> icon.</summary>
    public const string Server = "5877485980901971030";

    /// <summary>The <c>Bell</c> icon.</summary>
    public const string Bell = "5909201569898827582";

    /// <summary>The <c>Download</c> icon.</summary>
    public const string Download = "5899757765743615694";

    /// <summary>The <c>Folder</c> icon.</summary>
    public const string Folder = "5877332341331857066";

    /// <summary>The <c>BellOff</c> icon.</summary>
    public const string BellOff = "5909123362839335003";

    /// <summary>The <c>Send</c> icon.</summary>
    public const string Send = "5875465628285931233";

    /// <summary>The <c>Block</c> icon.</summary>
    public const string Block = "5872829476143894491";

    /// <summary>The <c>Play</c> icon.</summary>
    public const string Play = "5775981206319402773";

    /// <summary>The <c>Trash</c> icon.</summary>
    public const string Trash = "5879896690210639947";

    /// <summary>The <c>Exit</c> icon.</summary>
    public const string Exit = "5877341274863832725";

    /// <summary>The <c>Layers</c> icon.</summary>
    public const string Layers = "5924720918826848520";

    /// <summary>The <c>Check</c> icon.</summary>
    public const string Check = "5825794181183836432";

    /// <summary>The <c>Pencil</c> icon.</summary>
    public const string Pencil = "5879841310902324730";

    /// <summary>The <c>Erase</c> icon.</summary>
    public const string Erase = "5967355281057779430";

    /// <summary>The <c>Hide</c> icon.</summary>
    public const string Hide = "5952055319059239589";

    /// <summary>The <c>Open</c> icon.</summary>
    public const string Open = "5884343982816759327";

    /// <summary>The <c>Palette</c> icon.</summary>
    public const string Palette = "5814690801665446789";

    /// <summary>Icon of each button label, by the label's string key.</summary>
    public static readonly IReadOnlyDictionary<string, string> ForLabel = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["onboard.menu"] = Menu,
        ["menu.destinations"] = Chats,
        ["menu.add"] = Add,
        ["onboard.addAnother"] = Add,
        ["detected.setUp"] = Add,
        ["menu.settings"] = Settings,
        ["menu.status"] = Status,
        ["menu.detected"] = Search,
        ["menu.back"] = Back,
        ["onboard.channel"] = Channel,
        ["onboard.group"] = Group,
        ["onboard.here"] = Chat,
        ["onboard.cancel"] = Cancel,
        ["dest.btn.cancel"] = Cancel,
        ["topic.allow"] = Key,
        ["onboard.tryAgain"] = Refresh,
        ["dest.btn.check"] = Refresh,
        ["topic.general"] = Hash,
        ["topic.create"] = NewTopic,
        ["topic.existing"] = Link,
        ["advanced.topic"] = Link,
        ["settings.url"] = Link,
        ["onboard.advanced"] = Sliders,
        ["onboard.preset.custom"] = Toggles,
        ["advanced.dashboard"] = Laptop,
        ["onboard.preset.everything"] = Grid,
        ["onboard.preset.newMedia"] = Video,
        ["onboard.preset.adminAlerts"] = ShieldAlert,
        ["dest.btn.events"] = Bell,
        ["settings.updates"] = Download,
        ["dest.btn.libraries"] = Folder,
        ["dest.btn.silent"] = BellOff,
        ["dest.btn.test"] = Send,
        ["dest.btn.pause"] = Block,
        ["dest.btn.resume"] = Play,
        ["dest.btn.remove"] = Trash,
        ["dest.btn.removeLeave"] = Exit,
        ["dest.libraries.all"] = Layers,
        ["onboard.customDone"] = Check,
        ["settings.url.use"] = Check,
        ["settings.url.type"] = Pencil,
        ["settings.url.clear"] = Erase,
        ["detected.dismiss"] = Hide,
        ["notify.btn.open"] = Open,
        ["settings.messages"] = Palette,
        ["events.group.media"] = Video,
        ["events.group.security"] = Lock,
        ["events.group.server"] = Server,
    };

    /// <summary>Icon and fallback emoji of each heading, by the heading's string key.</summary>
    public static readonly IReadOnlyDictionary<string, (string Icon, string Fallback)> ForHeading = new Dictionary<string, (string Icon, string Fallback)>(StringComparer.Ordinal)
    {
        ["menu.title"] = (Menu, "🏠"),
        ["menu.destinations.title"] = (Chats, "📬"),
        ["settings.title"] = (Settings, "⚙️"),
        ["status.title"] = (Status, "🩺"),
        ["detected.title"] = (Search, "🔎"),
        ["settings.url.title"] = (Link, "🔗"),
        ["dest.events.title"] = (Bell, "🔔"),
        ["settings.updates.title"] = (Download, "🔔"),
        ["dest.libraries.title"] = (Folder, "📁"),
        ["dest.silent.title"] = (BellOff, "🔕"),
        ["settings.messages.title"] = (Palette, "🎨"),
        ["events.group.media"] = (Video, "🎬"),
        ["events.group.security"] = (Lock, "🔐"),
        ["events.group.server"] = (Server, "🖥"),
    };

    /// <summary>
    /// Builds the text of a heading: its icon (with the fallback emoji inside) and the escaped text.
    /// </summary>
    /// <param name="strings">The string table.</param>
    /// <param name="formatter">The formatter, for escaping.</param>
    /// <param name="key">The heading's string key.</param>
    /// <param name="args">The string's arguments.</param>
    /// <returns>The HTML, without the surrounding <c>&lt;b&gt;</c>.</returns>
    public static string Heading(StringTable strings, Formatter formatter, string key, params (string Name, object? Value)[] args)
    {
        ArgumentNullException.ThrowIfNull(strings);
        ArgumentNullException.ThrowIfNull(formatter);
        var text = formatter.Escape(strings.Get(key, args));
        return ForHeading.TryGetValue(key, out var icon)
            ? "<tg-emoji emoji-id=\"" + icon.Icon + "\">" + icon.Fallback + "</tg-emoji> " + text
            : text;
    }

    /// <summary>
    /// Replaces each custom emoji tag with its fallback emoji.
    /// </summary>
    /// <param name="html">The HTML.</param>
    /// <returns>The HTML without custom emoji tags.</returns>
    public static string StripHtml(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        return EmojiTag().Replace(html, "$1");
    }

    [GeneratedRegex("<tg-emoji emoji-id=\"[^\"]*\">(.*?)</tg-emoji>", RegexOptions.Singleline)]
    private static partial Regex EmojiTag();
}
