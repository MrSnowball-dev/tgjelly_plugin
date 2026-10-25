using System;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;
using Telegram.Bot.Types;

namespace Jellyfin.Plugin.TgJelly.Destinations;

/// <summary>
/// Learns forum topic names. The Bot API can't look a topic up, but the forum tells the bot when a topic is created or
/// renamed, and a message sent into a topic comes back with the topic's creation message as <c>reply_to_message</c>.
/// </summary>
public static class TopicNames
{
    /// <summary>
    /// Remembers the name of the topic a sent message went to, when TgJelly doesn't know it yet. The reply carries the
    /// name the topic was created with, so a name TgJelly already knows (perhaps from a rename) is kept.
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

        bool Unnamed(Destination d) => d.Id == destinationId && !d.KnownTopics.Any(t => t.ThreadId == thread && t.Name.Length > 0);
        Remember(settings, Unnamed, thread, name);
    }

    /// <summary>
    /// Follows a topic created or renamed in a forum: every destination in that chat knows the topic by its new name,
    /// in the dashboard and in the bot alike.
    /// </summary>
    /// <param name="settings">The settings.</param>
    /// <param name="message">A message from a supergroup; any message but a topic created or renamed is ignored.</param>
    public static void FollowRename(SettingsStore settings, Message message)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(message);
        if ((message.ForumTopicCreated?.Name ?? message.ForumTopicEdited?.Name) is not { Length: > 0 } name)
        {
            return;
        }

        // The General topic has no thread ID, and TgJelly calls it thread 0.
        var thread = message.IsTopicMessage ? message.MessageThreadId ?? 0 : 0;
        var chatId = message.Chat.Id;
        bool Stale(Destination d) => d.ChatId == chatId && !d.KnownTopics.Any(t => t.ThreadId == thread && string.Equals(t.Name, name, StringComparison.Ordinal));
        Remember(settings, Stale, thread, name);
    }

    private static void Remember(SettingsStore settings, Func<Destination, bool> needs, int thread, string name)
    {
        if (!settings.Current.Destinations.Any(needs))
        {
            return;
        }

        settings.UpdateIf(null, c =>
        {
            var changed = false;
            foreach (var d in c.Destinations.Where(needs))
            {
                d.KnownTopics = Array.Exists(d.KnownTopics, t => t.ThreadId == thread)
                    ? [.. d.KnownTopics.Select(t => t.ThreadId == thread ? new KnownTopic { ThreadId = thread, Name = name } : t)]
                    : [.. d.KnownTopics, new KnownTopic { ThreadId = thread, Name = name }];
                changed = true;
            }

            return changed;
        });
    }
}
