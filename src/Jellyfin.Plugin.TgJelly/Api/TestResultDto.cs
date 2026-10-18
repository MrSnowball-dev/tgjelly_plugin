namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// The result of <c>POST TgJelly/Destinations/{id}/Test</c>.
/// </summary>
public sealed class TestResultDto
{
    /// <summary>Gets a value indicating whether the test message was sent.</summary>
    public bool Sent { get; init; }

    /// <summary>Gets why not, if it wasn't.</summary>
    public string? Error { get; init; }

    /// <summary>Gets the health afterwards.</summary>
    public HealthDto Health { get; init; } = new();
}
