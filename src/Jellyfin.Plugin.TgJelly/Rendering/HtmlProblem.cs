using System;
using Jellyfin.Plugin.TgJelly.Strings;

namespace Jellyfin.Plugin.TgJelly.Rendering;

/// <summary>
/// One problem <see cref="TelegramHtmlValidator"/> found: a string key and the tag it is about.
/// </summary>
/// <param name="Key">The string key of the message, e.g. <c>templates.html.notAllowed</c>.</param>
/// <param name="Tag">The tag name, filled into the message's <c>{tag}</c>.</param>
public sealed record HtmlProblem(string Key, string Tag)
{
    /// <summary>
    /// Returns the problem as text from the string table.
    /// </summary>
    /// <param name="strings">The string table.</param>
    /// <returns>The message.</returns>
    public string Describe(StringTable strings)
    {
        ArgumentNullException.ThrowIfNull(strings);
        return strings.Get(Key, ("tag", Tag));
    }
}
