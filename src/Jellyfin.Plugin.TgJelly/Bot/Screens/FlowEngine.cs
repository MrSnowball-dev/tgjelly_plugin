using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot.Flows;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.Strings;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Jellyfin.Plugin.TgJelly.Bot.Screens;

/// <summary>
/// The bot's flow engine: routes private messages and button presses to screens. Runs on the <see cref="BotWorker"/>,
/// one update at a time.
/// </summary>
public sealed class FlowEngine : IBotFlows, IDisposable
{
    private readonly BotConnection _connection;
    private readonly BotMemory _memory;
    private readonly ScreenSender _screens;
    private readonly SettingsStore _settings;
    private readonly StringTable _strings;
    private readonly MenuScreens _menus;
    private readonly StatusScreen _status;
    private readonly AdvancedScreen _advanced;
    private readonly DestinationScreens _destinations;
    private readonly SettingsScreens _settingsScreens;
    private readonly OnboardingFlow _onboarding;
    private readonly DetectedChatFlow _detected;
    private readonly HealthService _health;
    private readonly ManagerNotifier _notifier;
    private readonly BotWorkQueue _queue;
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
    /// <param name="advanced">The Advanced options screen.</param>
    /// <param name="destinations">The destination screens.</param>
    /// <param name="settingsScreens">The settings screens.</param>
    /// <param name="onboarding">The onboarding flow.</param>
    /// <param name="detected">The detected chats flow.</param>
    /// <param name="health">The destination health; its new problems are told to the managers.</param>
    /// <param name="notifier">Tells the managers about destination problems.</param>
    /// <param name="queue">The bot work queue the notifications run on.</param>
    /// <param name="logger">The logger.</param>
    public FlowEngine(BotConnection connection, BotMemory memory, ScreenSender screens, SettingsStore settings, StringTable strings, MenuScreens menus, StatusScreen status, AdvancedScreen advanced, DestinationScreens destinations, SettingsScreens settingsScreens, OnboardingFlow onboarding, DetectedChatFlow detected, HealthService health, ManagerNotifier notifier, BotWorkQueue queue, ILogger<FlowEngine> logger)
    {
        _connection = connection;
        _memory = memory;
        _screens = screens;
        _settings = settings;
        _strings = strings;
        _menus = menus;
        _status = status;
        _advanced = advanced;
        _destinations = destinations;
        _settingsScreens = settingsScreens;
        _onboarding = onboarding;
        _detected = detected;
        _health = health;
        _notifier = notifier;
        _queue = queue;
        _logger = logger;
        _health.ProblemFound += OnProblemFound;
    }

    /// <summary>
    /// Gets every callback action name the bot's screens use, for the callback-data length test.
    /// </summary>
    public static IReadOnlyList<string> Actions { get; } =
    [
        "m", "st", "dl", "noop", "d", "de", "deg", "det", "dlb", "dlt", "dsg", "dsgt", "dt", "dc", "dp", "dr", "drx", "tp",
        "s", "sm", "smt", "su", "sut", "sum", "sp", "spu", "spt", "spc", "op", "oc", "ocg", "ocd", "oa", "ot", "oh", "on", "ox", "ol", "od",
        "tg", "tc", "te", "tb", "tk", "xl", "xs", "xd",
    ];

    /// <inheritdoc />
    public async Task OnPrivateMessageAsync(Message message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        var chatId = message.Chat.Id;
        _memory.TouchManager(chatId);
        if (await OnLegacyReplyAsync(message, ct).ConfigureAwait(false))
        {
            return;
        }

        var command = ParseCommand(message.Text);
        if (command.Length > 0)
        {
            _settingsScreens.CancelInput(chatId);
        }
        else if (message.Text is { Length: > 0 } text)
        {
            if (await _settingsScreens.OnTextAsync(chatId, text, ct).ConfigureAwait(false) is { Handled: true } typed)
            {
                if (typed.Screen is { } done)
                {
                    await ShowAsync(chatId, done, null, ct).ConfigureAwait(false);
                }

                return;
            }

            if (await _onboarding.OnTextAsync(chatId, text, ct).ConfigureAwait(false))
            {
                return;
            }
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
                await _onboarding.StartAsync(chatId, null, ct).ConfigureAwait(false);
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
                await _onboarding.StartAsync(chatId, message.MessageId, ct).ConfigureAwait(false);
                break;
            case "ot":
                await _onboarding.OnTryAgainAsync(query, ct).ConfigureAwait(false);
                break;
            case "oh":
                await _onboarding.OnRightHereAsync(query, ct).ConfigureAwait(false);
                break;
            case "on":
                await _onboarding.OnCancelAsync(query, ct).ConfigureAwait(false);
                break;
            case "ox":
                await ShowAsync(chatId, _advanced.Options(), message.MessageId, ct).ConfigureAwait(false);
                break;
            case "od":
                await ShowAsync(chatId, _advanced.Dashboard(), message.MessageId, ct).ConfigureAwait(false);
                break;
            case "ol":
                await _onboarding.OnAdvancedTopicAsync(query, ct).ConfigureAwait(false);
                break;
            case "op":
                await _onboarding.OnPresetAsync(query, args.Count > 0 ? args[0] : string.Empty, ct).ConfigureAwait(false);
                break;
            case "oc":
                await _onboarding.OnCustomToggleAsync(query, args.Count > 0 ? args[0] : string.Empty, ct).ConfigureAwait(false);
                break;
            case "ocg":
                await _onboarding.OnCustomGroupAsync(query, args.Count > 0 ? args[0] : null, ct).ConfigureAwait(false);
                break;
            case "tg":
            case "tc":
            case "te":
            case "tb":
            case "tk":
                await _onboarding.OnTopicAsync(query, action, ct).ConfigureAwait(false);
                break;
            case "ocd":
                await _onboarding.OnCustomDoneAsync(query, ct).ConfigureAwait(false);
                break;
            case "xl":
                await ShowAsync(chatId, _detected.List(PageOf(args)), message.MessageId, ct).ConfigureAwait(false);
                break;
            case "xd" when TryChatId(args, out var dismissed):
                await ShowAsync(chatId, _detected.Dismiss(dismissed), message.MessageId, ct).ConfigureAwait(false);
                break;
            case "xs" when TryChatId(args, out var detected):
                if (!await _detected.SetUpAsync(chatId, detected, message.MessageId, ct).ConfigureAwait(false))
                {
                    await ExpiredAsync(chatId, ct).ConfigureAwait(false);
                }

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
    public async Task OnChatMemberAsync(ChatMemberUpdated update, CancellationToken ct)
    {
        if (!await _onboarding.OnChatMemberAsync(update, ct).ConfigureAwait(false))
        {
            _detected.OnChatMember(update);
        }
    }

    /// <inheritdoc />
    public Task OnGroupStartAsync(Message message, CancellationToken ct) => _onboarding.OnGroupStartAsync(message, ct);

    /// <inheritdoc />
    public async Task TickAsync(CancellationToken ct)
    {
        foreach (var chatId in _memory.Prune())
        {
            await _onboarding.ExpireAsync(chatId, ct).ConfigureAwait(false);
        }

        await _notifier.NotifyAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose() => _health.ProblemFound -= OnProblemFound;

    /// <summary>Queues the managers' notification; runs on whichever thread found the problem, so it only enqueues.</summary>
    private void OnProblemFound(object? sender, string destinationId)
        => _queue.Enqueue("notify managers", _notifier.NotifyAsync);

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

    /// <summary>
    /// Stale input from a v0.1 client goes to onboarding before any other text: a chat picked in its native picker
    /// (<c>chat_shared</c>) and the texts of its reply keyboard. The bot sends no reply keyboards any more.
    /// </summary>
    /// <returns>True when the message was one of them.</returns>
    private async Task<bool> OnLegacyReplyAsync(Message message, CancellationToken ct)
    {
        Func<Message, CancellationToken, Task>? handler = message switch
        {
            { ChatShared: not null } => _onboarding.OnLegacyReplyAsync,
            { Text: { } text } when OnboardingFlow.LegacyReplyTexts.Contains(text, StringComparer.Ordinal) => _onboarding.OnLegacyReplyAsync,
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

    private static int PageOf(IReadOnlyList<string> args)
        => args.Count > 0 && int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var page) ? page : 0;

    private static bool TryChatId(IReadOnlyList<string> args, out long chatId)
    {
        chatId = 0;
        return args.Count > 0 && long.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out chatId);
    }

    private Screen MainMenu() => _menus.Main(_settings.Current.Destinations.Length, _memory.VisibleDetected(_settings.Current).Count);

    /// <summary>The "set up: N" menu; with no destinations there is nothing set up yet, so the plain menu.</summary>
    private Screen SetUpMenu()
        => _settings.Current.Destinations.Length == 0 ? MainMenu() : _menus.SetUp(_settings.Current.Destinations.Length, _memory.VisibleDetected(_settings.Current).Count);

    private async Task ExpiredAsync(long chatId, CancellationToken ct)
    {
        await _connection.RequireSender().SendTextAsync(new OutgoingText(chatId, 0, _strings.Get("menu.expired"), ParseMode.Html, null, null, false), ct).ConfigureAwait(false);
        await ShowAsync(chatId, MainMenu(), null, ct).ConfigureAwait(false);
    }
}
