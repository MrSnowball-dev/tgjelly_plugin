using System;
using System.Globalization;

namespace Jellyfin.Plugin.TgJelly.Updates;

/// <summary>
/// Reads versions from release tags, leniently.
/// </summary>
public static class ReleaseVersion
{
    /// <summary>
    /// Parses a tag: a leading <c>v</c> is stripped, then the leading run of digits and dots is read, so
    /// <c>v12.2</c> is 12.2.0.0 and <c>12.3-rc1</c> is 12.3.0.0.
    /// </summary>
    /// <param name="tag">The tag.</param>
    /// <param name="version">The version, four parts.</param>
    /// <returns><c>true</c> when the tag starts with a version.</returns>
    public static bool TryParse(string? tag, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        var text = tag?.Trim() ?? string.Empty;
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        var end = 0;
        while (end < text.Length && (char.IsAsciiDigit(text[end]) || text[end] == '.'))
        {
            end++;
        }

        var parts = text[..end].Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        var numbers = new int[4];
        for (var i = 0; i < Math.Min(parts.Length, 4); i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }

        version = new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
        return true;
    }

    /// <summary>
    /// Fills a version's missing parts with zeros.
    /// </summary>
    /// <param name="v">The version.</param>
    /// <returns>The same version with four parts.</returns>
    public static Version Normalize(Version v)
    {
        ArgumentNullException.ThrowIfNull(v);
        return new Version(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
    }
}
