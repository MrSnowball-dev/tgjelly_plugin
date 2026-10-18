namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// A response that carries only a message.
/// </summary>
public sealed class MessageDto
{
    /// <summary>Gets the message.</summary>
    public string Message { get; init; } = string.Empty;
}
