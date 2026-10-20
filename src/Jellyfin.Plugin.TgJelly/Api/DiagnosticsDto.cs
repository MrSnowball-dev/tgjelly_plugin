namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// The redacted diagnostics report an admin pastes into a GitHub issue.
/// </summary>
public sealed class DiagnosticsDto
{
    /// <summary>Gets the report text.</summary>
    public string Text { get; init; } = string.Empty;
}
