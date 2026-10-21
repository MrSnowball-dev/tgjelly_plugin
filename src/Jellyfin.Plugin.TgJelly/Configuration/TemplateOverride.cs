namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// A custom template for one event key (spec §8.5).
/// </summary>
public class TemplateOverride
{
    /// <summary>Gets or sets the event key.</summary>
    public string EventKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the template is used. Ignored: an override with text is the
    /// template in use. Kept so settings files from earlier versions still load; a migration removes the overrides
    /// that were switched off.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the Scriban template text.</summary>
    public string Text { get; set; } = string.Empty;
}
