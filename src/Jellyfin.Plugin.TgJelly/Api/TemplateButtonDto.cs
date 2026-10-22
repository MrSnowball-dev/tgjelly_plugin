namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// One inline button of a sample message.
/// </summary>
public sealed class TemplateButtonDto
{
    /// <summary>Gets the label.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Gets the link.</summary>
    public string Url { get; init; } = string.Empty;
}
