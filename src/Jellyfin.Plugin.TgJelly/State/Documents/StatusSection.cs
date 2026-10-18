using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TgJelly.State.Documents;

/// <summary>
/// Notices for the dashboard's Status section (spec §6.6, §10.3 <c>status</c>).
/// </summary>
public sealed class StatusSection
{
    /// <summary>Gets or sets when a wiped config was restored from settings.bak.json.</summary>
    public DateTimeOffset? BackupRestoredAt { get; set; }

    /// <summary>Gets or sets warnings from loading the state files (e.g. a quarantined file).</summary>
    public List<string> LoadWarnings { get; set; } = [];

    /// <summary>
    /// Gets or sets the most recent errors, newest last (capped at <see cref="StateStore.MaxLastErrors"/>).
    /// </summary>
    public List<StatusError> LastErrors { get; set; } = [];

    /// <summary>
    /// Gets or sets the drop and eviction counters.
    /// </summary>
    public StatusCounters Counters { get; set; } = new();
}
