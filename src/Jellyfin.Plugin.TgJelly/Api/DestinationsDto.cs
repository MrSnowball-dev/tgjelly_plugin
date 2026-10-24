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

    /// <summary>Gets the chats the bot was added to outside setup that are worth offering (spec §6.5).</summary>
    public IReadOnlyList<DetectedChatDto> DetectedChats { get; init; } = [];

    /// <summary>Gets a value indicating whether the dashboard's manual block is unlocked (spec §6.5).</summary>
    public bool ManualUnlocked { get; init; }

    /// <summary>Gets the event catalog, for the destination editor.</summary>
    public CatalogDto Catalog { get; init; } = new();
}
