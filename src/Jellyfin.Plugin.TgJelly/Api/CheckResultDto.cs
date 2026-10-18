namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// The result of <c>POST TgJelly/Destinations/{id}/Check</c>.
/// </summary>
public sealed class CheckResultDto
{
    /// <summary>Gets the health afterwards.</summary>
    public HealthDto Health { get; init; } = new();

    /// <summary>Gets why the check couldn't run, if it couldn't.</summary>
    public string? Error { get; init; }
}
