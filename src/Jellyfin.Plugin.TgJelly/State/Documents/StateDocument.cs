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

    /// <summary>Gets or sets the connected bot's identity and profile state.</summary>
    public TelegramSection Telegram { get; set; } = new();

    /// <summary>Gets or sets each destination's health, keyed by destination ID (spec §9.4).</summary>
    public Dictionary<string, DestinationHealth> Health { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets the update checker's state (spec §7.6).</summary>
    public UpdateCheckState UpdateCheck { get; set; } = new();

    /// <summary>Gets or sets each private chat's menu or setup flow, keyed by chat ID (invariant culture).</summary>
    public Dictionary<string, FlowState> Flows { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets when each manager last used the bot, keyed by private chat ID (invariant culture).</summary>
    public Dictionary<string, DateTimeOffset> Managers { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets the chats the bot was added to that are not destinations yet.</summary>
    public List<DetectedChat> DetectedChats { get; set; } = [];

    /// <summary>Gets or sets the recent setup failures.</summary>
    public List<SetupFailure> SetupFailures { get; set; } = [];
}
