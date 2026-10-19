using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot.Types;

namespace Jellyfin.Plugin.TgJelly.Bot.Screens;

/// <summary>
/// The bot's flow engine. This is the shell the router and worker are wired to; the screens arrive with the next task.
/// </summary>
public sealed class FlowEngine : IBotFlows
{
    /// <summary>
    /// Gets every callback action name the bot's screens use, for the callback-data length test.
    /// </summary>
    public static IReadOnlyList<string> Actions { get; } =
    [
        "m", "st", "dl", "noop", "d", "de", "det", "dlb", "dlt", "dsg", "dsgt", "dt", "dc", "dp", "dr", "drx", "tp",
        "s", "sm", "smt", "su", "sut", "sum", "sp", "spu", "spt", "spc", "op", "oc", "ocd", "oa", "ot", "ox",
        "tg", "tc", "te", "xl", "xs", "xd",
    ];

    /// <inheritdoc />
    public Task OnPrivateMessageAsync(Message message, CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public Task OnCallbackAsync(CallbackQuery query, CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public Task OnChatMemberAsync(ChatMemberUpdated update, CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public Task TickAsync(CancellationToken ct) => Task.CompletedTask;
}
