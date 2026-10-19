using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TgJelly.State.Documents;

/// <summary>
/// Markers that keep repeated security events quiet (spec §7.5).
/// </summary>
public sealed class DedupeSection
{
    /// <summary>Gets or sets the last announced sign-in, keyed <c>userId|deviceId</c> (12 h).</summary>
    public Dictionary<string, DateTimeOffset> Logins { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets recently created users, keyed by user ID (10 s), whose first password change is swallowed.</summary>
    public Dictionary<string, DateTimeOffset> UserCreated { get; set; } = new(StringComparer.Ordinal);
}
