namespace Jellyfin.Plugin.TgJelly.Templates;

/// <summary>
/// HTML that a template writes as-is instead of escaping it; produced by the template function <c>raw</c>.
/// </summary>
/// <param name="Html">The HTML.</param>
public sealed record RawHtml(string Html);
