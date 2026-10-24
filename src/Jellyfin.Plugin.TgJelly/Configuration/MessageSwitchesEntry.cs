namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// The switches of one message, stored in <see cref="MessageOptions.PerMessage"/>.
/// </summary>
public class MessageSwitchesEntry
{
    /// <summary>Gets or sets the event key, or <see cref="MessageOptions.DigestKey"/>.</summary>
    public string EventKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the switches.</summary>
    public MessageSwitches Switches { get; set; } = new();
}
