namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// One message's switches to change, in <see cref="MessageSettingsUpdate.PerMessage"/>.
/// </summary>
public sealed class MessageSwitchesChange
{
    /// <summary>Gets or sets the event key, or <c>media.digest</c>.</summary>
    public string EventKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the switches to change; the ones left out keep their value.</summary>
    public MessageSwitchesPatch? Switches { get; set; }
}
