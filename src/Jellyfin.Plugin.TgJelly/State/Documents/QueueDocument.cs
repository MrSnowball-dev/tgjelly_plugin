namespace Jellyfin.Plugin.TgJelly.State.Documents;

/// <summary>
/// The contents of queue.json: larger pipeline state saved every 10 s while it changes, or within 1 s
/// after an urgent change (spec §10.3). Later plans add their sections here.
/// </summary>
public sealed class QueueDocument
{
    /// <summary>Gets or sets the document format version.</summary>
    public int Version { get; set; } = 1;
}
