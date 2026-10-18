using System;
using System.Collections.Generic;
using Jellyfin.Plugin.TgJelly.State.Documents;

namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// Response of <c>GET /TgJelly/Status</c> (spec §11.2). Jellyfin serializes it in PascalCase.
/// Later plans add the Telegram connection state and counters.
/// </summary>
public sealed class StatusDto
{
    /// <summary>Gets the plugin version.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Gets the Jellyfin line this build targets ("10.11" or "12").</summary>
    public string BuildTarget { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether this build matches the server (CompatGuard).</summary>
    public bool Compatible { get; init; }

    /// <summary>Gets the CompatGuard banner text when incompatible.</summary>
    public string? CompatMessage { get; init; }

    /// <summary>Gets a value indicating whether this instance owns the data folder.</summary>
    public bool InstanceOwner { get; init; }

    /// <summary>Gets the instance gate's waiting message, if any.</summary>
    public string? InstanceMessage { get; init; }

    /// <summary>Gets the current settings revision.</summary>
    public long Revision { get; init; }

    /// <summary>Gets when a wiped config was restored from backup, if ever.</summary>
    public DateTimeOffset? BackupRestoredAt { get; init; }

    /// <summary>Gets warnings from loading the state files.</summary>
    public IReadOnlyList<string> LoadWarnings { get; init; } = [];

    /// <summary>
    /// Gets the most recent errors, oldest first (spec §10.3 <c>status.lastErrors</c>).
    /// </summary>
    public IReadOnlyList<StatusError> LastErrors { get; init; } = [];
}
