using System;

namespace Jellyfin.Plugin.TgJelly.State.Documents;

/// <summary>
/// What the update checker remembers.
/// </summary>
public sealed class UpdateCheckState
{
    /// <summary>Gets or sets when the releases were last read successfully.</summary>
    public DateTimeOffset? LastCheckedAt { get; set; }

    /// <summary>Gets or sets the tag of the last announced release.</summary>
    public string LastNotifiedVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets the number of failed checks in a row.</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>Gets or sets the last failure, redacted.</summary>
    public string? LastError { get; set; }
}
