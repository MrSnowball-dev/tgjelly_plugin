namespace Jellyfin.Plugin.TgJelly.Tests.Strings;

/// <summary>
/// Every string key the plugin uses; later tasks append theirs. <see cref="StringTableTests"/> checks each exists.
/// </summary>
public static class StringKeys
{
    public static readonly string[] All =
    [
        "connection.NoToken", "connection.WaitingForStartup", "connection.Incompatible", "connection.WaitingForInstance",
        "connection.Connecting", "connection.Connected", "connection.TokenRejected", "connection.WebhookConflict",
        "connection.ConflictingInstance", "connection.Unreachable", "connection.InvalidBaseUrl",
        "token.format", "token.rejected", "token.unreachable", "token.failed", "token.switch.one", "token.switch.other",
        "token.webhookDeleted", "health.botChanged",
        "check.notFound", "check.channelNotAdmin", "check.groupCantSend", "check.topicMissing", "check.lost", "check.photosBlocked",
        "destination.duplicate", "destination.notConnected", "destination.badLink",
        "test.message", "groups.media", "groups.security", "groups.server", "groups.none", "groups.separator",
        "outbox.summary.one", "outbox.summary.other",
        "profile.description", "profile.shortDescription",
        "command.menu", "command.status", "command.test", "command.help",
        "destination.notForum",
        "time.utc", "delivery.delayed", "media.runtime.hours", "media.runtime.minutes", "media.more",
        "media.heading", "media.headingYear", "media.rating", "media.library", "media.upgraded", "media.newSeries", "media.season", "media.specials", "media.episodes.heading", "media.episodes.count.one", "media.episodes.count.other", "media.episode", "media.episodeNoNumber", "media.album.heading", "media.album.headingYear", "media.album.tracks.one", "media.album.tracks.other", "media.audiobook.parts.one", "media.audiobook.parts.other", "digest.heading.one", "digest.heading.other", "digest.library", "digest.noLibrary", "digest.titleYear", "digest.season", "digest.episodes.one", "digest.episodes.other", "digest.episode", "digest.album", "digest.more.one", "digest.more.other", "button.open", "button.imdb", "button.tmdb",
    ];
}
