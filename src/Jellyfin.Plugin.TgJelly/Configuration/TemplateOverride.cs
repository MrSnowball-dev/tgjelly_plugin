namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// A custom template for one event key.
/// </summary>
public class TemplateOverride
{
    /// <summary>Gets or sets the event key.</summary>
    public string EventKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the Scriban template text.</summary>
    public string Text { get; set; } = string.Empty;
}
