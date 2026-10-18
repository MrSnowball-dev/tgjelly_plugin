using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot;
using Telegram.Bot.Types;

namespace Jellyfin.Plugin.TgJelly.Tests.Fakes;

public sealed class RecordingUpdateHandler : IUpdateHandler
{
    private readonly ConcurrentQueue<int> _ids = new();

    public Func<Update, bool> ThrowFor { get; set; } = _ => false;

    public int[] Ids => _ids.ToArray();

    public Task HandleAsync(Update update, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);
        _ids.Enqueue(update.Id);
        if (ThrowFor(update))
        {
            throw new InvalidOperationException("handler failed");
        }

        return Task.CompletedTask;
    }

    public bool Saw(int id) => _ids.Contains(id);
}
