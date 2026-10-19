using System;
using System.Collections.Generic;
using System.Globalization;

namespace Jellyfin.Plugin.TgJelly.Library;

/// <summary>
/// Builds an item's identity set (spec §7.4 step 8): its provider IDs plus, for numbered episodes, the series, season
/// and episode. Used to recognise upgrades of something already in the library or removed recently.
/// </summary>
public static class Identities
{
    private static readonly (string Provider, string Prefix)[] Providers =
    [
        ("Imdb", "imdb"), ("Tmdb", "tmdb"), ("Tvdb", "tvdb"), ("MusicBrainzAlbum", "musicbrainz"),
    ];

    /// <summary>
    /// Returns the identity set, in the order imdb, tmdb, tvdb, musicbrainz, series.
    /// </summary>
    /// <param name="kind">The item kind.</param>
    /// <param name="providerIds">The item's provider IDs (keys matched case-insensitively).</param>
    /// <param name="seriesId">For episodes, the series.</param>
    /// <param name="seasonNumber">For episodes, the season number.</param>
    /// <param name="episodeNumber">For episodes, the episode number.</param>
    /// <returns>The identities; empty when none are known.</returns>
    public static IReadOnlyList<string> Of(MediaKind kind, IReadOnlyDictionary<string, string>? providerIds, Guid seriesId, int? seasonNumber, int? episodeNumber)
    {
        var result = new List<string>();
        if (providerIds is not null)
        {
            foreach (var (provider, prefix) in Providers)
            {
                foreach (var (key, value) in providerIds)
                {
                    if (string.Equals(key, provider, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
                    {
                        result.Add(prefix + ":" + value.Trim());
                        break;
                    }
                }
            }
        }

        if (kind == MediaKind.Episode && seriesId != Guid.Empty && seasonNumber is { } season && episodeNumber is { } episode)
        {
            result.Add(string.Create(CultureInfo.InvariantCulture, $"series:{seriesId:N}:S{season:00}E{episode:00}"));
        }

        return result;
    }
}
