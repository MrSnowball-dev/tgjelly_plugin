namespace Jellyfin.Plugin.TgJelly.Templates;

/// <summary>
/// A template problem with its position.
/// </summary>
/// <param name="Message">What is wrong.</param>
/// <param name="Line">The 1-based line, when known.</param>
/// <param name="Column">The 1-based column, when known.</param>
public sealed record TemplateError(string Message, int? Line, int? Column);
