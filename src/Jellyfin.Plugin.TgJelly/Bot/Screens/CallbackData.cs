using System;
using System.Collections.Generic;
using System.Text;

namespace Jellyfin.Plugin.TgJelly.Bot.Screens;

/// <summary>
/// Versioned inline-button data: <c>1|action|arg|arg</c>, at most 64 bytes (Telegram's limit). A press with another
/// version, such as one on a message from an older build, does not parse.
/// </summary>
public static class CallbackData
{
    /// <summary>Telegram's limit for callback data, in UTF-8 bytes.</summary>
    public const int MaxBytes = 64;

    /// <summary>The current format version.</summary>
    public const string Version = "1";

    private const char Separator = '|';

    /// <summary>
    /// Builds callback data.
    /// </summary>
    /// <param name="action">The action name.</param>
    /// <param name="args">The arguments.</param>
    /// <returns>The data.</returns>
    /// <exception cref="ArgumentException">A part contains <c>|</c>, or the data is longer than <see cref="MaxBytes"/>.</exception>
    public static string Format(string action, params string[] args)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(args);
        var parts = new List<string>(args.Length + 2) { Version, action };
        parts.AddRange(args);
        foreach (var part in parts)
        {
            if (part.Contains(Separator, StringComparison.Ordinal))
            {
                throw new ArgumentException("Callback data parts can't contain '|'.", nameof(args));
            }
        }

        var data = string.Join(Separator, parts);
        if (Encoding.UTF8.GetByteCount(data) > MaxBytes)
        {
            throw new ArgumentException($"Callback data is longer than {MaxBytes} bytes: {data}", nameof(args));
        }

        return data;
    }

    /// <summary>
    /// Parses callback data.
    /// </summary>
    /// <param name="data">The data from a button press.</param>
    /// <param name="action">The action name.</param>
    /// <param name="args">The arguments.</param>
    /// <returns><c>false</c> for null, another version or an empty action.</returns>
    public static bool TryParse(string? data, out string action, out IReadOnlyList<string> args)
    {
        action = string.Empty;
        args = [];
        if (string.IsNullOrEmpty(data))
        {
            return false;
        }

        var parts = data.Split(Separator);
        if (parts.Length < 2 || parts[0] != Version || parts[1].Length == 0)
        {
            return false;
        }

        action = parts[1];
        args = parts[2..];
        return true;
    }
}
