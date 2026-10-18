using System;

namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// A destination's health for the dashboard.
/// </summary>
public sealed class HealthDto
{
    /// <summary>Gets the status: Ok, NeedsPermissions, TopicMissing or Lost.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether photos are blocked there.</summary>
    public bool PhotosBlocked { get; init; }

    /// <summary>Gets the last problem's description.</summary>
    public string? LastError { get; init; }

    /// <summary>Gets when the status last changed or was checked.</summary>
    public DateTimeOffset? At { get; init; }
}
