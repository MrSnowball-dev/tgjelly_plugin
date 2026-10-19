using Jellyfin.Plugin.TgJelly.State.Documents;

namespace Jellyfin.Plugin.TgJelly.Pipeline;

/// <summary>
/// A security or server message for the Outbox, before routing and rendering.
/// </summary>
/// <param name="Model">The model it is rendered from.</param>
/// <param name="Op">Send it, or edit an earlier message.</param>
/// <param name="SentKey">The key the sent message is recorded under, if it may be edited or replied to.</param>
/// <param name="TargetKey">For an edit, the message it edits.</param>
/// <param name="ReplyToKey">The message it replies to, if any.</param>
public sealed record AlertOutput(RenderModel Model, OutboxOp Op, string? SentKey, string? TargetKey, string? ReplyToKey);
