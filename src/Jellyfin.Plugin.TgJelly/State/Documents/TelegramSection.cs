namespace Jellyfin.Plugin.TgJelly.State.Documents;

/// <summary>
/// <c>state.telegram</c> (spec §10.3). The update offset is in memory only (§9.2).
/// </summary>
public sealed class TelegramSection
{
    /// <summary>Gets or sets the connected bot's user ID; kept on disconnect so the same bot isn't treated as a switch.</summary>
    public long? BotId { get; set; }

    /// <summary>Gets or sets the connected bot's username.</summary>
    public string BotUsername { get; set; } = string.Empty;

    /// <summary>Gets or sets the profile setup state.</summary>
    public ProfileState Profile { get; set; } = new();

    /// <summary>Gets or sets the last request ID handed to a chat picker button.</summary>
    public int LastRequestId { get; set; }
}
