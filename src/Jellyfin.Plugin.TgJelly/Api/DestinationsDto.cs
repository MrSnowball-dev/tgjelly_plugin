using System.Collections.Generic;

namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// <c>GET TgJelly/Destinations</c>.
/// </summary>
public sealed class DestinationsDto
{
    /// <summary>Gets the settings revision, for <c>expectedRevision</c>.</summary>
    public long Revision { get; init; }

    /// <summary>Gets the destinations.</summary>
    public IReadOnlyList<DestinationDto> Items { get; init; } = [];
}
