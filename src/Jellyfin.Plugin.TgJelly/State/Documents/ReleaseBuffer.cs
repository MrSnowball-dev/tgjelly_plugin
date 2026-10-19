using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TgJelly.State.Documents;

/// <summary>
/// The closed groups of one destination, released together (spec §7.4 steps 5–6).
/// </summary>
public sealed class ReleaseBuffer
{
    /// <summary>Gets or sets when the first group entered the buffer.</summary>
    public DateTimeOffset FirstAt { get; set; }

    /// <summary>Gets or sets when the latest group entered the buffer.</summary>
    public DateTimeOffset LastAt { get; set; }

    /// <summary>Gets or sets the groups.</summary>
    public List<MediaGroupState> Groups { get; set; } = [];
}
