using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot;
using Telegram.Bot.Types;

namespace Jellyfin.Plugin.TgJelly.Tests.Fakes;

public sealed class RecordingBotFlows : IBotFlows
{
    private readonly ConcurrentQueue<string> _calls = new();

    public Exception? Throw { get; set; }

    public int Ticks => _calls.Count(c => c == "tick");

    public string[] Calls => _calls.ToArray();

    /// <summary>Gets the texts of the group <c>/start@bot</c> messages handed over.</summary>
    public string[] GroupStarts => [.. _calls.Where(c => c.StartsWith("group-start:", StringComparison.Ordinal)).Select(c => c["group-start:".Length..])];

    public Task OnPrivateMessageAsync(Message message, CancellationToken ct) => Record("message:" + message.Text);

    public Task OnCallbackAsync(CallbackQuery query, CancellationToken ct) => Record("callback:" + query.Data);

    public Task OnChatMemberAsync(ChatMemberUpdated update, CancellationToken ct) => Record("member:" + update.Chat.Id);

    public Task OnGroupStartAsync(Message message, CancellationToken ct) => Record("group-start:" + message.Text);

    public Task TickAsync(CancellationToken ct) => Record("tick");

    private Task Record(string call)
    {
        _calls.Enqueue(call);
        return Throw is null ? Task.CompletedTask : Task.FromException(Throw);
    }
}
