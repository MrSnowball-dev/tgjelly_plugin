namespace Jellyfin.Plugin.TgJelly.Templates;

/// <summary>
/// A message's text as a template rendered it, fitted to both limits.
/// </summary>
/// <param name="Caption">The HTML for a photo caption.</param>
/// <param name="Text">The HTML for a text message.</param>
/// <param name="TemplateKey">The key when an edited template produced the text; <c>null</c> for the built-in one.</param>
public sealed record RenderedText(string Caption, string Text, string? TemplateKey);
