using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.State.Documents;

namespace Jellyfin.Plugin.TgJelly.Destinations;

/// <summary>
/// The outcome of checking whether the bot can post in a chat.
/// </summary>
/// <param name="Passed">Whether the bot can post there.</param>
/// <param name="Problem">The health problem when not passed.</param>
/// <param name="Message">What's wrong, for people, when not passed.</param>
/// <param name="ChatId">The numeric chat ID (resolved from an <c>@username</c>), or 0 when unknown.</param>
/// <param name="Title">The chat's title or the user's name.</param>
/// <param name="Kind">The chat kind.</param>
/// <param name="IsForum">Whether the chat is a forum.</param>
/// <param name="PhotosBlocked">Whether the bot can't send photos there.</param>
public sealed record ChatCheckResult(bool Passed, HealthStatus Problem, string? Message, long ChatId, string Title, ChatKind Kind, bool IsForum, bool PhotosBlocked);
