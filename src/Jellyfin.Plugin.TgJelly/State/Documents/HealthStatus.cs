namespace Jellyfin.Plugin.TgJelly.State.Documents;

/// <summary>
/// A destination's health. Never changes <c>Destination.Enabled</c>.
/// </summary>
public enum HealthStatus
{
    /// <summary>Deliverable.</summary>
    Ok,

    /// <summary>The bot lacks the rights to post.</summary>
    NeedsPermissions,

    /// <summary>The forum topic is gone.</summary>
    TopicMissing,

    /// <summary>The bot was removed, blocked, or the chat is gone.</summary>
    Lost,
}
