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
/// The bot's onboarding: an inline prompt whose <c>startchannel</c>/<c>startgroup</c> links open
/// Telegram's chat chooser, the check of the chosen chat and the Done message, all in one message
/// that gets edited. The chosen chat is learned from the <c>my_chat_member</c> its add produces, or from a group
/// <c>/start@bot</c>, matched to the private flow of the person who sent it. Each step keys off its own marker in the
/// chat's flow (the await marker, the wizard message ID), never off <see cref="FlowState.Screen"/> alone, because the
/// settings screens keep their typed-URL input in the same flow.
/// </summary>
public sealed class OnboardingFlow
{
    internal const string FlowScreen = "onboard";
    internal const string ChatIdKey = "chatId";
    internal const string TitleKey = "title";
    internal const string KindKey = "kind";
    internal const string IsForumKey = "isForum";
    internal const string PhotosBlockedKey = "photosBlocked";
    internal const string ThreadIdKey = "threadId";
    internal const string CreatingKey = "creating";

    /// <summary>The flow parameter that says what a prompt waits for; <c>chat</c> while the prompt waits for a chat.</summary>
    internal const string AwaitKey = "await";

    private const string AwaitChat = "chat";

    /// <summary>Marks a spent flow whose check failed; its <see cref="ChatIdKey"/> is the chat Try again re-checks.</summary>
    private const string FailedKey = "failed";

    private readonly BotConnection _connection;
    private readonly BotMemory _memory;
    private readonly ScreenSender _screens;
    private readonly SettingsStore _settings;
    private readonly ChatCheckRetrier _retrier;
    private readonly TopicFlow _topics;
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
    /// <param name="creator">Adds the destination.</param>
    /// <param name="health">The health service, to re-check an existing destination.</param>
    /// <param name="destinations">The destination screens, to open an existing destination.</param>
    /// <param name="menus">The menu screens.</param>
    /// <param name="strings">The string table.</param>
    /// <param name="formatter">The formatter, for escaping.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public OnboardingFlow(BotConnection connection, BotMemory memory, ScreenSender screens, SettingsStore settings, ChatCheckRetrier retrier, TopicFlow topics, DestinationCreator creator, HealthService health, DestinationScreens destinations, MenuScreens menus, StringTable strings, Formatter formatter, TimeProvider time, ILogger<OnboardingFlow> logger)
    {
        _connection = connection;
        _memory = memory;
        _screens = screens;
        _settings = settings;
        _retrier = retrier;
        _topics = topics;
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
    /// Starts over: replaces the chat's flow and shows the prompt. A live prompt shown in another
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
    /// with Add destination, and the flow is forgotten. Flows waiting for anything else are left alone.
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
    /// Matches the bot's own membership change to the setup flow of the person who made it: an
    /// add while that person's prompt waits is the chosen chat, and the bot made an admin in the forum whose promotion
    /// screen waits is the promotion. A redelivered add of the chat already chosen is taken and ignored. Anything else
    /// is left for <see cref="DetectedChatFlow"/>, including an add elsewhere while the promotion screen waits (which
    /// the screen still mentions).
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

        // The promotion waits in the flow's own chat, so it comes ahead of the repeat rule, which would swallow it.
        if (TopicFlow.AwaitsPromotion(flow))
        {
            if (IsChosenChat(flow, update.Chat.Id))
            {
                if (update.NewChatMember is ChatMemberAdministrator)
                {
                    await ContinueWithTopicAsync(update.From.Id, await _topics.OnPromotedAsync(update.From.Id, flow, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                    return true;
                }
            }
            else if (IsAdd(update))
            {
                // Not taken: a real join elsewhere is still a detected chat, even when the note can't be shown.
                try
                {
                    await _topics.OnOtherChatAsync(update.From.Id, flow, update.Chat, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "TgJelly could not note a change in another chat on the topic promotion screen");
                }

                return false;
            }
        }

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
    /// Handles a group <c>/start@bot …</c>: from a person whose prompt waits, it counts as
    /// choosing that group, which covers a group the bot is already in; from the forum whose promotion screen waits,
    /// it counts as the promotion. The bot never replies in the group.
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

        // The promotion waits in the flow's own chat, so it comes ahead of the repeat rule.
        if (TopicFlow.AwaitsPromotion(flow) && IsChosenChat(flow, message.Chat.Id))
        {
            await ContinueWithTopicAsync(from.Id, await _topics.OnPromotedAsync(from.Id, flow, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            return;
        }

        if (IsChosenChat(flow, message.Chat.Id) || !AwaitsChat(flow))
        {
            return;
        }

        await OnChatChosenAsync(from.Id, flow, message.Chat, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles Right here (<c>oh</c>): this private chat becomes the destination, checked once, in the prompt's
    /// message. A press on anything but the live prompt has expired.
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
        await CheckChosenAsync(chatId, flow.WizardMessageId, chatId, _formatter.Escape(_strings.Get("onboard.hereChosen")), retry: false, threadId: 0, ct).ConfigureAwait(false);
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
    /// Handles Try again on a failure (<c>ot</c>): the chat whose check failed is checked once more,
    /// without retries, in the same message, and the wizard continues, with the forum topic already chosen when the
    /// failure kept one. Without such a chat, a fresh prompt replaces the pressed message.
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
            var threadId = int.TryParse(flow.Params.GetValueOrDefault(ThreadIdKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out var thread) ? thread : 0;
            await CheckChosenAsync(chatId, message.MessageId, target, html, retry: false, threadId, ct).ConfigureAwait(false);
            return;
        }

        await StartAsync(chatId, message.MessageId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles a button on the topic wizard: General (<c>tg</c>), Create a "Jellyfin" topic (<c>tc</c>), Use an
    /// existing topic (<c>te</c>), the link prompt's Cancel (<c>tb</c>) or the promotion screen's Check again
    /// (<c>tk</c>).
    /// </summary>
    /// <param name="query">The button press.</param>
    /// <param name="action">The pressed action.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>A task.</returns>
    public async Task OnTopicAsync(CallbackQuery query, string action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (_topics.Wizard(query) is not { } flow || (action == "tk" && !TopicFlow.AwaitsPromotion(flow)))
        {
            await MenuExpiredAsync(query, ct).ConfigureAwait(false);
            return;
        }

        var chatId = query.Message!.Chat.Id;
        var choice = action switch
        {
            "tg" => await _topics.OnGeneralAsync(chatId, flow).ConfigureAwait(false),
            "tc" => await _topics.OnCreateAsync(chatId, flow, ct).ConfigureAwait(false),
            "tk" => await _topics.OnPromotedAsync(chatId, flow, ct).ConfigureAwait(false),
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
    /// Handles typed text while a topic link is awaited ("Use an existing topic").
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
    /// <see cref="OnTopicAsync"/>, not through here, so General doesn't loop back); anything else meets the duplicate rule, then is added
    /// with every event on.
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
    /// Whether a membership change counts as adding the bot: it is now in the chat, and either it
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

    /// <summary>
    /// A chat was chosen in Telegram while the prompt waited: the prompt is spent, the chat remembered, and the prompt's
    /// message turns into "Checking <i>Title</i>…" while the check runs with retries.
    /// </summary>
    private async Task OnChatChosenAsync(long privateChatId, FlowState flow, Chat chat, CancellationToken ct)
    {
        var title = string.IsNullOrEmpty(chat.Title) ? Text(chat.Id) : chat.Title;
        SpendPrompt(flow);
        flow.Params[ChatIdKey] = Text(chat.Id);
        flow.Params[TitleKey] = title;
        _memory.SaveFlow(privateChatId, flow);
        await CheckChosenAsync(privateChatId, flow.WizardMessageId, chat.Id, Checking(title, chat.Id), retry: true, threadId: 0, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Shows the "Checking…" text in the wizard message, checks the chat and continues, with <paramref name="threadId"/>
    /// as the forum topic when one is already chosen (0 asks the topic question for a forum). A transport or Telegram
    /// error, in the "Checking…" edit or in the check, is a failed step that keeps the chat for Try again.
    /// </summary>
    private async Task CheckChosenAsync(long privateChatId, int? wizardMessageId, long chatId, string checkingHtml, bool retry, int threadId, CancellationToken ct)
    {
        var wizard = wizardMessageId;
        ChatCheckResult check;
        try
        {
            wizard = await _screens.ShowAsync(privateChatId, new Screen(checkingHtml, null), wizardMessageId, ct).ConfigureAwait(false);
            check = await _retrier.CheckAsync(chatId, retry, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TgJelly could not check the chosen chat {Chat}", chatId);
            await FailAsync(privateChatId, _strings.Get("token.unreachable"), wizard, chatId, ct).ConfigureAwait(false);
            return;
        }

        await ContinueAfterCheckAsync(privateChatId, check, threadId, wizard, ct).ConfigureAwait(false);
    }

    /// <summary>"Checking <b>Title</b>…", with the chat's ID when the title is unknown.</summary>
    private string Checking(string? title, long chatId)
        => _strings.Get("onboard.checking", ("title", _formatter.Escape(string.IsNullOrEmpty(title) ? Text(chatId) : title)));

    /// <summary>
    /// After the topic step: a chosen topic (General is thread 0) meets the duplicate rule, then the destination is added. Nothing
    /// to do when the step is still waiting or has ended with its own message.
    /// </summary>
    private Task ContinueWithTopicAsync(long chatId, TopicChoice? choice, CancellationToken ct)
        => choice is null ? Task.CompletedTask : ContinueWithChatAsync(chatId, choice.Check, choice.ThreadId, choice.WizardMessageId, ct, choice.TopicName);

    /// <summary>The duplicate rule, then the destination is added with every event on.</summary>
    private async Task ContinueWithChatAsync(long chatId, ChatCheckResult check, int threadId, int? wizardMessageId, CancellationToken ct, string? topicName = null)
    {
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
        flow.Params[ChatIdKey] = Text(check.ChatId);
        flow.Params[TitleKey] = check.Title;
        flow.Params[KindKey] = check.Kind.ToString();
        flow.Params[IsForumKey] = Flag(check.IsForum);
        flow.Params[PhotosBlockedKey] = Flag(check.PhotosBlocked);
        flow.Params[ThreadIdKey] = Text(threadId);
        // No "what should I send" step (presets were removed): a new destination starts with every event on, and the
        // admin trims it from its screen afterwards.
        flow.WizardMessageId = wizardMessageId;
        await CreateAsync(chatId, flow, check, ct, topicName).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds the destination with every event on, using the check that was just made, and edits the wizard to Done.
    /// </summary>
    private async Task CreateAsync(long chatId, FlowState flow, ChatCheckResult check, CancellationToken ct, string? topicName = null)
    {
        // Marks the wizard as used, so a second press while this runs, or a redelivered one, does nothing more.
        flow.Params[CreatingKey] = "1";
        _memory.SaveFlow(chatId, flow);
        var wizard = flow.WizardMessageId;
        var target = long.Parse(flow.Params[ChatIdKey], CultureInfo.InvariantCulture);
        var threadId = int.Parse(flow.Params[ThreadIdKey], CultureInfo.InvariantCulture);

        if (!check.Passed)
        {
            await FailAsync(chatId, check.Message ?? _strings.Get("check.groupCantSend"), wizard, target, ct).ConfigureAwait(false);
            return;
        }

        var result = await _creator.CreateAsync(check, threadId, [.. EventCatalog.AllKeys], null, ct, topicName).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case CreateOutcome.Added:
                await _screens.ShowAsync(chatId, DoneScreen(result.Destination!, result.Test, check.PhotosBlocked), wizard, ct).ConfigureAwait(false);
                break;
            case CreateOutcome.AlreadyExists:
                // Added meanwhile elsewhere; the creator already re-checked it.
                await ShowExistingAsync(chatId, result.Destination!, wizard, ct).ConfigureAwait(false);
                break;
            default:
                await FailAsync(chatId, string.Join('\n', result.Errors.Select(e => e.Message)), wizard, target, ct).ConfigureAwait(false);
                return;
        }

        _memory.ClearFlow(chatId);
    }

    /// <summary>
    /// A failed step ("On failure"): the wizard explains what's missing with
    /// <c>[Try again] [Advanced options]</c>, a setup failure is recorded and the flow is spent. The spent flow keeps
    /// the chat that was checked (and its title and forum topic, when known), so Try again can check it again without
    /// asking for the topic, or creating one, a second time.
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

            if (flow is not null
                && flow.Params.GetValueOrDefault(ChatIdKey) == Text(failed)
                && flow.Params.TryGetValue(ThreadIdKey, out var thread)
                && thread != "0")
            {
                spent.Params[ThreadIdKey] = thread;
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

    /// <summary>
    /// The prompt: Add to a channel and Add to a group open Telegram's chat chooser, Right here
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

        html += ButtonGuide.Lines(_strings, _formatter, ("onboard.addAnother", "onboard.addAnother.about"), ("onboard.menu", "onboard.menu.about"));
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
