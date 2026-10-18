using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TgJelly.State.Documents;

/// <summary>
/// The contents of state.json: small, bot-scoped runtime state saved within 1 s of a change (spec §10.3).
/// Later plans add their sections here.
/// </summary>
public sealed class StateDocument
{
    /// <summary>Gets or sets the document format version.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Gets or sets the status section shown on the dashboard.</summary>
    public StatusSection Status { get; set; } = new();

    /// <summary>Gets or sets each destination's health, keyed by destination ID (spec §9.4).</summary>
    public Dictionary<string, DestinationHealth> Health { get; set; } = new(StringComparer.Ordinal);
}
