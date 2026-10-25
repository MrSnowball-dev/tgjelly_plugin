using System;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;
using Telegram.Bot.Types;

namespace Jellyfin.Plugin.TgJelly.Destinations;

/// <summary>
/// Learns forum topic names. The Bot API can't look a topic up, but a message sent into a topic comes back with the
/// topic's creation message as <c>reply_to_message</c>, and that carries the name.
/// </summary>
public static class TopicNames
{
    /// <summary>
    /// Remembers the name of the topic a sent message went to, when Telegram's reply names it and TgJelly didn't
    /// know it yet. Changes the settings only then.
    /// </summary>
    /// <param name="settings">The settings.</param>
    /// <param name="destinationId">The destination the message went to.</param>
    /// <param name="sent">The message Telegram returned.</param>
    public static void LearnFrom(SettingsStore settings, string destinationId, Message? sent)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (sent is not { MessageThreadId: > 0 and var thread } || sent.ReplyToMessage?.ForumTopicCreated?.Name is not { Length: > 0 } name)
        {
            return;
        }

        bool Known(Destination d) => d.KnownTopics.Any(t => t.ThreadId == thread && string.Equals(t.Name, name, StringComparison.Ordinal));
        if (settings.Current.Destinations.FirstOrDefault(d => d.Id == destinationId) is not { } current || Known(current))
        {
            return;
        }

        settings.UpdateIf(null, c =>
        {
            var d = c.Destinations.FirstOrDefault(x => x.Id == destinationId);
            if (d is null || Known(d))
            {
                return false;
            }

            d.KnownTopics = Array.Find(d.KnownTopics, t => t.ThreadId == thread) is { } known
                ? [.. d.KnownTopics.Select(t => t == known ? new KnownTopic { ThreadId = thread, Name = name } : t)]
                : [.. d.KnownTopics, new KnownTopic { ThreadId = thread, Name = name }];
            return true;
        });
    }
}
