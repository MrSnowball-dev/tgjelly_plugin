using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TgJelly.Templates;

/// <summary>
/// Picks the right word form for a count, per language.
/// </summary>
public static class PluralRules
{
    /// <summary>
    /// Picks a form for a count. The forms run in category order for the language; a missing form falls back to the last one.
    /// </summary>
    /// <param name="language">The language code; an unknown one uses English.</param>
    /// <param name="count">The count.</param>
    /// <param name="forms">The word forms, in category order.</param>
    /// <returns>The form, or an empty string when there are none.</returns>
    public static string Pick(string language, long count, IReadOnlyList<string> forms)
    {
        ArgumentNullException.ThrowIfNull(forms);
        if (forms.Count == 0)
        {
            return string.Empty;
        }

        var index = Category(language, count);
        return forms[Math.Min(index, forms.Count - 1)];
    }

    private static int Category(string language, long count)
    {
        // English: one, other. Every language falls back here until it gets its own rule.
        _ = language;
        return count == 1 ? 0 : 1;
    }
}
