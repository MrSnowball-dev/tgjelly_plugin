using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.TgJelly.Strings;

/// <summary>
/// The user-facing strings (spec §8.6): an embedded JSON table per language with named <c>{placeholders}</c>
/// and plural variants (<c>key.one</c> / <c>key.other</c>). Missing keys fall back to English, key by key.
/// </summary>
public sealed partial class StringTable
{
    private const string Fallback = "en";

    private const string ResourcePrefix = "Jellyfin.Plugin.TgJelly.Strings.";
    private const string ResourceSuffix = ".json";

    private static readonly IReadOnlyList<string> Languages = [.. typeof(StringTable).Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(ResourceSuffix, StringComparison.Ordinal))
        .Select(n => n[ResourcePrefix.Length..^ResourceSuffix.Length])
        .Order(StringComparer.Ordinal)];

    private readonly IReadOnlyDictionary<string, string> _strings;
    private readonly IReadOnlyDictionary<string, string> _fallback;
    private readonly CultureInfo _culture;

    private StringTable(IReadOnlyDictionary<string, string> strings, IReadOnlyDictionary<string, string> fallback, CultureInfo culture)
    {
        _strings = strings;
        _fallback = fallback;
        _culture = culture;
    }

    /// <summary>
    /// Gets the languages that have an embedded table, derived from the resource names.
    /// </summary>
    public static IReadOnlyList<string> EmbeddedLanguages => Languages;

    /// <summary>
    /// Picks the embedded language that best matches <paramref name="lang"/>: an exact match (case-insensitive),
    /// then its primary subtag (<c>pt-BR</c> to <c>pt</c>), then English.
    /// </summary>
    /// <param name="lang">A language tag such as <c>pt-BR</c>; may be null or empty.</param>
    /// <returns>An embedded language code.</returns>
    public static string BestMatch(string? lang)
    {
        var tag = lang?.Trim().Replace('_', '-') ?? string.Empty;
        foreach (var candidate in new[] { tag, tag.Split('-')[0] })
        {
            var match = Languages.FirstOrDefault(l => string.Equals(l, candidate, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return Fallback;
    }

    /// <summary>
    /// Loads the embedded table for <paramref name="language"/>, falling back to English for missing keys
    /// (or the whole table, when there is none for that language).
    /// </summary>
    /// <param name="language">A language code such as <c>en</c>.</param>
    /// <returns>The table.</returns>
    public static StringTable LoadEmbedded(string language = Fallback)
    {
        var fallback = Read(Fallback) ?? new Dictionary<string, string>();
        var own = language == Fallback ? null : Read(language);
        return own is null
            ? new StringTable(fallback, fallback, CultureInfo.GetCultureInfo("en-US"))
            : new StringTable(own, fallback, CultureFor(language));
    }

    /// <summary>
    /// Returns every key of this table: the English table overlaid by the language's own.
    /// </summary>
    /// <returns>A copy of the merged table, with the <c>{placeholders}</c> unfilled.</returns>
    public IReadOnlyDictionary<string, string> Snapshot()
    {
        var all = new Dictionary<string, string>(_fallback, StringComparer.Ordinal);
        foreach (var (key, value) in _strings)
        {
            all[key] = value;
        }

        return all;
    }

    /// <summary>
    /// Returns the string for <paramref name="key"/> with its placeholders filled. An unknown key returns the
    /// key itself; a placeholder without an argument stays as <c>{name}</c>.
    /// </summary>
    /// <param name="key">The string key.</param>
    /// <param name="args">Named values for the placeholders.</param>
    /// <returns>The text.</returns>
    public string Get(string key, params (string Name, object? Value)[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var template = _strings.TryGetValue(key, out var s) ? s : _fallback.TryGetValue(key, out var f) ? f : key;
        if (args.Length == 0)
        {
            return template;
        }

        return Placeholder().Replace(template, m =>
        {
            foreach (var (name, value) in args)
            {
                if (string.Equals(name, m.Groups[1].Value, StringComparison.Ordinal))
                {
                    return Convert.ToString(value, _culture) ?? string.Empty;
                }
            }

            return m.Value;
        });
    }

    /// <summary>
    /// Returns <c>key.one</c> or <c>key.other</c> for <paramref name="count"/>, with <c>{count}</c> filled
    /// (formatted with digit grouping) besides the given placeholders.
    /// </summary>
    /// <param name="key">The key without the plural suffix.</param>
    /// <param name="count">The number that picks the variant.</param>
    /// <param name="args">Further named values.</param>
    /// <returns>The text.</returns>
    public string Plural(string key, long count, params (string Name, object? Value)[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var all = new (string Name, object? Value)[args.Length + 1];
        all[0] = ("count", count.ToString("N0", _culture));
        args.CopyTo(all, 1);
        return Get(key + (count == 1 ? ".one" : ".other"), all);
    }

    private static CultureInfo CultureFor(string language)
    {
        try
        {
            return CultureInfo.GetCultureInfo(language);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.GetCultureInfo("en-US");
        }
    }

    private static Dictionary<string, string>? Read(string language)
    {
        using var stream = typeof(StringTable).Assembly.GetManifestResourceStream(ResourcePrefix + language + ResourceSuffix);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd());
    }

    [GeneratedRegex(@"\{([A-Za-z0-9_]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}
