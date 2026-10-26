using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.TgJelly.Rendering;

/// <summary>
/// Checks HTML against Telegram's tag whitelist: b strong i em u ins s strike del span.tg-spoiler
/// tg-spoiler a code pre blockquote[expandable] tg-emoji tg-time, with balanced tags. Problems are string keys, so the
/// admin reads them from the string table (<see cref="HtmlProblem.Describe"/>).
/// </summary>
public static partial class TelegramHtmlValidator
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "b", "strong", "i", "em", "u", "ins", "s", "strike", "del", "span", "tg-spoiler", "a", "code", "pre", "blockquote", "tg-emoji", "tg-time",
    };

    /// <summary>
    /// Lists the problems in an HTML fragment.
    /// </summary>
    /// <param name="html">The fragment.</param>
    /// <returns>The problems; empty when the fragment is valid.</returns>
    public static IReadOnlyList<HtmlProblem> Validate(string html)
    {
        var problems = new List<HtmlProblem>();
        var open = new Stack<string>();
        var rejected = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match match in TagPattern().Matches(html ?? string.Empty))
        {
            var closing = match.Groups["close"].Success;
            var name = match.Groups["name"].Value.ToLowerInvariant();
            var attributes = match.Groups["attrs"].Value.Trim();
            if (!Allowed.Contains(name))
            {
                // A closing tag is reported only when no reported opening tag of that name is still pending.
                if (closing && rejected.TryGetValue(name, out var pending) && pending > 0)
                {
                    rejected[name] = pending - 1;
                }
                else
                {
                    problems.Add(new HtmlProblem("templates.html.notAllowed", name));
                    if (!closing)
                    {
                        rejected[name] = rejected.GetValueOrDefault(name) + 1;
                    }
                }

                continue;
            }

            if (closing)
            {
                if (open.Count > 0 && open.Peek() == name)
                {
                    open.Pop();
                }
                else if (open.Contains(name))
                {
                    problems.Add(new HtmlProblem("templates.html.outOfOrder", name));

                    // Drop only the mismatched element, so its later closing tag isn't reported a second time.
                    var rest = new Stack<string>();
                    while (open.Peek() != name)
                    {
                        rest.Push(open.Pop());
                    }

                    open.Pop();
                    while (rest.Count > 0)
                    {
                        open.Push(rest.Pop());
                    }
                }
                else
                {
                    problems.Add(new HtmlProblem("templates.html.stray", name));
                }

                continue;
            }

            if (name == "span" && attributes != "class=\"tg-spoiler\"")
            {
                problems.Add(new HtmlProblem("templates.html.span", name));
            }
            else if (name == "blockquote" && attributes.Length > 0 && attributes != "expandable")
            {
                problems.Add(new HtmlProblem("templates.html.blockquote", name));
            }

            open.Push(name);
        }

        foreach (var name in open)
        {
            problems.Add(new HtmlProblem("templates.html.unclosed", name));
        }

        return problems;
    }

    [GeneratedRegex("<(?<close>/)?(?<name>[a-zA-Z][a-zA-Z0-9-]*)(?<attrs>[^>]*)>")]
    private static partial Regex TagPattern();
}
