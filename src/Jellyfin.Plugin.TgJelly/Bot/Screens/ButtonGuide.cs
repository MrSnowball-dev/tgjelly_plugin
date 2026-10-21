using System;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.Strings;

namespace Jellyfin.Plugin.TgJelly.Bot.Screens;

/// <summary>
/// Builds the lines that explain a screen's buttons, one per button, so that every button says what it does. The label
/// comes from the same string key as the button itself, so the two can't drift apart.
/// </summary>
internal static class ButtonGuide
{
    /// <summary>
    /// Builds the explanation block of a screen.
    /// </summary>
    /// <param name="strings">The string table.</param>
    /// <param name="formatter">The formatter, for escaping.</param>
    /// <param name="buttons">The label key and the about key of each button, in button order.</param>
    /// <returns>A blank line followed by one line per button, or the empty string without buttons.</returns>
    public static string Lines(StringTable strings, Formatter formatter, params (string LabelKey, string AboutKey)[] buttons)
    {
        ArgumentNullException.ThrowIfNull(strings);
        ArgumentNullException.ThrowIfNull(formatter);
        ArgumentNullException.ThrowIfNull(buttons);
        return buttons.Length == 0
            ? string.Empty
            : "\n\n" + string.Join('\n', buttons.Select(b => Line(formatter, strings.Get(b.LabelKey), strings.Get(b.AboutKey))));
    }

    /// <summary>
    /// Builds the explanation block from lines already built, for screens where some labels carry arguments.
    /// </summary>
    /// <param name="lines">The lines, in button order, from <see cref="Line(Formatter, string, string)"/>.</param>
    /// <returns>A blank line followed by the lines, or the empty string without any.</returns>
    public static string Block(params string[] lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return lines.Length == 0 ? string.Empty : "\n\n" + string.Join('\n', lines);
    }

    /// <summary>
    /// Builds one line for a button whose label is a fixed string key.
    /// </summary>
    /// <param name="strings">The string table.</param>
    /// <param name="formatter">The formatter, for escaping.</param>
    /// <param name="labelKey">The key of the button's label.</param>
    /// <param name="aboutKey">The key of what the button does.</param>
    /// <returns>The line.</returns>
    public static string Line(StringTable strings, Formatter formatter, string labelKey, string aboutKey)
    {
        ArgumentNullException.ThrowIfNull(strings);
        return Line(formatter, strings.Get(labelKey), strings.Get(aboutKey));
    }

    /// <summary>
    /// Builds one line for a label that isn't a fixed string key.
    /// </summary>
    /// <param name="formatter">The formatter, for escaping.</param>
    /// <param name="label">The button's label, unescaped.</param>
    /// <param name="about">What the button does, unescaped.</param>
    /// <returns>The line.</returns>
    public static string Line(Formatter formatter, string label, string about)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        return "<b>" + formatter.Escape(label) + "</b> — " + formatter.Escape(about);
    }
}
