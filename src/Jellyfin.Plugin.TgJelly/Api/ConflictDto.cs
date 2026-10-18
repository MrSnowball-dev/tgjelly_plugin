namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// A 409 response: the caller's revision was stale; here is the current data (spec §10.4).
/// </summary>
/// <typeparam name="T">The resource type.</typeparam>
public sealed class ConflictDto<T>
{
    /// <summary>Gets the current data.</summary>
    public required T Current { get; init; }
}
