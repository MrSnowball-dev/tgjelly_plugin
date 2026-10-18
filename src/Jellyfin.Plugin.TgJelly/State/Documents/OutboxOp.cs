namespace Jellyfin.Plugin.TgJelly.State.Documents;

/// <summary>
/// What an Outbox entry does.
/// </summary>
public enum OutboxOp
{
    /// <summary>Send a new message.</summary>
    Send,

    /// <summary>Edit a sent message, found through <c>sentMessages</c>.</summary>
    Edit,
}
