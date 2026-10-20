using System;

namespace Jellyfin.Plugin.TgJelly.State.Documents;

/// <summary>
/// A problem with a custom template found at send time (spec §8.5, §9.4 row 6).
/// </summary>
public sealed class TemplateFlag
{
    /// <summary>Gets or sets the kind: <c>error</c> or <c>truncated</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the error text, empty for <c>truncated</c>.</summary>
    public string Error { get; set; } = string.Empty;

    /// <summary>Gets or sets when the flag was set.</summary>
    public DateTimeOffset At { get; set; }

    /// <summary>Gets or sets the hash of the template text the flag is about.</summary>
    public string TextHash { get; set; } = string.Empty;
}
