using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.TgJelly.Logging;

/// <summary>
/// Removes bot tokens from text before it is logged or shown.
/// </summary>
public static partial class SecretRedactor
{
    /// <summary>
    /// What a token is replaced with.
    /// </summary>
    public const string Placeholder = "<token>";

    /// <summary>
    /// Replaces every bot token in <paramref name="text"/> with <see cref="Placeholder"/>.
    /// </summary>
    /// <param name="text">The text, or <c>null</c>.</param>
    /// <returns>The redacted text; empty for <c>null</c>.</returns>
    public static string Redact(string? text)
        => string.IsNullOrEmpty(text) ? string.Empty : TokenPattern().Replace(text, Placeholder);

    [GeneratedRegex(@"\d{5,}:[A-Za-z0-9_-]{30,}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}
