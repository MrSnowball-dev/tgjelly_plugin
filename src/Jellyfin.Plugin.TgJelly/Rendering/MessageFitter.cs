using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Telegram.Bot.Extensions;

namespace Jellyfin.Plugin.TgJelly.Rendering;

/// <summary>
/// Fits a message into Telegram's limits (spec §8.6), measured in code points with <see cref="HtmlText.PlainLength"/>
/// and leaving room for the delayed-delivery line: the overview is shortened first, then lists ("+N more"), then the
/// whole text is truncated.
/// </summary>
public static class MessageFitter
{
    /// <summary>The caption limit.</summary>
    public const int CaptionLimit = 1024;

    /// <summary>The text limit.</summary>
    public const int TextLimit = 4096;

    /// <summary>Code points kept free for the ⏱ line added at send time.</summary>
    public const int DelayedReserve = 40;

    private const int MinOverview = 20;
    private const string Ellipsis = "…";

    /// <summary>
    /// Builds the message and shortens it to fit.
    /// </summary>
    /// <param name="parts">The segments.</param>
    /// <param name="limit">The message type's limit.</param>
    /// <param name="more">Renders the "+N more" line for N removed list lines.</param>
    /// <returns>The HTML.</returns>
    public static string Fit(MessageParts parts, int limit, Func<int, string> more)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(more);
        var budget = limit - DelayedReserve;
        var overview = string.IsNullOrEmpty(parts.Overview) ? null : parts.Overview;
        var list = parts.List;
        var text = Compose(parts.Head, list, overview, parts.Tail, parts.QuoteOverview);
        if (Fits(text, budget))
        {
            return text;
        }

        if (overview is not null)
        {
            var runes = overview.EnumerateRunes().ToArray();
            var (lo, hi) = (0, runes.Length - 1);
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                if (Fits(Compose(parts.Head, list, Prefix(runes, mid), parts.Tail, parts.QuoteOverview), budget))
                {
                    lo = mid;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            overview = lo >= MinOverview ? Prefix(runes, lo) : null;
            text = Compose(parts.Head, list, overview, parts.Tail, parts.QuoteOverview);
            if (Fits(text, budget))
            {
                return text;
            }
        }

        for (var removed = 1; removed < list.Count; removed++)
        {
            var shortened = list.Take(list.Count - removed).Append(more(removed)).ToList();
            text = Compose(parts.Head, shortened, overview, parts.Tail, parts.QuoteOverview);
            if (Fits(text, budget))
            {
                return text;
            }
        }

        return HtmlText.Truncate(text, budget, Ellipsis);
    }

    private static bool Fits(string html, int budget) => HtmlText.PlainLength(html) <= budget;

    private static string Prefix(Rune[] runes, int count)
    {
        var builder = new StringBuilder();
        foreach (var rune in runes.AsSpan(0, count))
        {
            builder.Append(rune.ToString());
        }

        return builder.ToString().TrimEnd() + Ellipsis;
    }

    private static string Compose(string head, IReadOnlyList<string> list, string? overview, string tail, bool quote)
    {
        var segments = new List<string>(4);
        if (head.Length > 0)
        {
            segments.Add(head);
        }

        if (list.Count > 0)
        {
            segments.Add(string.Join('\n', list));
        }

        if (overview is not null)
        {
            segments.Add(quote ? "<blockquote expandable>" + HtmlText.Escape(overview) + "</blockquote>" : HtmlText.Escape(overview));
        }

        if (tail.Length > 0)
        {
            segments.Add(tail);
        }

        return string.Join("\n\n", segments);
    }
}
