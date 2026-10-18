using System;

namespace Jellyfin.Plugin.TgJelly.State.Documents;

/// <summary>
/// Set when Telegram rejected a button URL (spec §9.4 row 7); cleared when PublicUrl changes or a send with buttons succeeds.
/// </summary>
public sealed class PublicUrlFlag
{
    /// <summary>Gets or sets the public URL that was rejected.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets when.</summary>
    public DateTimeOffset At { get; set; }
}
