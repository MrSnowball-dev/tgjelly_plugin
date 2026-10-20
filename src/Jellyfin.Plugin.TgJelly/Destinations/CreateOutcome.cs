namespace Jellyfin.Plugin.TgJelly.Destinations;

/// <summary>
/// What <see cref="DestinationCreator.CreateAsync"/> did.
/// </summary>
public enum CreateOutcome
{
    /// <summary>A destination was added, checked and sent the test message.</summary>
    Added,

    /// <summary>The chat and topic already had a destination; it was re-checked, not added.</summary>
    AlreadyExists,

    /// <summary>The expected revision was stale; nothing was saved.</summary>
    Conflict,

    /// <summary>The settings were rejected; nothing was saved.</summary>
    Invalid,
}
