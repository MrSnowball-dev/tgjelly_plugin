namespace Jellyfin.Plugin.TgJelly.Bot.Flows;

/// <summary>
/// The native chat pickers the bot offers on a reply keyboard (spec §6.2).
/// </summary>
public enum PickerKind
{
    /// <summary>A channel where the bot becomes an admin that can post and edit messages.</summary>
    Channel,

    /// <summary>A group the bot joins as a member.</summary>
    Group,

    /// <summary>A forum the bot is promoted in, so it can create topics.</summary>
    TopicPromotion,
}
