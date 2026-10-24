using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// <c>PUT TgJelly/Destinations/{id}</c>: only the given fields change.
/// </summary>
[SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "A JSON request body.")]
public sealed class UpdateDestinationRequest
{
    /// <summary>Gets or sets the revision the change is based on.</summary>
    public long ExpectedRevision { get; set; }

    /// <summary>Gets or sets the events; whether every event is on is worked out from them.</summary>
    public string[]? Events { get; set; }

    /// <summary>Gets or sets the library filter (Guids; empty means all).</summary>
    public string[]? LibraryIds { get; set; }

    /// <summary>Gets or sets the silent groups.</summary>
    public string[]? SilentGroups { get; set; }

    /// <summary>Gets or sets whether the destination is enabled (not paused).</summary>
    public bool? Enabled { get; set; }
}
