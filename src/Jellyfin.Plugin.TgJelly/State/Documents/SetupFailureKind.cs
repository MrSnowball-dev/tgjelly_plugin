namespace Jellyfin.Plugin.TgJelly.State.Documents;

/// <summary>
/// What went wrong during a setup step in the bot.
/// </summary>
public enum SetupFailureKind
{
    /// <summary>A destination check failed.</summary>
    CheckFailed,

    /// <summary>A prompt waited too long for an answer.</summary>
    PromptExpired,

    /// <summary>Creating or using a forum topic failed.</summary>
    TopicFailed,

    /// <summary>A topic could not be picked.</summary>
    TopicNotPickable,
}
