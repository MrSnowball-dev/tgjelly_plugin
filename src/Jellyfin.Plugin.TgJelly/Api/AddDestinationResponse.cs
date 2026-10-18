namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// The result of adding a destination.
/// </summary>
public sealed class AddDestinationResponse
{
    /// <summary>Gets the outcome: Added or AlreadyExists.</summary>
    public string Outcome { get; init; } = string.Empty;

    /// <summary>Gets the destination.</summary>
    public DestinationDto Destination { get; init; } = new();

    /// <summary>Gets a value indicating whether the automatic test message was sent.</summary>
    public bool TestSent { get; init; }

    /// <summary>Gets why the test message wasn't sent, if it wasn't.</summary>
    public string? TestError { get; init; }
}
