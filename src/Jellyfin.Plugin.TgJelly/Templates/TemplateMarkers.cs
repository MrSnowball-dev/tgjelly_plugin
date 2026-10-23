using System;
using System.Text;

namespace Jellyfin.Plugin.TgJelly.Templates;

/// <summary>
/// The invisible markers of the preview-only render (design §6.5): each top-level piece's output is written as
/// <see cref="Start"/>, the piece's decimal index, <see cref="IndexEnd"/>, the output, then <see cref="End"/>. The page
/// turns them into spans for the hover link. They are private-use characters, and the sent message never holds them.
/// </summary>
public static class TemplateMarkers
{
    /// <summary>Opens a piece's marker; the piece's index follows.</summary>
    public const char Start = '\uE000';

    /// <summary>Ends the piece's index; the piece's output follows.</summary>
    public const char IndexEnd = '\uE001';

    /// <summary>Closes a piece's output.</summary>
    public const char End = '\uE002';

    /// <summary>
    /// Removes every marker.
    /// </summary>
    /// <param name="marked">Marked output.</param>
    /// <returns>The output as the unmarked render gives it.</returns>
    public static string Strip(string marked)
    {
        ArgumentNullException.ThrowIfNull(marked);
        var builder = new StringBuilder(marked.Length);
        for (var i = 0; i < marked.Length; i++)
        {
            if (marked[i] == Start)
            {
                var close = marked.IndexOf(IndexEnd, i + 1);
                i = close < 0 ? marked.Length : close;
            }
            else if (marked[i] != End && marked[i] != IndexEnd)
            {
                builder.Append(marked[i]);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Fits marked output to the HTML the fitter produced from the same render (cut, with an ellipsis and closing tags):
    /// the markers of the part both share are kept, a piece the cut runs through ends at the cut, and what the fitter
    /// added follows unmarked. Stripping the result gives <paramref name="fitted"/> exactly.
    /// </summary>
    /// <param name="marked">The marked render.</param>
    /// <param name="fitted">The HTML as it is sent.</param>
    /// <returns>Marked HTML whose stripped text is <paramref name="fitted"/>.</returns>
    public static string Align(string marked, string fitted)
    {
        ArgumentNullException.ThrowIfNull(marked);
        ArgumentNullException.ThrowIfNull(fitted);
        var plain = Strip(marked);
        if (string.Equals(plain, fitted, StringComparison.Ordinal))
        {
            return marked;
        }

        var shared = 0;
        while (shared < plain.Length && shared < fitted.Length && plain[shared] == fitted[shared])
        {
            shared++;
        }

        var builder = new StringBuilder(fitted.Length + (marked.Length - plain.Length));
        var count = 0;
        var open = false;
        for (var i = 0; i < marked.Length; i++)
        {
            var c = marked[i];
            if (c == Start)
            {
                var close = marked.IndexOf(IndexEnd, i + 1);
                var length = (close < 0 ? marked.Length - 1 : close) - i + 1;
                if (count < shared)
                {
                    builder.Append(marked, i, length);
                    open = true;
                }

                i += length - 1;
            }
            else if (c == End)
            {
                if (open && count <= shared)
                {
                    builder.Append(End);
                    open = false;
                }
            }
            else if (c != IndexEnd)
            {
                if (count < shared)
                {
                    builder.Append(c);
                }

                count++;
            }
        }

        if (open)
        {
            builder.Append(End);
        }

        return builder.Append(fitted, shared, fitted.Length - shared).ToString();
    }
}
