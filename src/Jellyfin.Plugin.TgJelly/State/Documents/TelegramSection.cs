namespace Jellyfin.Plugin.TgJelly.State.Documents;

/// <summary>
/// <c>state.telegram</c>. The update offset is in memory only.
/// </summary>
public sealed class TelegramSection
{
    /// <summary>Gets or sets the connected bot's user ID; kept on disconnect so the same bot isn't treated as a switch.</summary>
    public long? BotId { get; set; }

    /// <summary>Gets or sets the connected bot's username.</summary>
    public string BotUsername { get; set; } = string.Empty;
}
