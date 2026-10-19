using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Strings;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Jellyfin.Plugin.TgJelly.Bot.Screens;

/// <summary>
/// The bot's flow engine: routes private messages and button presses to screens. Runs on the <see cref="BotWorker"/>,
/// one update at a time. Later tasks add the destination, settings and onboarding screens.
/// </summary>
public sealed class FlowEngine : IBotFlows
{
    private readonly BotConnection _connection;
    private readonly BotMemory _memory;
    private readonly ScreenSender _screens;
    private readonly SettingsStore _settings;
    private readonly StringTable _strings;
    private readonly MenuScreens _menus;
    private readonly StatusScreen _status;
    private readonly ILogger<FlowEngine> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FlowEngine"/> class.
    /// </summary>
    /// <param name="connection">The bot connection.</param>
    /// <param name="memory">The bot's persisted memory.</param>
    /// <param name="screens">Shows screens.</param>
    /// <param name="settings">The settings store.</param>
    /// <param name="strings">The string table.</param>
    /// <param name="menus">The menu screens.</param>
    /// <param name="status">The status screen.</param>
    /// <param name="logger">The logger.</param>
    public FlowEngine(BotConnection connection, BotMemory memory, ScreenSender screens, SettingsStore settings, StringTable strings, MenuScreens menus, StatusScreen status, ILogger<FlowEngine> logger)
    {
        _connection = connection;
        _memory = memory;
        _screens = screens;
        _settings = settings;
        _strings = strings;
        _menus = menus;
        _status = status;
        _logger = logger;
    }

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
    public async Task OnPrivateMessageAsync(Message message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        var chatId = message.Chat.Id;
        _memory.TouchManager(chatId);
        var command = ParseCommand(message.Text);
        switch (command)
        {
            case "/menu" or "/test":
                await ShowAsync(chatId, MainMenu(), null, ct).ConfigureAwait(false);
                break;
            case "/status":
                await ShowAsync(chatId, _status.Build(), null, ct).ConfigureAwait(false);
                break;
            case "/help":
                await ShowAsync(chatId, new Screen(_strings.Get("help.text"), null), null, ct).ConfigureAwait(false);
                break;
            default:
                await ShowAsync(chatId, SetUpMenu(), null, ct).ConfigureAwait(false);
                break;
        }
    }

    /// <inheritdoc />
    public async Task OnCallbackAsync(CallbackQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Message is not { } message)
        {
            return;
        }

        var chatId = message.Chat.Id;
        _memory.TouchManager(chatId);
        if (!CallbackData.TryParse(query.Data, out var action, out var args))
        {
            await ExpiredAsync(chatId, ct).ConfigureAwait(false);
            return;
        }

        switch (action)
        {
            case "noop":
                break;
            case "m":
                await ShowAsync(chatId, MainMenu(), message.MessageId, ct).ConfigureAwait(false);
                break;
            case "st":
                await ShowAsync(chatId, _status.Build(), message.MessageId, ct).ConfigureAwait(false);
                break;
            case "dl":
                var page = args.Count > 0 && int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : 0;
                await ShowAsync(chatId, _menus.Destinations(_settings.Current.Destinations, page), message.MessageId, ct).ConfigureAwait(false);
                break;
            default:
                _logger.LogDebug("TgJelly has no screen for button action {Action}", action);
                await ExpiredAsync(chatId, ct).ConfigureAwait(false);
                break;
        }
    }

    /// <inheritdoc />
    public Task OnChatMemberAsync(ChatMemberUpdated update, CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public Task TickAsync(CancellationToken ct)
    {
        _memory.Prune();
        return Task.CompletedTask;
    }

    /// <summary>The command word of a message, without the <c>@bot</c> suffix and in lower case; empty for plain text.</summary>
    private static string ParseCommand(string? text)
    {
        if (string.IsNullOrEmpty(text) || text[0] != '/')
        {
            return string.Empty;
        }

        var word = text.Split([' ', '\n'], 2)[0];
        var at = word.IndexOf('@', StringComparison.Ordinal);
        return (at < 0 ? word : word[..at]).ToLowerInvariant();
    }

    private Task ShowAsync(long chatId, Screen screen, int? editMessageId, CancellationToken ct)
        => _screens.ShowAsync(chatId, screen, editMessageId, ct);

    private Screen MainMenu() => _menus.Main(_settings.Current.Destinations.Length, 0);

    private Screen SetUpMenu() => _menus.SetUp(_settings.Current.Destinations.Length, 0);

    private async Task ExpiredAsync(long chatId, CancellationToken ct)
    {
        await _connection.RequireSender().SendTextAsync(new OutgoingText(chatId, 0, _strings.Get("menu.expired"), ParseMode.Html, null, null, false), ct).ConfigureAwait(false);
        await ShowAsync(chatId, MainMenu(), null, ct).ConfigureAwait(false);
    }
}
