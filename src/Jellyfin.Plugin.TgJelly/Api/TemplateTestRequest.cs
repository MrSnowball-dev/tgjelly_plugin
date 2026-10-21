namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// <c>POST TgJelly/Templates/Test</c> body.
/// </summary>
public sealed class TemplateTestRequest
{
    /// <summary>Gets or sets the event key whose sample is sent.</summary>
    public string EventKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the template text.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Gets or sets the destination that receives the sample.</summary>
    public string DestinationId { get; set; } = string.Empty;
}
