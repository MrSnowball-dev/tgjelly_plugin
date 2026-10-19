namespace Jellyfin.Plugin.TgJelly.Pipeline;

/// <summary>
/// What the aggregator hands to rendering.
/// </summary>
public enum MediaOutputKind
{
    /// <summary>One message for one group.</summary>
    Group,

    /// <summary>One digest for several groups.</summary>
    Digest,

    /// <summary>An edit of a group already sent or queued (a late arrival, spec §7.4 step 7).</summary>
    LateEdit,
}
