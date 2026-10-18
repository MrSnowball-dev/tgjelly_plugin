using System;

namespace Jellyfin.Plugin.TgJelly.Bot;

/// <summary>
/// A classified polling error.
/// </summary>
/// <param name="Kind">The kind.</param>
/// <param name="Description">Telegram's description or the exception message.</param>
/// <param name="RetryAfter">How long to wait, for <see cref="PollErrorKind.RateLimited"/>.</param>
public sealed record PollError(PollErrorKind Kind, string Description, TimeSpan? RetryAfter = null);
