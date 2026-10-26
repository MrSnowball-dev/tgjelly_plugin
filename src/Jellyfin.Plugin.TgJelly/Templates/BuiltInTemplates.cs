using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.TgJelly.Templates;

/// <summary>
/// The built-in templates: embedded resources, one file per key and language, at
/// <c>Templates/BuiltIn/&lt;language&gt;/&lt;key&gt;.sbn</c>.
/// </summary>
public static class BuiltInTemplates
{
    private const string Fallback = "en";
    private const string Prefix = "Jellyfin.Plugin.TgJelly.Templates.BuiltIn.";
    private const string Suffix = ".sbn";

    private static readonly Dictionary<string, Dictionary<string, string>> Texts = Load();

    /// <summary>
    /// Returns the built-in template of a key.
    /// </summary>
    /// <param name="eventKey">The template key.</param>
    /// <param name="language">The language; a key the language has no file for falls back to English.</param>
    /// <returns>The template text with <c>\n</c> line ends, or <c>null</c> when there is none.</returns>
    public static string? Get(string eventKey, string language)
    {
        ArgumentNullException.ThrowIfNull(eventKey);
        if (language is not null && Texts.TryGetValue(Folder(language), out var texts) && texts.TryGetValue(eventKey, out var text))
        {
            return text;
        }

        return Texts.TryGetValue(Fallback, out var en) && en.TryGetValue(eventKey, out var fallback) ? fallback : null;
    }

    /// <summary>
    /// Returns the keys that have a built-in template in a language.
    /// </summary>
    /// <param name="language">The language.</param>
    /// <returns>The keys, in ordinal order; empty for a language without templates.</returns>
    public static IReadOnlyList<string> Keys(string language)
    {
        ArgumentNullException.ThrowIfNull(language);
        return Texts.TryGetValue(Folder(language), out var texts) ? [.. texts.Keys.Order(StringComparer.Ordinal)] : [];
    }

    // The build writes a folder such as pt-BR into the resource name as pt_BR.
    private static string Folder(string language) => language.Replace('-', '_');

    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        var assembly = typeof(BuiltInTemplates).Assembly;
        var texts = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(Prefix, StringComparison.Ordinal) || !name.EndsWith(Suffix, StringComparison.Ordinal))
            {
                continue;
            }

            // <language>.<key>.sbn: the language has no dot, the key does.
            var rest = name[Prefix.Length..^Suffix.Length];
            var dot = rest.IndexOf('.', StringComparison.Ordinal);
            if (dot <= 0)
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var language = rest[..dot];
            if (!texts.TryGetValue(language, out var byKey))
            {
                byKey = new Dictionary<string, string>(StringComparer.Ordinal);
                texts[language] = byKey;
            }

            // A checkout with CRLF line ends must not change the output.
            byKey[rest[(dot + 1)..]] = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
        }

        return texts;
    }
}
