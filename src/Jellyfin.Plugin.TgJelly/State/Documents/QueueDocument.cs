using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TgJelly.State.Documents;

/// <summary>
/// The contents of queue.json: larger pipeline state saved every 10 s while it changes, or within 1 s
/// after an urgent change. Later plans add their sections here.
/// </summary>
public sealed class QueueDocument
{
    /// <summary>Gets or sets the document format version.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Gets or sets the queued messages of every destination.</summary>
    public List<OutboxEntry> Outbox { get; set; } = [];

    /// <summary>Gets or sets the sent messages that may be edited or replied to, keyed <c>destinationId|key</c>.</summary>
    public Dictionary<string, SentMessage> SentMessages { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets the poster file_id cache, keyed by item ID and image modification time.</summary>
    public Dictionary<string, CachedFileId> FileIdCache { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets the items (or albums) waiting for readiness, keyed by ID (<c>N</c>), with when they were first seen.</summary>
    public Dictionary<string, DateTimeOffset> PendingMedia { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets the identities of removed items with when they were removed (48 h).</summary>
    public Dictionary<string, DateTimeOffset> RemovedIdentities { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets recently added series, keyed by ID (<c>N</c>), with when they were added (24 h).</summary>
    public Dictionary<string, DateTimeOffset> NewSeries { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets the media groups still collecting items.</summary>
    public List<MediaGroupState> OpenGroups { get; set; } = [];

    /// <summary>Gets or sets the closed groups waiting for release, keyed by destination ID.</summary>
    public Dictionary<string, ReleaseBuffer> ReleaseBuffers { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets the failed-login windows, keyed by the upper-cased user name.</summary>
    public Dictionary<string, SecurityWindow> SecurityWindows { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets the dedupe markers of security events.</summary>
    public DedupeSection Dedupe { get; set; } = new();

    /// <summary>Gets or sets the recent plugin events, the reasons a restart is pending.</summary>
    public List<RecentPluginEvent> RecentPluginEvents { get; set; } = [];
}
