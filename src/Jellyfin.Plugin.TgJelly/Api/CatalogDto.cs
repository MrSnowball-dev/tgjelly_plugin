using System.Collections.Generic;

namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// The event catalog the dashboard's destination editor draws its switches from.
/// </summary>
public sealed class CatalogDto
{
    /// <summary>Gets every event key, in catalog order.</summary>
    public IReadOnlyList<string> EventKeys { get; init; } = [];

    /// <summary>Gets the event groups (media, security, server).</summary>
    public IReadOnlyList<string> Groups { get; init; } = [];
}
