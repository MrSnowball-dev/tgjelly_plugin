using System;
using System.Collections.Generic;
using Jellyfin.Plugin.TgJelly.Configuration;

namespace Jellyfin.Plugin.TgJelly.Destinations;

/// <summary>
/// Names a destination in stored text by its ID, never its title or chat ID (spec §12): diagnostics can't leak the title
/// of a destination that was removed after the text was written. The dashboard shows a live destination's title instead.
/// </summary>
public static class DestinationRef
{
    /// <summary>
    /// Returns the reference for a destination, <c>&lt;dest:ID&gt;</c>.
    /// </summary>
    /// <param name="destination">The destination.</param>
    /// <returns>The reference.</returns>
    public static string Of(Destination destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return "<dest:" + destination.Id + ">";
    }

    /// <summary>
    /// Replaces the references to the given destinations with their titles; others stay as they are.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="destinations">The current destinations.</param>
    /// <returns>The text for an admin to read.</returns>
    public static string WithTitles(string text, IEnumerable<Destination> destinations)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(destinations);
        foreach (var destination in destinations)
        {
            if (destination.Title.Length > 0)
            {
                text = text.Replace(Of(destination), destination.Title, StringComparison.Ordinal);
            }
        }

        return text;
    }
}
