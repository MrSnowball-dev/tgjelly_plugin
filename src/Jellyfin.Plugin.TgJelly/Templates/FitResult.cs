using System.Collections.Generic;

namespace Jellyfin.Plugin.TgJelly.Templates;

/// <summary>
/// The outcome of fitting a template: the HTML, whether it had to be cut, or the render error.
/// </summary>
/// <param name="Html">The fitted HTML; null on error.</param>
/// <param name="Truncated">True when the whole text had to be truncated.</param>
/// <param name="Error">The render error, if any.</param>
/// <param name="Model">The model variant <paramref name="Html"/> was rendered from, before any cut (the preview marks it).</param>
public sealed record FitResult(string? Html, bool Truncated, TemplateError? Error, IReadOnlyDictionary<string, object?>? Model = null);
