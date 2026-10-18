using System;

namespace Jellyfin.Plugin.TgJelly.State.Documents;

/// <summary>
/// One entry of <c>status.lastErrors</c> (spec §10.3), shown on the Status page.
/// </summary>
public sealed class StatusError
{
    /// <summary>
    /// Gets or sets when the error happened.
    /// </summary>
    public DateTimeOffset At { get; set; }

    /// <summary>
    /// Gets or sets the part of TgJelly that reported it, e.g. <c>outbox</c> or <c>persistence</c>.
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the error text, with secrets redacted.
    /// </summary>
    public string Message { get; set; } = string.Empty;
}
