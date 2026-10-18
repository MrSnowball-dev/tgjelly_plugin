namespace Jellyfin.Plugin.TgJelly.State.Documents;

/// <summary>
/// An inline URL button.
/// </summary>
public sealed class OutboxButton
{
    /// <summary>Gets or sets the label.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Gets or sets the URL.</summary>
    public string Url { get; set; } = string.Empty;
}
