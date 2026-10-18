using System;

namespace Jellyfin.Plugin.TgJelly.Bot;

/// <summary>
/// A classified delivery error.
/// </summary>
/// <param name="Kind">The §9.4 row.</param>
/// <param name="Description">Telegram's description or the exception message.</param>
/// <param name="RetryAfter">How long to wait, for <see cref="DeliveryErrorKind.RateLimited"/>.</param>
/// <param name="MigrateToChatId">The supergroup's ID, for <see cref="DeliveryErrorKind.Migrated"/>.</param>
public sealed record DeliveryError(DeliveryErrorKind Kind, string Description, TimeSpan? RetryAfter = null, long? MigrateToChatId = null);
