namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// A template problem with its position.
/// </summary>
public sealed class TemplateErrorDto
{
    /// <summary>Gets what is wrong.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Gets the 1-based line, when known.</summary>
    public int? Line { get; init; }

    /// <summary>Gets the 1-based column, when known.</summary>
    public int? Column { get; init; }
}
