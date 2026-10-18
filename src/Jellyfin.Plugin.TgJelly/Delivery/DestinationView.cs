using Jellyfin.Plugin.TgJelly.Configuration;

namespace Jellyfin.Plugin.TgJelly.Delivery;

/// <summary>
/// What the Outbox needs to know about a destination when picking the next entry.
/// </summary>
/// <param name="ChatId">The chat, for pacing.</param>
/// <param name="Kind">The chat kind; groups and channels have a per-minute limit.</param>
/// <param name="Deliverable">Whether its entries may go out now (enabled and healthy).</param>
public sealed record DestinationView(long ChatId, ChatKind Kind, bool Deliverable);
