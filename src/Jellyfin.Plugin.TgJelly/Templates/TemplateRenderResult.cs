namespace Jellyfin.Plugin.TgJelly.Templates;

/// <summary>
/// The outcome of rendering a template: the output, or the error.
/// </summary>
/// <param name="Output">The rendered text; null when rendering failed.</param>
/// <param name="Error">The failure; null when rendering succeeded.</param>
/// <param name="TooLong">True when the failure is the output limit, so a shorter model may still render.</param>
public sealed record TemplateRenderResult(string? Output, TemplateError? Error, bool TooLong = false)
{
    /// <summary>Gets a value indicating whether rendering succeeded.</summary>
    public bool Ok => Error is null;
}
