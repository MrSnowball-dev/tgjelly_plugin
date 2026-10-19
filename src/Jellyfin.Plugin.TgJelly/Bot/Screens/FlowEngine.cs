using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot.Flows;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Strings;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Jellyfin.Plugin.TgJelly.Bot.Screens;

/// <summary>
/// The bot's flow engine: routes private messages and button presses to screens. Runs on the <see cref="BotWorker"/>,
/// one update at a time.
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
    private readonly DestinationScreens _destinations;
    private readonly SettingsScreens _settingsScreens;
    private readonly OnboardingFlow _onboarding;
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
    /// <param name="destinations">The destination screens.</param>
    /// <param name="settingsScreens">The settings screens.</param>
    /// <param name="onboarding">The onboarding flow.</param>
    /// <param name="logger">The logger.</param>
    public FlowEngine(BotConnection connection, BotMemory memory, ScreenSender screens, SettingsStore settings, StringTable strings, MenuScreens menus, StatusScreen status, DestinationScreens destinations, SettingsScreens settingsScreens, OnboardingFlow onboarding, ILogger<FlowEngine> logger)
    {
        _connection = connection;
        _memory = memory;
        _screens = screens;
        _settings = settings;
        _strings = strings;
        _menus = menus;
        _status = status;
        _destinations = destinations;
        _settingsScreens = settingsScreens;
        _onboarding = onboarding;
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
        if (await OnPickerAnswerAsync(message, ct).ConfigureAwait(false))
        {
            return;
        }

        var command = ParseCommand(message.Text);
        if (command.Length > 0)
        {
            _settingsScreens.CancelInput(chatId);
        }
        else if (message.Text is { Length: > 0 } text && await _settingsScreens.OnTextAsync(chatId, text, ct).ConfigureAwait(false) is { Handled: true } typed)
        {
            if (typed.Screen is { } done)
            {
                await ShowAsync(chatId, done, null, ct).ConfigureAwait(false);
            }

            return;
        }

        switch (command)
        {
            case "/menu":
                await ShowAsync(chatId, MainMenu(), null, ct).ConfigureAwait(false);
                break;
            case "/test":
                await ShowAsync(chatId, _destinations.Picker(0), null, ct).ConfigureAwait(false);
                break;
            case "/status":
                await ShowAsync(chatId, _status.Build(), null, ct).ConfigureAwait(false);
                break;
            case "/help":
                await ShowAsync(chatId, new Screen(_strings.Get("help.text"), null), null, ct).ConfigureAwait(false);
                break;
            case "/start" when StartsOnboarding(message.Text):
                await _onboarding.StartAsync(chatId, ct).ConfigureAwait(false);
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

        if (action != "spt")
        {
            _settingsScreens.CancelInput(chatId);
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
            case "oa":
            case "ot":
                await _onboarding.StartAsync(chatId, ct).ConfigureAwait(false);
                break;
            case "ox":
                await ShowAsync(chatId, MainMenu(), message.MessageId, ct).ConfigureAwait(false);
                break;
            case "op":
                await _onboarding.OnPresetAsync(query, args.Count > 0 ? args[0] : string.Empty, ct).ConfigureAwait(false);
                break;
            case "oc":
                await _onboarding.OnCustomToggleAsync(query, args.Count > 0 ? args[0] : string.Empty, ct).ConfigureAwait(false);
                break;
            case "ocd":
                await _onboarding.OnCustomDoneAsync(query, ct).ConfigureAwait(false);
                break;
            default:
                var screen = await _destinations.HandleAsync(action, args, ct).ConfigureAwait(false)
                    ?? await _settingsScreens.HandleAsync(action, args, chatId, ct).ConfigureAwait(false);
                if (screen is not null)
                {
                    await ShowAsync(chatId, screen, message.MessageId, ct).ConfigureAwait(false);
                    break;
                }

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

    /// <summary>The prompt's answers (a picked chat, "💬 Right here", Cancel) go to onboarding before any other text.</summary>
    /// <returns>True when the message was one of them.</returns>
    private async Task<bool> OnPickerAnswerAsync(Message message, CancellationToken ct)
    {
        Func<Message, CancellationToken, Task>? handler = message switch
        {
            { ChatShared: not null } => _onboarding.OnChatSharedAsync,
            { Text: { } text } when text == _strings.Get("onboard.here") => _onboarding.OnRightHereAsync,
            { Text: { } text } when text == _strings.Get("onboard.cancel") => _onboarding.OnCancelAsync,
            _ => null,
        };
        if (handler is null)
        {
            return false;
        }

        _settingsScreens.CancelInput(message.Chat.Id);
        await handler(message, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Whether a <c>/start</c> begins onboarding (spec §6.2): the <c>setup</c> and <c>add</c> payloads, or no payload
    /// while there are no destinations yet.
    /// </summary>
    private bool StartsOnboarding(string? text)
    {
        var parts = (text ?? string.Empty).Split([' ', '\n'], 2, StringSplitOptions.RemoveEmptyEntries);
        var payload = parts.Length > 1 ? parts[1].Trim() : string.Empty;
        return payload is "setup" or "add" || (payload.Length == 0 && _settings.Current.Destinations.Length == 0);
    }

    private Task ShowAsync(long chatId, Screen screen, int? editMessageId, CancellationToken ct)
        => _screens.ShowAsync(chatId, screen, editMessageId, ct);

    private Screen MainMenu() => _menus.Main(_settings.Current.Destinations.Length, 0);

    /// <summary>The "set up: N" menu; with no destinations there is nothing set up yet, so the plain menu.</summary>
    private Screen SetUpMenu()
        => _settings.Current.Destinations.Length == 0 ? MainMenu() : _menus.SetUp(_settings.Current.Destinations.Length, 0);

    private async Task ExpiredAsync(long chatId, CancellationToken ct)
    {
        await _connection.RequireSender().SendTextAsync(new OutgoingText(chatId, 0, _strings.Get("menu.expired"), ParseMode.Html, null, null, false), ct).ConfigureAwait(false);
        await ShowAsync(chatId, MainMenu(), null, ct).ConfigureAwait(false);
    }
}
