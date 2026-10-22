namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// <c>PUT TgJelly/Templates/{eventKey}</c> body.
/// </summary>
public sealed class TemplateUpdateRequest
{
    /// <summary>Gets or sets the revision the caller's data is based on.</summary>
    public long ExpectedRevision { get; set; }

    /// <summary>Gets or sets the template text.</summary>
    public string Text { get; set; } = string.Empty;
}
