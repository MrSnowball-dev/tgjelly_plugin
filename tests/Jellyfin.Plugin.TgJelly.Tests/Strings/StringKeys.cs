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
        "security.login_failed.heading", "security.login_failed.many", "security.ips.one", "security.ips.other", "security.locked", "security.lockout.one", "security.lockout.other", "security.login_succeeded.heading", "security.ip", "security.at", "security.user_created", "security.user_deleted", "security.password_changed", "server.started", "server.update_available", "server.plugin_installed", "server.plugin_updated", "server.plugin_uninstalled", "server.plugin_install_failed", "server.task_failed", "server.restart_pending", "server.reason.server.plugin_installed", "server.reason.server.plugin_updated", "server.reason.server.plugin_uninstalled", "server.reason.server.plugin_install_failed", "button.releaseNotes",
        "update.failing",
        "menu.title", "menu.setUp.one", "menu.setUp.other", "menu.destinations", "menu.add", "menu.settings", "menu.status", "menu.detected", "menu.expired", "menu.prev", "menu.next", "menu.back",
        "menu.destinations.title", "menu.destinations.empty", "help.text",
        "status.title", "status.connection", "status.compat", "status.compat.ok", "status.destinations", "status.noDestinations", "status.health.Ok", "status.health.NeedsPermissions", "status.health.TopicMissing", "status.health.Lost", "status.pending", "status.outbox", "status.errors", "status.noErrors",
        "dest.gone", "dest.health.Ok", "dest.health.PhotosBlocked", "dest.health.NeedsPermissions", "dest.health.TopicMissing", "dest.health.Lost", "dest.kind.Private", "dest.kind.Group", "dest.kind.Supergroup", "dest.kind.Channel", "dest.preset.Everything", "dest.preset.NewMedia", "dest.preset.AdminAlerts", "dest.preset.Custom", "dest.line.kind", "dest.line.status", "dest.line.preset", "dest.line.groups", "dest.line.enabled", "dest.enabled.yes", "dest.enabled.no", "dest.btn.events", "dest.btn.libraries", "dest.btn.silent", "dest.btn.test", "dest.btn.check", "dest.btn.pause", "dest.btn.resume", "dest.btn.remove", "dest.btn.removeLeave", "dest.btn.cancel", "dest.events.title", "dest.libraries.title", "dest.libraries.hint", "dest.libraries.all", "dest.libraries.removed", "dest.silent.title", "dest.silent.hint", "dest.test.ok", "dest.test.failed", "dest.remove.ask", "dest.picker.title", "dest.event.media.movie", "dest.event.media.episodes", "dest.event.media.album", "dest.event.media.musicvideo", "dest.event.media.book", "dest.event.security.login_failed", "dest.event.security.lockout", "dest.event.security.login_succeeded", "dest.event.security.user_created", "dest.event.security.user_deleted", "dest.event.security.password_changed", "dest.event.server.started", "dest.event.server.update_available", "dest.event.server.plugin_installed", "dest.event.server.plugin_updated", "dest.event.server.plugin_uninstalled", "dest.event.server.plugin_install_failed", "dest.event.server.task_failed", "dest.event.server.restart_pending",
        "settings.title", "settings.messages", "settings.updates", "settings.url", "settings.messages.title", "settings.messages.hint", "settings.msg.ShowPoster", "settings.msg.ShowOverview", "settings.msg.ShowRatingGenres", "settings.msg.ShowOpenInJellyfin", "settings.msg.ShowProviderLinks", "settings.msg.AnnounceUpgrades", "settings.updates.title", "settings.updates.hint", "settings.updates.enabled", "settings.updates.mode.SameMajor", "settings.updates.mode.AnyNewer", "settings.url.title", "settings.url.hint", "settings.url.current", "settings.url.none", "settings.url.invalid", "settings.url.suggested", "settings.url.use", "settings.url.type", "settings.url.clear", "settings.url.prompt", "settings.url.rule", "settings.url.placeholder",
        "onboard.prompt", "onboard.channel", "onboard.group", "onboard.here", "onboard.cancel", "onboard.checking", "onboard.preset", "onboard.preset.everything", "onboard.preset.newMedia", "onboard.preset.adminAlerts", "onboard.preset.custom", "onboard.customDone", "onboard.done", "onboard.addAnother", "onboard.menu", "onboard.tryAgain", "onboard.advanced", "onboard.expired", "onboard.hereChosen", "onboard.cancelled",
    ];
}
