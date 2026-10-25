using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace Jellyfin.Plugin.TgJelly.Destinations;

/// <summary>
/// Creates destination IDs (8 characters from <c>a-z0-9</c>).
/// </summary>
public static class DestinationIds
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>
    /// Returns a new random ID that isn't in <paramref name="existing"/>.
    /// </summary>
    /// <param name="existing">The IDs already in use.</param>
    /// <returns>The ID.</returns>
    public static string New(IEnumerable<string> existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        var taken = existing as ISet<string> ?? existing.ToHashSet(StringComparer.Ordinal);
        while (true)
        {
            var id = RandomNumberGenerator.GetString(Alphabet, 8);
            if (!taken.Contains(id))
            {
                return id;
            }
        }
    }
}
