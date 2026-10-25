using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TgJelly.State.Documents;

/// <summary>
/// One destination's entry in <c>state.health</c>.
/// </summary>
public sealed class DestinationHealth
{
    /// <summary>
    /// Gets or sets the status.
    /// </summary>
    public HealthStatus Status { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether photos are blocked there (posters are skipped until a re-check).
    /// </summary>
    public bool PhotosBlocked { get; set; }

    /// <summary>
    /// Gets or sets the last problem's description.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Gets or sets when the status last changed or was checked.
    /// </summary>
    public DateTimeOffset? At { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether managers were told about the current problem.
    /// </summary>
    public bool Notified { get; set; }

    /// <summary>
    /// Gets or sets when the next re-check is due, while unhealthy or photos are blocked.
    /// </summary>
    public DateTimeOffset? NextCheckAt { get; set; }

    /// <summary>
    /// Gets or sets the routed topics Telegram said are gone; their messages go to the destination's own topic until
    /// the next check passes.
    /// </summary>
    public List<int> MissingTopics { get; set; } = [];

    /// <summary>
    /// Returns a copy.
    /// </summary>
    /// <returns>The copy.</returns>
    public DestinationHealth Copy()
    {
        var copy = (DestinationHealth)MemberwiseClone();
        copy.MissingTopics = [.. MissingTopics];
        return copy;
    }
}
