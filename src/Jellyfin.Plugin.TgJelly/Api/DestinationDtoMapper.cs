using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Bot;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.Library;
using Jellyfin.Plugin.TgJelly.Pipeline;
using Jellyfin.Plugin.TgJelly.State.Documents;

namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// Builds the destination DTOs that the destination and detected-chat endpoints share.
/// </summary>
public sealed class DestinationDtoMapper
{
    private readonly HealthService _health;
    private readonly ILibraryAccess _library;
    private readonly BotMemory _memory;

    /// <summary>
    /// Initializes a new instance of the <see cref="DestinationDtoMapper"/> class.
    /// </summary>
    /// <param name="health">The health service.</param>
    /// <param name="library">The library, for removed-library flags.</param>
    /// <param name="memory">The bot's memory, for the detected chats.</param>
    public DestinationDtoMapper(HealthService health, ILibraryAccess library, BotMemory memory)
    {
        _health = health;
        _library = library;
        _memory = memory;
    }

    /// <summary>
    /// Maps a health record.
    /// </summary>
    /// <param name="health">The record.</param>
    /// <returns>The DTO.</returns>
    public static HealthDto ToDto(DestinationHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);
        return new() { Status = health.Status.ToString(), PhotosBlocked = health.PhotosBlocked, LastError = health.LastError, At = health.At };
    }

    /// <summary>
    /// Maps a destination.
    /// </summary>
    /// <param name="destination">The destination.</param>
    /// <returns>The DTO.</returns>
    public DestinationDto ToDto(Destination destination) => ToDto(destination, _library.GetVirtualFolders().Select(l => l.Id).ToHashSet());

    /// <summary>
    /// Maps a configuration to the destination list, with its detected chats.
    /// </summary>
    /// <param name="configuration">The configuration.</param>
    /// <returns>The DTO.</returns>
    public DestinationsDto List(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var existing = _library.GetVirtualFolders().Select(l => l.Id).ToHashSet();
        return new()
        {
            Revision = configuration.Revision,
            Items = [.. configuration.Destinations.Select(d => ToDto(d, existing))],
            DetectedChats = [.. _memory.VisibleDetected(configuration).Select(c => new DetectedChatDto { ChatId = c.ChatId, Title = c.Title, Kind = c.Kind.ToString(), DetectedAt = c.DetectedAt })],
            ManualUnlocked = false,
        };
    }

    private DestinationDto ToDto(Destination d, IReadOnlyCollection<Guid> existing) => new()
    {
        Id = d.Id,
        ChatId = d.ChatId,
        ThreadId = d.ThreadId,
        Title = d.Title,
        Kind = d.Kind.ToString(),
        Enabled = d.Enabled,
        Preset = d.Preset.ToString(),
        Events = d.Events,
        LibraryIds = d.LibraryIds,
        SilentGroups = d.SilentGroups,
        Health = ToDto(_health.Get(d.Id)),
        RemovedLibraryIds = LibraryFilter.Removed(d, existing),
        LibraryFilterBroken = LibraryFilter.RefersOnlyToRemoved(d, existing),
    };
}
