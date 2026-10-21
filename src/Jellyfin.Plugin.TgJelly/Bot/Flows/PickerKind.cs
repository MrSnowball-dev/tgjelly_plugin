namespace Jellyfin.Plugin.TgJelly.Bot.Flows;

/// <summary>
/// The native chat pickers the bot still offers on a reply keyboard: only the topic promotion, until it moves to an
/// inline link too.
/// </summary>
public enum PickerKind
{
    /// <summary>A forum the bot is promoted in, so it can create topics.</summary>
    TopicPromotion,
}
