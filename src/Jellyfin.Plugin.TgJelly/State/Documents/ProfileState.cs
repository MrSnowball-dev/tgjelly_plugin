using System.Collections.Generic;

namespace Jellyfin.Plugin.TgJelly.State.Documents;

/// <summary>
/// Left from the bot-profile setup that was removed on 2026-10-25 (TgJelly no longer changes the bot's profile); kept
/// so older state files load.
/// </summary>
public sealed class ProfileState
{
    /// <summary>Gets or sets a value indicating whether TgJelly may set the bot's profile.</summary>
    public bool SetProfile { get; set; }

    /// <summary>Gets or sets the profile calls an older version left pending; nothing runs them now.</summary>
    public List<string> PendingCalls { get; set; } = [];
}
