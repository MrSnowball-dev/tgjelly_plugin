namespace Jellyfin.Plugin.TgJelly.State.Documents;

/// <summary>
/// An inline button: a URL button, or a callback button when <see cref="CallbackData"/> is set.
/// </summary>
public sealed class OutboxButton
{
    /// <summary>Gets or sets the label.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Gets or sets the URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the callback data of a callback button; <c>null</c> for a URL button.</summary>
    public string? CallbackData { get; set; }
}
