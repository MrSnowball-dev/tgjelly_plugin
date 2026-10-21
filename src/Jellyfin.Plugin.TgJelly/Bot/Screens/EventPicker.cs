using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Strings;
using Telegram.Bot.Types.ReplyMarkups;

namespace Jellyfin.Plugin.TgJelly.Bot.Screens;

/// <summary>
/// The button rows shared by the destination's events screen and the onboarding Custom step: one button per event
/// group, and the switches of one group.
/// </summary>
internal static class EventPicker
{
    /// <summary>
    /// Builds one row per event group, labelled with the group's name and how many of its events are on.
    /// </summary>
    /// <param name="strings">The string table.</param>
    /// <param name="selected">The selected event keys.</param>
    /// <param name="openGroup">Gets the callback data that opens a group.</param>
    /// <returns>The rows.</returns>
    public static List<InlineKeyboardButton[]> GroupRows(StringTable strings, IReadOnlyCollection<string> selected, Func<string, string> openGroup)
    {
        ArgumentNullException.ThrowIfNull(strings);
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(openGroup);
        return [.. EventCatalog.Groups.Select(g =>
        {
            var keys = EventCatalog.AllKeys.Where(k => EventCatalog.GroupOf(k) == g).ToList();
            var nameKey = "events.group." + g;
            var text = strings.Get(
                "events.group",
                ("name", strings.Get(nameKey)),
                ("on", keys.Count(k => selected.Contains(k, StringComparer.Ordinal))),
                ("total", keys.Count));
            var button = InlineKeyboardButton.WithCallbackData(text, openGroup(g));
            button.IconCustomEmojiId = Icons.ForLabel.GetValueOrDefault(nameKey);
            return new[] { button };
        })];
    }

    /// <summary>
    /// Builds one switch row per event of a group, in catalog order.
    /// </summary>
    /// <param name="strings">The string table.</param>
    /// <param name="group">The event group.</param>
    /// <param name="selected">The selected event keys.</param>
    /// <param name="toggle">Gets the callback data that flips an event.</param>
    /// <returns>The rows.</returns>
    public static List<InlineKeyboardButton[]> ToggleRows(StringTable strings, string group, IReadOnlyCollection<string> selected, Func<string, string> toggle)
    {
        ArgumentNullException.ThrowIfNull(strings);
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(toggle);
        return [.. EventCatalog.AllKeys
            .Where(k => EventCatalog.GroupOf(k) == group)
            .Select(k => new[] { InlineKeyboardButton.WithCallbackData(DestinationScreens.Mark(selected.Contains(k, StringComparer.Ordinal)) + " " + strings.Get("dest.event." + k), toggle(k)) })];
    }
}
