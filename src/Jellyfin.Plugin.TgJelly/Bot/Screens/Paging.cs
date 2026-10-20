using System;
using System.Collections.Generic;
using System.Linq;
using Telegram.Bot.Types.ReplyMarkups;

namespace Jellyfin.Plugin.TgJelly.Bot.Screens;

/// <summary>
/// Pages long lists of buttons.
/// </summary>
public static class Paging
{
    /// <summary>The most items on a page.</summary>
    public const int PageSize = 8;

    /// <summary>
    /// Takes one page of a list.
    /// </summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <param name="items">All items.</param>
    /// <param name="page">The zero-based page; one out of range is moved to the nearest page.</param>
    /// <returns>The page's items.</returns>
    public static IReadOnlyList<T> Page<T>(IReadOnlyList<T> items, int page)
    {
        ArgumentNullException.ThrowIfNull(items);
        return [.. items.Skip(Clamp(page, items.Count) * PageSize).Take(PageSize)];
    }

    /// <summary>
    /// Builds the Prev / Next buttons.
    /// </summary>
    /// <param name="page">The zero-based current page.</param>
    /// <param name="count">The number of items in all pages.</param>
    /// <param name="callback">Builds the callback data that opens a page.</param>
    /// <returns>The buttons; <c>null</c> when everything fits on one page.</returns>
    public static InlineKeyboardButton[]? NavRow(int page, int count, Func<int, string> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return NavRow(page, count, callback, "‹ Prev", "Next ›");
    }

    /// <summary>
    /// Builds the Prev / Next buttons with the given texts.
    /// </summary>
    /// <param name="page">The zero-based current page.</param>
    /// <param name="count">The number of items in all pages.</param>
    /// <param name="callback">Builds the callback data that opens a page.</param>
    /// <param name="prev">The text of the Prev button.</param>
    /// <param name="next">The text of the Next button.</param>
    /// <returns>The buttons; <c>null</c> when everything fits on one page.</returns>
    public static InlineKeyboardButton[]? NavRow(int page, int count, Func<int, string> callback, string prev, string next)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var pages = (count + PageSize - 1) / PageSize;
        if (pages <= 1)
        {
            return null;
        }

        page = Clamp(page, count);
        var row = new List<InlineKeyboardButton>(2);
        if (page > 0)
        {
            row.Add(InlineKeyboardButton.WithCallbackData(prev, callback(page - 1)));
        }

        if (page < pages - 1)
        {
            row.Add(InlineKeyboardButton.WithCallbackData(next, callback(page + 1)));
        }

        return [.. row];
    }

    private static int Clamp(int page, int count) => Math.Clamp(page, 0, Math.Max(0, (count - 1) / PageSize));
}
