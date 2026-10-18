using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Telegram.Bot.Types;

namespace Jellyfin.Plugin.TgJelly.Destinations;

/// <summary>
/// A forum topic's link (spec F35, §6.5): <c>t.me/c/&lt;id&gt;/&lt;topic&gt;</c> for private supergroups or
/// <c>t.me/&lt;username&gt;/&lt;topic&gt;</c> for public ones, optionally followed by a message ID.
/// </summary>
/// <param name="Chat">The chat: a numeric <c>-100…</c> ID or an <c>@username</c>.</param>
/// <param name="ThreadId">The topic's thread ID.</param>
public sealed record TopicLink(ChatId Chat, int ThreadId)
{
    /// <summary>
    /// Parses a topic link.
    /// </summary>
    /// <param name="text">The link as pasted.</param>
    /// <param name="link">The parsed link.</param>
    /// <returns><c>true</c> when <paramref name="text"/> is a topic link.</returns>
    public static bool TryParse(string? text, [NotNullWhen(true)] out TopicLink? link)
    {
        link = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var rest = text.Trim();
        foreach (var scheme in new[] { "https://", "http://" })
        {
            if (rest.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                rest = rest[scheme.Length..];
                break;
            }
        }

        var query = rest.IndexOfAny(['?', '#']);
        if (query >= 0)
        {
            rest = rest[..query];
        }

        var parts = rest.TrimEnd('/').Split('/');
        if (parts.Length < 3 || !(parts[0].Equals("t.me", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("telegram.me", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (parts[1] == "c")
        {
            // t.me/c/<internal id>/<topic>[/<message>]
            if (parts.Length is < 4 or > 5 || !IsDigits(parts[2]) || !TryTopic(parts[3], out var topic)
                || !long.TryParse("-100" + parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var chatId))
            {
                return false;
            }

            link = new TopicLink(new ChatId(chatId), topic);
            return true;
        }

        // t.me/<username>/<topic>[/<message>]
        if (parts.Length > 4 || !IsUsername(parts[1]) || !TryTopic(parts[2], out var publicTopic))
        {
            return false;
        }

        link = new TopicLink(new ChatId("@" + parts[1]), publicTopic);
        return true;
    }

    private static bool TryTopic(string text, out int topic)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out topic) && topic > 0;

    private static bool IsDigits(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsUsername(string text)
    {
        if (text.Length is < 4 or > 32 || !char.IsAsciiLetter(text[0]))
        {
            return false;
        }

        foreach (var c in text)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                return false;
            }
        }

        return true;
    }
}
