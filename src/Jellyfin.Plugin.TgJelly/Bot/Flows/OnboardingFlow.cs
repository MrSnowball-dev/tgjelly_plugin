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
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Jellyfin.Plugin.TgJelly.Bot.Flows;

/// <summary>
/// The bot's onboarding (spec §6.2): an inline prompt whose <c>startchannel</c>/<c>startgroup</c> links open
/// Telegram's chat chooser, the check of the chosen chat, the preset wizard and the Done message, all in one message
/// that gets edited. The chosen chat is learned from the <c>my_chat_member</c> its add produces, or from a group
/// <c>/start@bot</c>, matched to the private flow of the person who sent it. Each step keys off its own marker in the
/// chat's flow (the await marker, the wizard message ID), never off <see cref="FlowState.Screen"/> alone, because the
/// settings screens keep their typed-URL input in the same flow.
/// </summary>
public sealed class OnboardingFlow
{
    internal const string FlowScreen = "onboard";
    internal const string AnswerKey = "answer";
    internal const string ChatIdKey = "chatId";
    internal const string TitleKey = "title";
    internal const string KindKey = "kind";
    internal const string IsForumKey = "isForum";
    internal const string PhotosBlockedKey = "photosBlocked";
    internal const string ThreadIdKey = "threadId";
    internal const string CreatingKey = "creating";

    /// <summary>The flow parameter that says what a prompt waits for; <c>chat</c> while the prompt waits for a chat.</summary>
    internal const string AwaitKey = "await";

    private const string EventsKey = "events";
    private const string AwaitChat = "chat";

    /// <summary>Marks a spent flow whose check failed; its <see cref="ChatIdKey"/> is the chat Try again re-checks.</summary>
    private const string FailedKey = "failed";

    /// <summary>
    /// The texts a stale v0.1 reply keyboard sends when tapped. Only English shipped in v0.1, so they are fixed here
    /// rather than read from the string table.
    /// </summary>
    internal static readonly string[] LegacyReplyTexts = ["📢 Choose a channel", "👥 Choose a group", "💬 Right here", "Cancel"];

    private readonly BotConnection _connection;
    private readonly BotMemory _memory;
    private readonly ScreenSender _screens;
    private readonly SettingsStore _settings;
    private readonly ChatCheckRetrier _retrier;
    private readonly TopicFlow _topics;
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
    /// <param name="retrier">Checks a chosen chat, trying again while the bot's membership settles.</param>
    /// <param name="topics">The forum topic step.</param>
    /// <param name="checker">Re-checks the chat, with its topic, right before creating.</param>
    /// <param name="creator">Adds the destination.</param>
    /// <param name="health">The health service, to re-check an existing destination.</param>
    /// <param name="destinations">The destination screens, to open an existing destination.</param>
    /// <param name="menus">The menu screens.</param>
    /// <param name="strings">The string table.</param>
    /// <param name="formatter">The formatter, for escaping.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public OnboardingFlow(BotConnection connection, BotMemory memory, ScreenSender screens, SettingsStore settings, ChatCheckRetrier retrier, TopicFlow topics, DestinationChecker checker, DestinationCreator creator, HealthService health, DestinationScreens destinations, MenuScreens menus, StringTable strings, Formatter formatter, TimeProvider time, ILogger<OnboardingFlow> logger)
    {
        _connection = connection;
        _memory = memory;
        _screens = screens;
        _settings = settings;
        _retrier = retrier;
        _topics = topics;
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
    /// Starts over: replaces the chat's flow and shows the prompt (spec §6.2 step 1). A live prompt shown in another
    /// message is deleted, so only one prompt waits.
    /// </summary>
    /// <param name="chatId">The private chat.</param>
    /// <param name="editMessageId">The message to turn into the prompt, or <c>null</c> to send it as a new message.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>A task.</returns>
    public async Task StartAsync(long chatId, int? editMessageId, CancellationToken ct)
    {
        var previous = _memory.GetFlow(chatId);
        _memory.ClearFlow(chatId);
        if (previous is not null && AwaitsChat(previous) && previous.WizardMessageId != editMessageId)
        {
            await DeleteAsync(chatId, previous.WizardMessageId, ct).ConfigureAwait(false);
        }

        var id = await _screens.ShowAsync(chatId, PromptScreen(), editMessageId, ct).ConfigureAwait(false);
        var flow = new FlowState { Screen = FlowScreen, WizardMessageId = id, PromptSentAt = _time.GetUtcNow() };
        flow.Params[AwaitKey] = AwaitChat;
        _memory.SaveFlow(chatId, flow);
    }

    /// <summary>
    /// Ends a prompt that <see cref="BotMemory.Prune"/> just expired: it is edited to "That setup prompt expired."
    /// with Add destination, and the flow is forgotten (spec §6.2). Flows waiting for anything else are left alone.
    /// </summary>
    /// <param name="chatId">The private chat whose prompt expired.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>A task.</returns>
    public async Task ExpireAsync(long chatId, CancellationToken ct)
    {
        if (_memory.GetFlow(chatId) is not { } flow || !flow.Params.TryGetValue(AwaitKey, out var awaited) || awaited != AwaitChat)
        {
            return;
        }

        try
        {
            if (flow.WizardMessageId is { } id)
            {
                await _screens.ShowAsync(chatId, ExpiredScreen(), id, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TgJelly could not mark an expired setup prompt");
        }

        _memory.ClearFlow(chatId);
    }

    /// <summary>
    /// Matches the bot's own membership change to the setup flow of the person who made it (spec §6.2 step 2): an add
    /// while that person's prompt waits is the chosen chat. A redelivered add of the chat already chosen is taken and
    /// ignored. Anything else is left for <see cref="DetectedChatFlow"/>.
    /// </summary>
    /// <param name="update">The membership change.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns><c>true</c> when a setup flow took the update.</returns>
    public async Task<bool> OnChatMemberAsync(ChatMemberUpdated update, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.Chat.Type == ChatType.Private || _memory.GetFlow(update.From.Id) is not { } flow)
        {
            return false;
        }

        // Cases for steps that wait for something in their own chat belong here, ahead of the repeat rule: such a
        // flow already has that chat as its chosen chat, so the repeat rule would swallow the update.
        if (!IsAdd(update))
        {
            return false;
        }

        if (IsChosenChat(flow, update.Chat.Id))
        {
            _logger.LogDebug("TgJelly ignored an add it already handled");
            return true;
        }

        if (!AwaitsChat(flow))
        {
            return false;
        }

        await OnChatChosenAsync(update.From.Id, flow, update.Chat, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Handles a group <c>/start@bot …</c> (spec §6.2 step 2, F43): from a person whose prompt waits, it counts as
    /// choosing that group, which covers a group the bot is already in. The bot never replies in the group.
    /// </summary>
    /// <param name="message">The group message.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>A task.</returns>
    public async Task OnGroupStartAsync(Message message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.From is not { } from || _memory.GetFlow(from.Id) is not { } flow)
        {
            return;
        }

        // Cases for steps that wait in their own chat belong here too, ahead of the repeat rule.
        if (IsChosenChat(flow, message.Chat.Id) || !AwaitsChat(flow))
        {
            return;
        }

        await OnChatChosenAsync(from.Id, flow, message.Chat, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles Right here (<c>oh</c>): this private chat becomes the destination, checked once, in the prompt's
    /// message (spec §6.2 step 1). A press on anything but the live prompt has expired.
    /// </summary>
    /// <param name="query">The button press.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>A task.</returns>
    public async Task OnRightHereAsync(CallbackQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (Prompt(query) is not { } flow)
        {
            await MenuExpiredAsync(query, ct).ConfigureAwait(false);
            return;
        }

        var chatId = query.Message!.Chat.Id;
        SpendPrompt(flow);
        flow.Params[ChatIdKey] = Text(chatId);
        _memory.SaveFlow(chatId, flow);
        await CheckChosenAsync(chatId, flow.WizardMessageId, chatId, _formatter.Escape(_strings.Get("onboard.hereChosen")), retry: false, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles the prompt's Cancel (<c>on</c>): ends the prompt and shows the menu in its message. A press on anything
    /// but the live prompt has expired.
    /// </summary>
    /// <param name="query">The button press.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>A task.</returns>
    public async Task OnCancelAsync(CallbackQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (Prompt(query) is null)
        {
            await MenuExpiredAsync(query, ct).ConfigureAwait(false);
            return;
        }

        var message = query.Message!;
        _memory.ClearFlow(message.Chat.Id);
        await _screens.ShowAsync(message.Chat.Id, MainMenu(), message.MessageId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles Try again on a failure (<c>ot</c>, spec §6.2 step 2): the chat whose check failed is checked once more,
    /// without retries, in the same message, and the wizard continues. Without such a chat, a fresh prompt replaces
    /// the pressed message.
    /// </summary>
    /// <param name="query">The button press.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>A task.</returns>
    public async Task OnTryAgainAsync(CallbackQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Message is not { } message)
        {
            return;
        }

        var chatId = message.Chat.Id;
        if (_memory.GetFlow(chatId) is { } flow
            && flow.WizardMessageId == message.MessageId
            && flow.Params.ContainsKey(FailedKey)
            && flow.Params.TryGetValue(ChatIdKey, out var failed)
            && long.TryParse(failed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var target))
        {
            var html = target == chatId
                ? _formatter.Escape(_strings.Get("onboard.hereChosen"))
                : Checking(flow.Params.GetValueOrDefault(TitleKey), target);
            await CheckChosenAsync(chatId, message.MessageId, target, html, retry: false, ct).ConfigureAwait(false);
            return;
        }

        await StartAsync(chatId, message.MessageId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles a <c>chat_shared</c>: the answer to the topic-promotion picker goes to the topic step; a redelivered
    /// one is ignored; any other is left over from a v0.1 picker and is stale input.
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

        if (flow is not null && TopicFlow.IsPromotionRequest(flow, shared.RequestId))
        {
            await ContinueWithTopicAsync(chatId, await _topics.OnPromotionSharedAsync(chatId, flow, message, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            return;
        }

        await OnLegacyReplyAsync(message, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles stale input from a v0.1 client (spec §6.2): a tap on its reply keyboard or its chat picker. "That menu
    /// expired" removes the keyboard the client still shows, then the menu follows.
    /// </summary>
    /// <param name="message">The private message.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>A task.</returns>
    public async Task OnLegacyReplyAsync(Message message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        var chatId = message.Chat.Id;
        var expired = new OutgoingPrompt(chatId, _formatter.Escape(_strings.Get("menu.expired")), new ReplyKeyboardRemove());
        await _connection.RequireSender().SendPromptAsync(expired, ct).ConfigureAwait(false);
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
            await ShowCustomAsync(chatId, flow, null, ct).ConfigureAwait(false);
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

        await ShowCustomAsync(query.Message!.Chat.Id, flow, EventCatalog.IsKnown(key) ? EventCatalog.GroupOf(key) : null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles a group button on the Custom wizard (<c>ocg</c>): opens that group's switches, or the groups without one.
    /// </summary>
    /// <param name="query">The button press.</param>
    /// <param name="group">The event group, or <c>null</c> for the chooser.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>A task.</returns>
    public async Task OnCustomGroupAsync(CallbackQuery query, string? group, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (Wizard(query) is not { } flow || !flow.Params.ContainsKey(EventsKey))
        {
            await MenuExpiredAsync(query, ct).ConfigureAwait(false);
            return;
        }

        await ShowCustomAsync(query.Message!.Chat.Id, flow, group, ct).ConfigureAwait(false);
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
    /// Handles a button on the topic wizard: General (<c>tg</c>), Create a "Jellyfin" topic (<c>tc</c>) or Use an
    /// existing topic (<c>te</c>) (spec §6.2 step 3).
    /// </summary>
    /// <param name="query">The button press.</param>
    /// <param name="action">The pressed action.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>A task.</returns>
    public async Task OnTopicAsync(CallbackQuery query, string action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (_topics.Wizard(query) is not { } flow)
        {
            await MenuExpiredAsync(query, ct).ConfigureAwait(false);
            return;
        }

        var chatId = query.Message!.Chat.Id;
        var choice = action switch
        {
            "tg" => await _topics.OnGeneralAsync(chatId, flow, ct).ConfigureAwait(false),
            "tc" => await _topics.OnCreateAsync(chatId, flow, ct).ConfigureAwait(false),
            "tb" => await _topics.OnBackAsync(chatId, flow, ct).ConfigureAwait(false),
            _ => await _topics.OnExistingAsync(chatId, flow, ct).ConfigureAwait(false),
        };
        await ContinueWithTopicAsync(chatId, choice, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles Use an existing topic on the Advanced options screen (<c>ol</c>): with a picked chat in the flow the
    /// link must name that chat; with none, the link's own chat is checked.
    /// </summary>
    /// <param name="query">The button press.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>A task.</returns>
    public Task OnAdvancedTopicAsync(CallbackQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        return _topics.Wizard(query) is not null
            ? OnTopicAsync(query, "te", ct)
            : _topics.AskForLinkAnywhereAsync(query.Message!.Chat.Id, query.Message.MessageId, ct);
    }

    /// <summary>
    /// Handles typed text while a topic link is awaited (spec §6.5, "Use an existing topic").
    /// </summary>
    /// <param name="chatId">The private chat.</param>
    /// <param name="text">The text.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns><c>true</c> when the text was the awaited link (right or wrong); <c>false</c> when no link was awaited.</returns>
    public async Task<bool> OnTextAsync(long chatId, string text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_memory.GetFlow(chatId) is not { } flow || !TopicFlow.AwaitsLink(flow))
        {
            return false;
        }

        await ContinueWithTopicAsync(chatId, await _topics.OnLinkAsync(chatId, flow, text, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Continues after a chat was checked: a failure is explained with <c>[Try again] [Advanced options]</c>; a forum
    /// whose topic isn't chosen yet goes to the topic question (the topic's answers continue through
    /// <see cref="OnTopicAsync"/>, not through here, so General doesn't loop back); anything else meets the duplicate rule, then the preset
    /// step (spec §6.2 steps 2–4).
    /// </summary>
    /// <param name="privateChatId">The private chat.</param>
    /// <param name="check">The check of the chosen chat.</param>
    /// <param name="threadId">The forum topic, or 0.</param>
    /// <param name="wizardMessageId">The wizard message to edit, or <c>null</c> to send a new one.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>A task.</returns>
    public async Task ContinueAfterCheckAsync(long privateChatId, ChatCheckResult check, int threadId, int? wizardMessageId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (!check.Passed)
        {
            await FailAsync(privateChatId, check.Message ?? _strings.Get("check.groupCantSend"), wizardMessageId, check.ChatId, ct).ConfigureAwait(false);
            return;
        }

        if (check.IsForum && threadId == 0)
        {
            await _topics.AskAsync(privateChatId, check, wizardMessageId, ct).ConfigureAwait(false);
            return;
        }

        await ContinueWithChatAsync(privateChatId, check, threadId, wizardMessageId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether a flow's prompt is waiting for a chat to be chosen.
    /// </summary>
    /// <param name="flow">The chat's flow.</param>
    /// <returns><c>true</c> while the prompt is live: it waits for a chat and hasn't expired.</returns>
    internal static bool AwaitsChat(FlowState flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return flow.PromptSentAt is not null && flow.Params.TryGetValue(AwaitKey, out var awaited) && awaited == AwaitChat;
    }

    /// <summary>
    /// Builds the native chat picker button that asks to promote the bot in a forum so it can manage topics
    /// (spec §6.2 step 3).
    /// </summary>
    /// <param name="kind">The picker.</param>
    /// <param name="requestId">Its fresh request ID.</param>
    /// <param name="text">The button text.</param>
    /// <returns>The button.</returns>
    internal static KeyboardButton PickerButton(PickerKind kind, int requestId, string text)
    {
        var request = kind switch
        {
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

    /// <summary>
    /// Whether a membership change counts as adding the bot (spec §6.2 step 2): it is now in the chat, and either it
    /// wasn't before or it was just made an admin (rights granted in a chat it was already in).
    /// </summary>
    private static bool IsAdd(ChatMemberUpdated u)
        => (u.NewChatMember is ChatMemberMember or ChatMemberAdministrator || u.NewChatMember is ChatMemberRestricted { IsMember: true })
           && (u.OldChatMember.Status is ChatMemberStatus.Left or ChatMemberStatus.Kicked || u.NewChatMember is ChatMemberAdministrator);

    /// <summary>
    /// Whether the flow already chose this chat, so the update is a redelivery (or a later echo) of the choice. A
    /// failed check doesn't count: after a failure, a later add of the chat is like any other.
    /// </summary>
    private static bool IsChosenChat(FlowState flow, long chatId)
        => !flow.Params.ContainsKey(FailedKey) && flow.Params.TryGetValue(ChatIdKey, out var chosen) && chosen == Text(chatId);

    /// <summary>Ends the prompt's wait: from here on the flow is on the check.</summary>
    private static void SpendPrompt(FlowState flow)
    {
        flow.Params.Remove(AwaitKey);
        flow.PromptSentAt = null;
    }

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Flag(bool value) => value ? "1" : "0";

    private static string[] Split(string csv) => csv.Split(',', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Whether this exact message was already handled (a redelivered update).</summary>
    private static bool IsRepeat(FlowState? flow, Message message)
        => flow is not null && flow.Params.TryGetValue(AnswerKey, out var answer) && answer == Text(message.MessageId);

    /// <summary>
    /// A chat was chosen in Telegram while the prompt waited: the prompt is spent, the chat remembered, and the prompt's
    /// message turns into "Checking <i>Title</i>…" while the check runs with retries (spec §6.2 step 2).
    /// </summary>
    private async Task OnChatChosenAsync(long privateChatId, FlowState flow, Chat chat, CancellationToken ct)
    {
        var title = string.IsNullOrEmpty(chat.Title) ? Text(chat.Id) : chat.Title;
        SpendPrompt(flow);
        flow.Params[ChatIdKey] = Text(chat.Id);
        flow.Params[TitleKey] = title;
        _memory.SaveFlow(privateChatId, flow);
        await CheckChosenAsync(privateChatId, flow.WizardMessageId, chat.Id, Checking(title, chat.Id), retry: true, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Shows the "Checking…" text in the wizard message, checks the chat and continues; a transport error is a failed
    /// step that keeps the chat for Try again.
    /// </summary>
    private async Task CheckChosenAsync(long privateChatId, int? wizardMessageId, long chatId, string checkingHtml, bool retry, CancellationToken ct)
    {
        var wizard = await _screens.ShowAsync(privateChatId, new Screen(checkingHtml, null), wizardMessageId, ct).ConfigureAwait(false);
        ChatCheckResult check;
        try
        {
            check = await _retrier.CheckAsync(chatId, retry, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TgJelly could not check the chosen chat {Chat}", chatId);
            await FailAsync(privateChatId, _strings.Get("token.unreachable"), wizard, chatId, ct).ConfigureAwait(false);
            return;
        }

        await ContinueAfterCheckAsync(privateChatId, check, 0, wizard, ct).ConfigureAwait(false);
    }

    /// <summary>"Checking <b>Title</b>…", with the chat's ID when the title is unknown.</summary>
    private string Checking(string? title, long chatId)
        => _strings.Get("onboard.checking", ("title", _formatter.Escape(string.IsNullOrEmpty(title) ? Text(chatId) : title)));

    /// <summary>
    /// After the topic step: a chosen topic (General is thread 0) meets the duplicate rule, then the preset step. Nothing
    /// to do when the step is still waiting or has ended with its own message.
    /// </summary>
    private Task ContinueWithTopicAsync(long chatId, TopicChoice? choice, CancellationToken ct)
        => choice is null ? Task.CompletedTask : ContinueWithChatAsync(chatId, choice.Check, choice.ThreadId, choice.WizardMessageId, ct);

    /// <summary>The duplicate rule, then the preset step.</summary>
    private async Task ContinueWithChatAsync(long chatId, ChatCheckResult check, int threadId, int? wizardMessageId, CancellationToken ct)
    {
        var previous = _memory.GetFlow(chatId);
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
            await FailAsync(chatId, _strings.Get("token.unreachable"), wizard, target, ct).ConfigureAwait(false);
            return;
        }

        if (!check.Passed)
        {
            await FailAsync(chatId, check.Message ?? _strings.Get("check.groupCantSend"), wizard, target, ct).ConfigureAwait(false);
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
                await FailAsync(chatId, string.Join('\n', result.Errors.Select(e => e.Message)), wizard, target, ct).ConfigureAwait(false);
                return;
        }

        _memory.ClearFlow(chatId);
    }

    /// <summary>
    /// A failed step (spec §6.2 step 2, "On failure"): the wizard explains what's missing with
    /// <c>[Try again] [Advanced options]</c>, a setup failure is recorded and the flow is spent. The spent flow keeps
    /// the chat that was checked (and its title, when known), so Try again can check it again.
    /// </summary>
    private async Task FailAsync(long chatId, string message, int? wizardMessageId, long? failedChatId, CancellationToken ct)
    {
        var flow = _memory.GetFlow(chatId);
        var buttons = new InlineKeyboardMarkup(new[]
        {
            Button("onboard.tryAgain", "ot"),
            Button("onboard.advanced", "ox"),
        });
        var html = _formatter.Escape(message)
            + ButtonGuide.Lines(_strings, _formatter, ("onboard.tryAgain", "onboard.tryAgain.about"), ("onboard.advanced", "onboard.advanced.about"));
        var shown = await _screens.ShowAsync(chatId, new Screen(html, buttons), wizardMessageId, ct).ConfigureAwait(false);
        _memory.RecordFailure(SetupFailureKind.CheckFailed);

        var spent = new FlowState { Screen = FlowScreen, WizardMessageId = shown };
        if (flow is not null && flow.Params.TryGetValue(AnswerKey, out var answer))
        {
            spent.Params[AnswerKey] = answer;
        }

        if (failedChatId is { } failed)
        {
            spent.Params[ChatIdKey] = Text(failed);
            spent.Params[FailedKey] = "1";
            if (flow is not null
                && flow.Params.GetValueOrDefault(ChatIdKey) == Text(failed)
                && flow.Params.TryGetValue(TitleKey, out var title))
            {
                spent.Params[TitleKey] = title;
            }
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

    private async Task ShowCustomAsync(long chatId, FlowState flow, string? group, CancellationToken ct)
    {
        var selected = Split(flow.Params[EventsKey]);
        var title = "<b>" + _formatter.Escape(flow.Params[TitleKey]) + "</b>\n";
        Screen screen;
        if (group is not null && EventCatalog.Groups.Contains(group, StringComparer.Ordinal))
        {
            var rows = EventPicker.ToggleRows(_strings, group, selected, k => CallbackData.Format("oc", k));
            rows.Add([Button("menu.back", "ocg")]);
            screen = new Screen(title + "<b>" + Icons.Heading(_strings, _formatter, "events.group." + group) + "</b>", new InlineKeyboardMarkup(rows));
        }
        else
        {
            var rows = EventPicker.GroupRows(_strings, selected, g => CallbackData.Format("ocg", g));
            rows.Add([Button("onboard.customDone", "ocd")]);
            var guide = ButtonGuide.Lines(
                _strings,
                _formatter,
                [.. EventCatalog.Groups.Select(g => ("events.group." + g, "events.group." + g + ".about")), ("onboard.customDone", "onboard.customDone.about")]);
            screen = new Screen(title + _formatter.Escape(_strings.Get("events.choose")) + guide, new InlineKeyboardMarkup(rows));
        }

        flow.WizardMessageId = await _screens.ShowAsync(chatId, screen, flow.WizardMessageId, ct).ConfigureAwait(false);
        _memory.SaveFlow(chatId, flow);
    }

    private Screen PresetScreen(string title)
    {
        var rows = new[]
        {
            new[] { Button("onboard.preset.everything", "op", nameof(PresetKind.Everything)), Button("onboard.preset.newMedia", "op", nameof(PresetKind.NewMedia)) },
            new[] { Button("onboard.preset.adminAlerts", "op", nameof(PresetKind.AdminAlerts)), Button("onboard.preset.custom", "op", nameof(PresetKind.Custom)) },
        };
        var guide = ButtonGuide.Lines(
            _strings,
            _formatter,
            ("onboard.preset.everything", "onboard.preset.everything.about"),
            ("onboard.preset.newMedia", "onboard.preset.newMedia.about"),
            ("onboard.preset.adminAlerts", "onboard.preset.adminAlerts.about"),
            ("onboard.preset.custom", "onboard.preset.custom.about"));
        var html = "<b>" + _formatter.Escape(title) + "</b>\n" + _formatter.Escape(_strings.Get("onboard.preset")) + guide;
        return new Screen(html, new InlineKeyboardMarkup(rows));
    }

    /// <summary>
    /// The prompt (spec §6.2 step 1): Add to a channel and Add to a group open Telegram's chat chooser, Right here
    /// uses this chat, up to three chats the bot is already in are offered as Set up rows, and Cancel ends it.
    /// </summary>
    private Screen PromptScreen()
    {
        var bot = _connection.BotUsername;
        var rows = new List<InlineKeyboardButton[]>
        {
            new[] { Buttons.Url(_strings, "onboard.channel", BotLinks.StartChannel(bot)) },
            new[] { Buttons.Url(_strings, "onboard.group", BotLinks.StartGroup(bot)) },
            new[] { Button("onboard.here", "oh") },
        };
        var detected = _memory.VisibleDetected(_settings.Current).Take(3).ToList();
        foreach (var chat in detected)
        {
            rows.Add([Buttons.Callback(_strings, "detected.setUp", CallbackData.Format("xs", Text(chat.ChatId)), ("title", MenuScreens.Label(chat.Title)))]);
        }

        rows.Add([Button("onboard.cancel", "on")]);

        var guide = ButtonGuide.Lines(
            _strings,
            _formatter,
            ("onboard.channel", "onboard.channel.about"),
            ("onboard.group", "onboard.group.about"),
            ("onboard.here", "onboard.here.about"));
        if (detected.Count > 0)
        {
            // The Set up rows share one line.
            guide += "\n" + ButtonGuide.Line(_formatter, _strings.Get("detected.setUp", ("title", "…")), _strings.Get("onboard.detected.about"));
        }

        guide += "\n" + ButtonGuide.Line(_formatter, _strings.Get("onboard.cancel"), _strings.Get("onboard.cancel.about"));
        var html = _formatter.Escape(_strings.Get("onboard.prompt")) + "\n" + _formatter.Escape(_strings.Get("onboard.hint")) + guide;
        return new Screen(html, new InlineKeyboardMarkup(rows));
    }

    /// <summary>"That setup prompt expired." with Add destination.</summary>
    private Screen ExpiredScreen()
        => new(
            _formatter.Escape(_strings.Get("onboard.expired")) + ButtonGuide.Lines(_strings, _formatter, ("menu.add", "onboard.expired.about")),
            new InlineKeyboardMarkup(Button("menu.add", "oa")));

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

    /// <summary>The prompt's flow, when the pressed message is the live prompt.</summary>
    private FlowState? Prompt(CallbackQuery query)
        => query.Message is { } message
           && _memory.GetFlow(message.Chat.Id) is { } flow
           && flow.WizardMessageId == message.MessageId
           && AwaitsChat(flow)
            ? flow
            : null;

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

    /// <summary>Deletes a replaced prompt; a failure only leaves it behind, so it doesn't stop the flow.</summary>
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

    private Screen MainMenu() => _menus.Main(_settings.Current.Destinations.Length, _memory.VisibleDetected(_settings.Current).Count);

    private InlineKeyboardButton Button(string textKey, string action, params string[] args)
        => Buttons.Callback(_strings, textKey, CallbackData.Format(action, args));
}
