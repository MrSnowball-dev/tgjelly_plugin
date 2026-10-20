namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// <c>POST TgJelly/Templates/Validate</c> body.
/// </summary>
public sealed class TemplateValidateRequest
{
    /// <summary>Gets or sets the event key whose sample models are used.</summary>
    public string EventKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the template text.</summary>
    public string Text { get; set; } = string.Empty;
}
