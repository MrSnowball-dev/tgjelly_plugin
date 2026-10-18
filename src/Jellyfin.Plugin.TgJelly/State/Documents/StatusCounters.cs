namespace Jellyfin.Plugin.TgJelly.State.Documents;

/// <summary>
/// The counters of <c>status.counters</c> (spec §10.3).
/// </summary>
public sealed class StatusCounters
{
    /// <summary>
    /// Gets or sets how many intake events were dropped because a queue was full.
    /// </summary>
    public long DroppedIntake { get; set; }

    /// <summary>
    /// Gets or sets how many Outbox entries were dropped (caps, TTL, permanent errors).
    /// </summary>
    public long DroppedOutbox { get; set; }

    /// <summary>
    /// Gets or sets how many entries were evicted from capped state sections.
    /// </summary>
    public long Evictions { get; set; }
}
