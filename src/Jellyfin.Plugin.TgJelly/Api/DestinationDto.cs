using System.Collections.Generic;
using Jellyfin.Plugin.TgJelly.Configuration;

namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// A destination for the dashboard.
/// </summary>
public sealed class DestinationDto
{
    /// <summary>Gets the ID.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets the chat ID.</summary>
    public long ChatId { get; init; }

    /// <summary>Gets the forum topic, or 0.</summary>
    public int ThreadId { get; init; }

    /// <summary>Gets the chat title.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Gets the chat kind.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether the chat is a forum (a supergroup with topics), shown as Forum.</summary>
    public bool IsForum { get; init; }

    /// <summary>Gets, for a forum, the slots that post to their own topic.</summary>
    public IReadOnlyList<TopicRoute> TopicRoutes { get; init; } = [];

    /// <summary>Gets a value indicating whether each media event has its own topic choice.</summary>
    public bool SeparateMediaTopics { get; init; }

    /// <summary>Gets the forum topics TgJelly knows for the chat.</summary>
    public IReadOnlyList<KnownTopic> KnownTopics { get; init; } = [];

    /// <summary>Gets the routed topics Telegram said are gone (their messages go to the destination's topic).</summary>
    public IReadOnlyList<int> MissingTopics { get; init; } = [];

    /// <summary>Gets a value indicating whether it's enabled (not paused).</summary>
    public bool Enabled { get; init; }

    /// <summary>Gets the event keys.</summary>
    public IReadOnlyList<string> Events { get; init; } = [];

    /// <summary>Gets the library filter (empty means all).</summary>
    public IReadOnlyList<string> LibraryIds { get; init; } = [];

    /// <summary>Gets the silent groups.</summary>
    public IReadOnlyList<string> SilentGroups { get; init; } = [];

    /// <summary>Gets the IDs in the library filter that name removed libraries.</summary>
    public IReadOnlyList<string> RemovedLibraryIds { get; init; } = [];

    /// <summary>Gets a value indicating whether the library filter names only removed libraries, so it matches nothing.</summary>
    public bool LibraryFilterBroken { get; init; }

    /// <summary>Gets the health.</summary>
    public HealthDto Health { get; init; } = new();

    /// <summary>
    /// Gets the settings revision this destination was read at: after a change, the revision the change moved to, so the
    /// dashboard's next save (of any section) sends it as <c>expectedRevision</c>.
    /// </summary>
    public long Revision { get; init; }
}
