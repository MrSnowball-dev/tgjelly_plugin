using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.TgJelly.Rendering;

/// <summary>
/// Checks HTML against Telegram's tag whitelist (spec §8.5): b strong i em u ins s strike del span.tg-spoiler
/// tg-spoiler a code pre blockquote[expandable] tg-emoji tg-time, with balanced tags.
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
    public static IReadOnlyList<string> Validate(string html)
    {
        var problems = new List<string>();
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
                    problems.Add(string.Create(CultureInfo.InvariantCulture, $"Tag <{name}> is not allowed"));
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
                    problems.Add(string.Create(CultureInfo.InvariantCulture, $"Tag </{name}> closes out of order"));

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
                    problems.Add(string.Create(CultureInfo.InvariantCulture, $"Stray closing tag </{name}>"));
                }

                continue;
            }

            if (name == "span" && attributes != "class=\"tg-spoiler\"")
            {
                problems.Add("A span must be <span class=\"tg-spoiler\">");
            }
            else if (name == "blockquote" && attributes.Length > 0 && attributes != "expandable")
            {
                problems.Add("A blockquote may only have the attribute expandable");
            }

            open.Push(name);
        }

        foreach (var name in open)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"Tag <{name}> is never closed"));
        }

        return problems;
    }

    [GeneratedRegex("<(?<close>/)?(?<name>[a-zA-Z][a-zA-Z0-9-]*)(?<attrs>[^>]*)>")]
    private static partial Regex TagPattern();
}
