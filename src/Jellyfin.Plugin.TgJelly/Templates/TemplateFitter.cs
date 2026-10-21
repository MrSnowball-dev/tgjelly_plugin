using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.TgJelly.Rendering;
using Telegram.Bot.Extensions;

namespace Jellyfin.Plugin.TgJelly.Templates;

/// <summary>
/// Shortens rendered template output in v0.2's order: the text field first, then the list, then the whole text.
/// </summary>
public static class TemplateFitter
{
    /// <summary>The caption limit.</summary>
    public const int CaptionLimit = 1024;

    /// <summary>The text limit.</summary>
    public const int TextLimit = 4096;

    /// <summary>Code points kept free for the ⏱ line added at send time.</summary>
    public const int DelayedReserve = 40;

    /// <summary>The most list items rendered; the engine's loop limit.</summary>
    public const int MaxListItems = 1000;

    private const int MinOverview = 20;
    private const string Ellipsis = "…";

    /// <summary>
    /// Renders the model and shortens the result to fit.
    /// </summary>
    /// <param name="model">The model with its shrinkable parts.</param>
    /// <param name="render">Renders one model variant.</param>
    /// <param name="limit">The message type's limit.</param>
    /// <returns>The result.</returns>
    public static FitResult Fit(TemplateModel model, Func<IReadOnlyDictionary<string, object?>, TemplateRenderResult> render, int limit)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(render);
        var budget = limit - DelayedReserve;
        var fullList = Math.Min(model.ListCount, MaxListItems);
        TemplateError? error = null;
        TemplateError? tooLong = null;

        // Output over the engine's limit is far over any message limit: it does not fit, and a shorter model may.
        string? Render(string? text, int kept)
        {
            var result = render(model.With(text, kept));
            if (result is { Ok: false, TooLong: true })
            {
                tooLong ??= result.Error;
                return null;
            }

            if (!result.Ok)
            {
                error ??= result.Error;
                return null;
            }

            return result.Output ?? string.Empty;
        }

        bool Fits(string? html) => html is not null && HtmlText.PlainLength(html) <= budget;

        var text = model.ShrinkText;
        var html = Render(text, fullList);
        if (error is not null)
        {
            return new FitResult(null, false, error);
        }

        if (Fits(html))
        {
            return new FitResult(html, false, null);
        }

        if (text is not null)
        {
            var runes = text.EnumerateRunes().ToArray();
            var (lo, hi) = (0, runes.Length - 1);
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                var attempt = Render(Prefix(runes, mid), fullList);
                if (error is not null)
                {
                    return new FitResult(null, false, error);
                }

                if (Fits(attempt))
                {
                    lo = mid;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            text = lo >= MinOverview ? Prefix(runes, lo) : null;
            html = Render(text, fullList);
            if (error is not null)
            {
                return new FitResult(null, false, error);
            }

            if (Fits(html))
            {
                return new FitResult(html, false, null);
            }
        }

        var minKept = model.MinListKept;
        if (fullList > minKept)
        {
            var (lo, hi) = (0, fullList - 1);
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                var attempt = Render(text, mid);
                if (error is not null)
                {
                    return new FitResult(null, false, error);
                }

                if (Fits(attempt))
                {
                    lo = mid;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            html = Render(text, Math.Max(lo, minKept));
            if (error is not null)
            {
                return new FitResult(null, false, error);
            }

            if (Fits(html))
            {
                return new FitResult(html, false, null);
            }
        }

        return html is null
            ? new FitResult(null, false, tooLong)
            : new FitResult(HtmlText.Truncate(html, budget, Ellipsis), true, null);
    }

    private static string Prefix(Rune[] runes, int count)
    {
        var builder = new StringBuilder();
        foreach (var rune in runes.AsSpan(0, count))
        {
            builder.Append(rune.ToString());
        }

        return builder.ToString().TrimEnd() + Ellipsis;
    }
}
