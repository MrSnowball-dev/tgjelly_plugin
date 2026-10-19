namespace Jellyfin.Plugin.TgJelly.Bot;

/// <summary>
/// The bot's <c>t.me</c> links (spec §6.5, §11.2), shared by the API and the bot's screens.
/// </summary>
public static class BotLinks
{
    /// <summary>Gets the link that opens the bot's setup.</summary>
    /// <param name="bot">The bot's username.</param>
    /// <returns>The link.</returns>
    public static string OpenBot(string bot) => Base(bot) + "?start=setup";

    /// <summary>Gets the link that starts adding a destination.</summary>
    /// <param name="bot">The bot's username.</param>
    /// <returns>The link.</returns>
    public static string StartAdd(string bot) => Base(bot) + "?start=add";

    /// <summary>Gets the link that adds the bot to a group.</summary>
    /// <param name="bot">The bot's username.</param>
    /// <returns>The link.</returns>
    public static string StartGroup(string bot) => Base(bot) + "?startgroup=add";

    /// <summary>Gets the link that adds the bot to a channel as an admin who can post and edit.</summary>
    /// <param name="bot">The bot's username.</param>
    /// <returns>The link.</returns>
    public static string StartChannel(string bot) => Base(bot) + "?startchannel&admin=post_messages+edit_messages";

    private static string Base(string bot) => "https://t.me/" + bot;
}
