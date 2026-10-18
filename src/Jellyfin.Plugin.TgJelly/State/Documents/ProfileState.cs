using System.Collections.Generic;

namespace Jellyfin.Plugin.TgJelly.State.Documents;

/// <summary>
/// The bot-profile setup choice and the calls still to make (spec §6.1 step 6).
/// </summary>
public sealed class ProfileState
{
    /// <summary>Gets or sets a value indicating whether TgJelly may set the bot's profile.</summary>
    public bool SetProfile { get; set; }

    /// <summary>Gets or sets the profile calls still pending; retried at each supervisor start.</summary>
    public List<string> PendingCalls { get; set; } = [];
}
