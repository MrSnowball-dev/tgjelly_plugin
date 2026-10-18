using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Jellyfin.Plugin.TgJelly.Library;

namespace Jellyfin.Plugin.TgJelly.Rendering;

/// <summary>
/// Picks a poster that can be uploaded as-is (spec §8.2, D8, D13): local, existing, at most 10 MB, and a JPEG, PNG or
/// WebP file.
/// </summary>
public static class PosterPicker
{
    /// <summary>The largest photo Telegram accepts as an upload.</summary>
    public const long MaxBytes = 10_485_760;

    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".webp"];

    /// <summary>
    /// Returns the first eligible candidate.
    /// </summary>
    /// <param name="candidates">The candidates, in order of preference.</param>
    /// <returns>The poster, or <c>null</c> for the text-only layout.</returns>
    public static PosterRef? Pick(IEnumerable<(Guid ItemId, ImageRef? Image)> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        foreach (var (itemId, image) in candidates)
        {
            if (image is not { IsLocal: true } || !Array.Exists(Extensions, e => image.Path.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            try
            {
                var file = new FileInfo(image.Path);
                if (file.Exists && file.Length <= MaxBytes)
                {
                    return new PosterRef(image.Path, itemId.ToString("N", CultureInfo.InvariantCulture) + ":" + image.DateModified.Ticks.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // An unreadable path is skipped like a missing file.
            }
        }

        return null;
    }
}
