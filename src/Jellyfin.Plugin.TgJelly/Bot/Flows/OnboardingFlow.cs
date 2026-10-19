using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Bot.Screens;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Destinations;
using Jellyfin.Plugin.TgJelly.Rendering;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Jellyfin.Plugin.TgJelly.Strings;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace Jellyfin.Plugin.TgJelly.Bot.Flows;

/// <summary>
/// The bot's onboarding (spec §6.2): a reply-keyboard prompt with Telegram's native chat pickers, the check of the
/// picked chat, the preset wizard and the Done message. Each step keys off its own marker in the chat's flow (the live
/// picker request IDs, the wizard message ID), never off <see cref="FlowState.Screen"/> alone, because the settings
/// screens keep their typed-URL input in the same flow.
/// </summary>
public sealed class OnboardingFlow
{
    private const string FlowScreen = "onboard";
    private const string RequestChannelKey = "requestChannel";
    private const string RequestGroupKey = "requestGroup";
    private const string AnswerKey = "answer";
    private const string ChatIdKey = "chatId";
    private const string TitleKey = "title";
    private const string KindKey = "kind";
    private const string IsForumKey = "isForum";
    private const string PhotosBlockedKey = "photosBlocked";
    private const string ThreadIdKey = "threadId";
    private const string EventsKey = "events";
    private const string CreatingKey = "creating";

    private readonly BotConnection _connection;
    private readonly BotMemory _memory;
    private readonly ScreenSender _screens;
    private readonly SettingsStore _settings;
    private readonly ChatCheckRetrier _retrier;
    private readonly DestinationChecker _checker;
    private readonly DestinationCreator _creator;
    private readonly HealthService _health;
    private readonly DestinationScreens _destinations;
    private readonly MenuScreens _menus;
    private readonly StringTable _strings;
    private readonly Formatter _formatter;
    private readonly TimeProvider _time;
    private readonly ILogger<OnboardingFlow> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OnboardingFlow"/> class.
    /// </summary>
    /// <param name="connection">The bot connection.</param>
    /// <param name="memory">The bot's memory, for the chat's flow.</param>
    /// <param name="screens">Shows screens.</param>
    /// <param name="settings">The settings store.</param>
    /// <param name="retrier">Checks a picked chat, trying again while the bot's membership settles.</param>
    /// <param name="checker">Re-checks the chat, with its topic, right before creating.</param>
    /// <param name="creator">Adds the destination.</param>
    /// <param name="health">The health service, to re-check an existing destination.</param>
    /// <param name="destinations">The destination screens, to open an existing destination.</param>
    /// <param name="menus">The menu screens.</param>
    /// <param name="strings">The string table.</param>
    /// <param name="formatter">The formatter, for escaping.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public OnboardingFlow(BotConnection connection, BotMemory memory, ScreenSender screens, SettingsStore settings, ChatCheckRetrier retrier, DestinationChecker checker, DestinationCreator creator, HealthService health, DestinationScreens destinations, MenuScreens menus, StringTable strings, Formatter formatter, TimeProvider time, ILogger<OnboardingFlow> logger)
    {
        _connection = connection;
        _memory = memory;
        _screens = screens;
        _settings = settings;
        _retrier = retrier;
        _checker = checker;
        _creator = creator;
        _health = health;
        _destinations = destinations;
        _menus = menus;
        _strings = strings;
        _formatter = formatter;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Starts over: replaces the chat's flow and sends the prompt with the pickers (spec §6.2 step 1).
    /// </summary>
    /// <param name="chatId">The private chat.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>A task.</returns>
    public async Task StartAsync(long chatId, CancellationToken ct)
    {
        var sender = _connection.RequireSender();
        var previous = _memory.GetFlow(chatId);
        _memory.ClearFlow(chatId);
        await DeleteAsync(chatId, previous?.PromptMessageId, ct).ConfigureAwait(false);

        var channel = _memory.NextRequestId();
        var group = _memory.NextRequestId();
        var keyboard = new ReplyKeyboardMarkup(
        [
            [PickerButton(PickerKind.Channel, channel, _strings.Get("onboard.channel"))],
            [PickerButton(PickerKind.Group, group, _strings.Get("onboard.group"))],
            [new KeyboardButton(_strings.Get("onboard.here"))],
            [new KeyboardButton(_strings.Get("onboard.cancel"))],
        ])
        {
            ResizeKeyboard = true,
        };
        var prompt = await sender.SendPromptAsync(new OutgoingPrompt(chatId, _formatter.Escape(_strings.Get("onboard.prompt")), keyboard), ct).ConfigureAwait(false);

        var flow = new FlowState
        {
            Screen = FlowScreen,
            PromptMessageId = prompt.MessageId,
            PromptSentAt = _time.GetUtcNow(),
            PickerRequestIds = [channel, group],
        };
        flow.Params[RequestChannelKey] = Text(channel);
        flow.Params[RequestGroupKey] = Text(group);
        _memory.SaveFlow(chatId, flow);
    }

    /// <summary>
    /// Handles a chat picked with one of the prompt's pickers: the "Checking…" message, the prompt's removal and the
    /// check with retries (spec §6.2 step 2). A picker the chat's flow doesn't know has expired.
    /// </summary>
    /// <param name="message">The private message carrying <see cref="Message.ChatShared"/>.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>A task.</returns>
    public async Task OnChatSharedAsync(Message message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.ChatShared is not { } shared)
        {
            return;
        }

        var chatId = message.Chat.Id;
        var flow = _memory.GetFlow(chatId);
        if (IsRepeat(flow, message))
        {
            _logger.LogDebug("TgJelly ignored a chat_shared it already handled");
            return;
        }

        if (flow is null || !flow.PickerRequestIds.Contains(shared.RequestId))
        {
            await PickerExpiredAsync(chatId, ct).ConfigureAwait(false);
            return;
        }

        var title = string.IsNullOrEmpty(shared.Title) ? Text(shared.ChatId) : shared.Title;
        await BeginCheckAsync(chatId, flow, message, _strings.Get("onboard.checking", ("title", _formatter.Escape(title))), ct).ConfigureAwait(false);

        ChatCheckResult check;
        try
        {
            check = await _retrier.CheckAsync(shared.ChatId, retry: true, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TgJelly could not check the picked chat {Title}", title);
            await FailAsync(chatId, _strings.Get("token.unreachable"), null, ct).ConfigureAwait(false);
            return;
        }

        await ContinueAfterCheckAsync(chatId, check, 0, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles "💬 Right here": this private chat becomes the destination, checked once (spec §6.2 step 1). Without a
    /// live prompt the picker has expired.
    /// </summary>
    /// <param name="message">The private message.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>A task.</returns>
    public async Task OnRightHereAsync(Message message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        var chatId = message.Chat.Id;
        var flow = _memory.GetFlow(chatId);
        if (IsRepeat(flow, message))
        {
            return;
        }

        if (flow is null || flow.PickerRequestIds.Count == 0)
        {
            await PickerExpiredAsync(chatId, ct).ConfigureAwait(false);
            return;
        }

        await BeginCheckAsync(chatId, flow, message, _formatter.Escape(_strings.Get("onboard.hereChosen")), ct).ConfigureAwait(false);

        ChatCheckResult check;
        try
        {
            check = await _retrier.CheckAsync(chatId, retry: false, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TgJelly could not check the private chat");
            await FailAsync(chatId, _strings.Get("token.unreachable"), null, ct).ConfigureAwait(false);
            return;
        }

        await ContinueAfterCheckAsync(chatId, check, 0, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles the prompt's Cancel: removes the reply keyboard, ends a live prompt and shows the menu.
    /// </summary>
    /// <param name="message">The private message.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>A task.</returns>
    public async Task OnCancelAsync(Message message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        var chatId = message.Chat.Id;
        var sender = _connection.RequireSender();
        await sender.SendPromptAsync(new OutgoingPrompt(chatId, _formatter.Escape(_strings.Get("onboard.cancelled")), new ReplyKeyboardRemove()), ct).ConfigureAwait(false);
        if (_memory.GetFlow(chatId) is { PickerRequestIds.Count: > 0 } flow)
        {
            _memory.ClearFlow(chatId);
            await DeleteAsync(chatId, flow.PromptMessageId, ct).ConfigureAwait(false);
        }

        await _screens.ShowAsync(chatId, MainMenu(), null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles a preset button on the wizard (<c>op</c>): Custom opens the event switches with nothing selected; any
    /// other preset creates the destination (spec §6.2 steps 4–5).
    /// </summary>
    /// <param name="query">The button press.</param>
    /// <param name="preset">The <see cref="PresetKind"/> name.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>A task.</returns>
    public async Task OnPresetAsync(CallbackQuery query, string preset, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (Wizard(query) is not { } flow || !Enum.TryParse<PresetKind>(preset, out var kind) || !Enum.IsDefined(kind))
        {
            await MenuExpiredAsync(query, ct).ConfigureAwait(false);
            return;
        }

        var chatId = query.Message!.Chat.Id;
        if (kind == PresetKind.Custom)
        {
            flow.Params[EventsKey] = string.Empty;
            await ShowCustomAsync(chatId, flow, ct).ConfigureAwait(false);
            return;
        }

        await CreateAsync(chatId, flow, kind, EventCatalog.Expand(kind), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles an event switch on the Custom wizard (<c>oc</c>).
    /// </summary>
    /// <param name="query">The button press.</param>
    /// <param name="key">The event key.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>A task.</returns>
    public async Task OnCustomToggleAsync(CallbackQuery query, string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (Wizard(query) is not { } flow || !flow.Params.TryGetValue(EventsKey, out var csv))
        {
            await MenuExpiredAsync(query, ct).ConfigureAwait(false);
            return;
        }

        if (EventCatalog.IsKnown(key))
        {
            var selected = Split(csv);
            var on = selected.Contains(key, StringComparer.Ordinal);
            flow.Params[EventsKey] = string.Join(',', EventCatalog.AllKeys.Where(k => k == key ? !on : selected.Contains(k, StringComparer.Ordinal)));
        }

        await ShowCustomAsync(query.Message!.Chat.Id, flow, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles Done on the Custom wizard (<c>ocd</c>): creates the destination with the chosen events.
    /// </summary>
    /// <param name="query">The button press.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>A task.</returns>
    public async Task OnCustomDoneAsync(CallbackQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (Wizard(query) is not { } flow || !flow.Params.TryGetValue(EventsKey, out var csv))
        {
            await MenuExpiredAsync(query, ct).ConfigureAwait(false);
            return;
        }

        await CreateAsync(query.Message!.Chat.Id, flow, PresetKind.Custom, Split(csv), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Continues after a chat was checked: a failure is explained with <c>[Try again] [Advanced options]</c>; a forum
    /// whose topic isn't chosen yet goes to the topic step; anything else meets the duplicate rule, then the preset
    /// step (spec §6.2 steps 2–4). The "Checking…" message, if the flow has one, is deleted.
    /// </summary>
    /// <param name="privateChatId">The private chat.</param>
    /// <param name="check">The check of the picked chat.</param>
    /// <param name="threadId">The forum topic, or 0.</param>
    /// <param name="wizardMessageId">The wizard message to edit, or <c>null</c> to send a new one.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>A task.</returns>
    public async Task ContinueAfterCheckAsync(long privateChatId, ChatCheckResult check, int threadId, int? wizardMessageId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (!check.Passed)
        {
            await FailAsync(privateChatId, check.Message ?? _strings.Get("check.groupCantSend"), wizardMessageId, ct).ConfigureAwait(false);
            return;
        }

        if (check.IsForum && threadId == 0)
        {
            await ContinueForForumAsync(privateChatId, check, wizardMessageId, ct).ConfigureAwait(false);
            return;
        }

        await ContinueWithChatAsync(privateChatId, check, threadId, wizardMessageId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds a native chat picker button (spec §6.2): a channel where the bot can post and edit, a group the bot
    /// joins, or a forum where the bot is promoted to manage topics.
    /// </summary>
    /// <param name="kind">The picker.</param>
    /// <param name="requestId">Its fresh request ID.</param>
    /// <param name="text">The button text.</param>
    /// <returns>The button.</returns>
    internal static KeyboardButton PickerButton(PickerKind kind, int requestId, string text)
    {
        var request = kind switch
        {
            PickerKind.Channel => new KeyboardButtonRequestChat
            {
                RequestId = requestId,
                ChatIsChannel = true,
                BotAdministratorRights = PostRights(),
                UserAdministratorRights = PostRights(),
                RequestTitle = true,
            },
            PickerKind.Group => new KeyboardButtonRequestChat
            {
                RequestId = requestId,
                ChatIsChannel = false,
                BotIsMember = true,
                RequestTitle = true,
            },
            PickerKind.TopicPromotion => new KeyboardButtonRequestChat
            {
                RequestId = requestId,
                ChatIsChannel = false,
                ChatIsForum = true,
                BotAdministratorRights = new ChatAdministratorRights { CanManageTopics = true },
                UserAdministratorRights = new ChatAdministratorRights { CanManageTopics = true },
                RequestTitle = true,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
        return KeyboardButton.WithRequestChat(text, request);
    }

    private static ChatAdministratorRights PostRights() => new() { CanPostMessages = true, CanEditMessages = true };

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Flag(bool value) => value ? "1" : "0";

    private static string[] Split(string csv) => csv.Split(',', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Whether this exact message was already handled (a redelivered update).</summary>
    private static bool IsRepeat(FlowState? flow, Message message)
        => flow is not null && flow.Params.TryGetValue(AnswerKey, out var answer) && answer == Text(message.MessageId);

    /// <summary>
    /// The forum step. Topics are not offered yet: a forum is set up like any other chat, in its General topic.
    /// </summary>
    private Task ContinueForForumAsync(long privateChatId, ChatCheckResult check, int? wizardMessageId, CancellationToken ct)
        => ContinueWithChatAsync(privateChatId, check, 0, wizardMessageId, ct);

    /// <summary>The duplicate rule, then the preset step.</summary>
    private async Task ContinueWithChatAsync(long chatId, ChatCheckResult check, int threadId, int? wizardMessageId, CancellationToken ct)
    {
        var previous = _memory.GetFlow(chatId);
        var checking = previous?.CheckingMessageId;
        if (DestinationCreator.Find(_settings.Current, check.ChatId, threadId) is { } existing)
        {
            try
            {
                await _health.CheckNowAsync(existing.Id, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "TgJelly could not re-check destination {Destination}", existing.Title);
            }

            await ShowExistingAsync(chatId, existing, wizardMessageId, ct).ConfigureAwait(false);
            _memory.ClearFlow(chatId);
            await DeleteAsync(chatId, checking, ct).ConfigureAwait(false);
            return;
        }

        var flow = new FlowState { Screen = FlowScreen };
        if (previous is not null && previous.Params.TryGetValue(AnswerKey, out var answer))
        {
            flow.Params[AnswerKey] = answer;
        }

        flow.Params[ChatIdKey] = Text(check.ChatId);
        flow.Params[TitleKey] = check.Title;
        flow.Params[KindKey] = check.Kind.ToString();
        flow.Params[IsForumKey] = Flag(check.IsForum);
        flow.Params[PhotosBlockedKey] = Flag(check.PhotosBlocked);
        flow.Params[ThreadIdKey] = Text(threadId);
        flow.WizardMessageId = await _screens.ShowAsync(chatId, PresetScreen(check.Title), wizardMessageId, ct).ConfigureAwait(false);
        _memory.SaveFlow(chatId, flow);
        await DeleteAsync(chatId, checking, ct).ConfigureAwait(false);
    }

    /// <summary>Re-checks the chat, adds the destination, and edits the wizard to Done (spec §6.2 step 5).</summary>
    private async Task CreateAsync(long chatId, FlowState flow, PresetKind preset, string[] events, CancellationToken ct)
    {
        // Marks the wizard as used, so a second press while this runs, or a redelivered one, does nothing more.
        flow.Params[CreatingKey] = "1";
        _memory.SaveFlow(chatId, flow);
        var wizard = flow.WizardMessageId;
        var target = long.Parse(flow.Params[ChatIdKey], CultureInfo.InvariantCulture);
        var threadId = int.Parse(flow.Params[ThreadIdKey], CultureInfo.InvariantCulture);

        // The chat may have changed while the admin chose.
        ChatCheckResult check;
        try
        {
            check = await _checker.CheckAsync(target, threadId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TgJelly could not re-check {Title} before adding it", flow.Params[TitleKey]);
            await FailAsync(chatId, _strings.Get("token.unreachable"), wizard, ct).ConfigureAwait(false);
            return;
        }

        if (!check.Passed)
        {
            await FailAsync(chatId, check.Message ?? _strings.Get("check.groupCantSend"), wizard, ct).ConfigureAwait(false);
            return;
        }

        var result = await _creator.CreateAsync(check, threadId, events, preset, null, ct).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case CreateOutcome.Added:
                await _screens.ShowAsync(chatId, DoneScreen(result.Destination!, result.Test, check.PhotosBlocked), wizard, ct).ConfigureAwait(false);
                break;
            case CreateOutcome.AlreadyExists:
                // Added meanwhile elsewhere; CreateAsync already re-checked it.
                await ShowExistingAsync(chatId, result.Destination!, wizard, ct).ConfigureAwait(false);
                break;
            default:
                await FailAsync(chatId, string.Join('\n', result.Errors.Select(e => e.Message)), wizard, ct).ConfigureAwait(false);
                return;
        }

        _memory.ClearFlow(chatId);
    }

    /// <summary>
    /// Ends the current answer: the picker IDs and the prompt are spent, the answer is remembered so a redelivered
    /// update is ignored, the reply keyboard goes with a short message (kept as the "Checking…" message) and the prompt
    /// is deleted.
    /// </summary>
    private async Task BeginCheckAsync(long chatId, FlowState flow, Message answer, string html, CancellationToken ct)
    {
        var prompt = flow.PromptMessageId;
        flow.Params[AnswerKey] = Text(answer.MessageId);
        flow.PickerRequestIds = [];
        flow.PromptMessageId = null;
        flow.PromptSentAt = null;
        _memory.SaveFlow(chatId, flow);

        var sender = _connection.RequireSender();
        var checking = await sender.SendPromptAsync(new OutgoingPrompt(chatId, html, new ReplyKeyboardRemove()), ct).ConfigureAwait(false);
        flow.CheckingMessageId = checking.MessageId;
        _memory.SaveFlow(chatId, flow);
        await DeleteAsync(chatId, prompt, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A failed step (spec §6.2 step 2, "On failure"): deletes the "Checking…" message, explains what's missing with
    /// <c>[Try again] [Advanced options]</c>, records a setup failure and spends the flow.
    /// </summary>
    private async Task FailAsync(long chatId, string message, int? wizardMessageId, CancellationToken ct)
    {
        var flow = _memory.GetFlow(chatId);
        await DeleteAsync(chatId, flow?.CheckingMessageId, ct).ConfigureAwait(false);

        var buttons = new InlineKeyboardMarkup(new[]
        {
            Button("onboard.tryAgain", "ot"),
            Button("onboard.advanced", "ox"),
        });
        await _screens.ShowAsync(chatId, new Screen(_formatter.Escape(message), buttons), wizardMessageId, ct).ConfigureAwait(false);
        _memory.RecordFailure(SetupFailureKind.CheckFailed);

        var spent = new FlowState { Screen = FlowScreen };
        if (flow is not null && flow.Params.TryGetValue(AnswerKey, out var answer))
        {
            spent.Params[AnswerKey] = answer;
        }

        _memory.SaveFlow(chatId, spent);
    }

    /// <summary>"<i>Title</i> is already set up", over that destination's screen.</summary>
    private async Task ShowExistingAsync(long chatId, Destination existing, int? wizardMessageId, CancellationToken ct)
    {
        var detail = await _destinations.HandleAsync("d", [existing.Id], ct).ConfigureAwait(false);
        var note = _strings.Get("destination.duplicate", ("title", "<b>" + _formatter.Escape(existing.Title) + "</b>"));
        var screen = new Screen(note + "\n\n" + detail!.Html, detail.Buttons);
        await _screens.ShowAsync(chatId, screen, wizardMessageId, ct).ConfigureAwait(false);
    }

    private async Task ShowCustomAsync(long chatId, FlowState flow, CancellationToken ct)
    {
        var selected = Split(flow.Params[EventsKey]);
        var rows = EventCatalog.AllKeys
            .Select(k => new[] { InlineKeyboardButton.WithCallbackData(DestinationScreens.Mark(selected.Contains(k, StringComparer.Ordinal)) + " " + _strings.Get("dest.event." + k), CallbackData.Format("oc", k)) })
            .ToList();
        rows.Add([Button("onboard.customDone", "ocd")]);
        var html = "<b>" + _formatter.Escape(flow.Params[TitleKey]) + "</b>\n" + _formatter.Escape(_strings.Get("onboard.preset"));
        flow.WizardMessageId = await _screens.ShowAsync(chatId, new Screen(html, new InlineKeyboardMarkup(rows)), flow.WizardMessageId, ct).ConfigureAwait(false);
        _memory.SaveFlow(chatId, flow);
    }

    private Screen PresetScreen(string title)
    {
        var rows = new[]
        {
            new[] { Button("onboard.preset.everything", "op", nameof(PresetKind.Everything)), Button("onboard.preset.newMedia", "op", nameof(PresetKind.NewMedia)) },
            new[] { Button("onboard.preset.adminAlerts", "op", nameof(PresetKind.AdminAlerts)), Button("onboard.preset.custom", "op", nameof(PresetKind.Custom)) },
        };
        var html = "<b>" + _formatter.Escape(title) + "</b>\n" + _formatter.Escape(_strings.Get("onboard.preset"));
        return new Screen(html, new InlineKeyboardMarkup(rows));
    }

    private Screen DoneScreen(Destination destination, TestSendResult? test, bool photosBlocked)
    {
        var html = _strings.Get(
            "onboard.done",
            ("title", _formatter.Escape(destination.Title)),
            ("groups", _formatter.Escape(DestinationTester.GroupsText(_strings, destination.Events))));
        if (test is { Sent: false })
        {
            html += "\n" + _formatter.Escape(_strings.Get("dest.test.failed", ("error", test.Error ?? string.Empty)));
        }

        if (photosBlocked)
        {
            html += "\n" + _formatter.Escape(_strings.Get("check.photosBlocked"));
        }

        var buttons = new InlineKeyboardMarkup(new[] { Button("onboard.addAnother", "oa"), Button("onboard.menu", "m") });
        return new Screen(html, buttons);
    }

    /// <summary>The wizard's flow, when the pressed message is the live wizard and it isn't creating already.</summary>
    private FlowState? Wizard(CallbackQuery query)
    {
        if (query.Message is not { } message
            || _memory.GetFlow(message.Chat.Id) is not { } flow
            || flow.WizardMessageId != message.MessageId
            || flow.Params.ContainsKey(CreatingKey)
            || !flow.Params.TryGetValue(ChatIdKey, out var chat) || !long.TryParse(chat, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            || !flow.Params.TryGetValue(ThreadIdKey, out var thread) || !int.TryParse(thread, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            || !flow.Params.ContainsKey(TitleKey))
        {
            return null;
        }

        return flow;
    }

    /// <summary>"That picker expired" with the reply keyboard removed, then the menu with its Add destination button.</summary>
    private async Task PickerExpiredAsync(long chatId, CancellationToken ct)
    {
        await _connection.RequireSender().SendPromptAsync(new OutgoingPrompt(chatId, _formatter.Escape(_strings.Get("onboard.expired")), new ReplyKeyboardRemove()), ct).ConfigureAwait(false);
        await _screens.ShowAsync(chatId, MainMenu(), null, ct).ConfigureAwait(false);
    }

    /// <summary>A stale wizard button: "That menu expired", then the menu.</summary>
    private async Task MenuExpiredAsync(CallbackQuery query, CancellationToken ct)
    {
        if (query.Message is not { } message)
        {
            return;
        }

        var chatId = message.Chat.Id;
        await _screens.ShowAsync(chatId, new Screen(_formatter.Escape(_strings.Get("menu.expired")), null), null, ct).ConfigureAwait(false);
        await _screens.ShowAsync(chatId, MainMenu(), null, ct).ConfigureAwait(false);
    }

    /// <summary>Deletes a throwaway message; a failure only leaves it behind, so it doesn't stop the flow.</summary>
    private async Task DeleteAsync(long chatId, int? messageId, CancellationToken ct)
    {
        if (messageId is not { } id)
        {
            return;
        }

        try
        {
            await _connection.RequireSender().DeleteMessageAsync(chatId, id, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TgJelly could not delete an onboarding message");
        }
    }

    private Screen MainMenu() => _menus.Main(_settings.Current.Destinations.Length, 0);

    private InlineKeyboardButton Button(string textKey, string action, params string[] args)
        => InlineKeyboardButton.WithCallbackData(_strings.Get(textKey), CallbackData.Format(action, args));
}
