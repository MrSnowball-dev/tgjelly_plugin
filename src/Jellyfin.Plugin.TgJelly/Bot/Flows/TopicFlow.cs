using System;
using System.Collections.Generic;
using System.Globalization;
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
/// The forum step of onboarding (spec §6.2 step 3, §6.5 "Use an existing topic"): where in the forum to post. Each
/// handler returns the <see cref="TopicChoice"/> once a topic is chosen, and <c>null</c> while it still waits (for the
/// promotion or for the link) or after it ended with its own message. Steps key off their own markers in the chat's
/// flow, never off <see cref="FlowState.Screen"/> alone.
/// </summary>
public sealed class TopicFlow
{
    private const string TopicKey = "topic";
    private const string PromotionKey = "promotionRequest";
    private const string InputKey = "input";
    private const string LinkInput = "topicLink";
    private const string AnyChatKey = "anyChat";
    private const string TopicName = "Jellyfin";

    private readonly BotConnection _connection;
    private readonly BotMemory _memory;
    private readonly ScreenSender _screens;
    private readonly ChatCheckRetrier _retrier;
    private readonly DestinationChecker _checker;
    private readonly StringTable _strings;
    private readonly Formatter _formatter;
    private readonly TimeProvider _time;
    private readonly ILogger<TopicFlow> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TopicFlow"/> class.
    /// </summary>
    /// <param name="connection">The bot connection.</param>
    /// <param name="memory">The bot's memory, for the chat's flow.</param>
    /// <param name="screens">Shows screens.</param>
    /// <param name="retrier">Re-checks the forum after its promotion, trying again while the bot's rights settle.</param>
    /// <param name="checker">Checks an existing topic.</param>
    /// <param name="strings">The string table.</param>
    /// <param name="formatter">The formatter, for escaping.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public TopicFlow(BotConnection connection, BotMemory memory, ScreenSender screens, ChatCheckRetrier retrier, DestinationChecker checker, StringTable strings, Formatter formatter, TimeProvider time, ILogger<TopicFlow> logger)
    {
        _connection = connection;
        _memory = memory;
        _screens = screens;
        _retrier = retrier;
        _checker = checker;
        _strings = strings;
        _formatter = formatter;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Whether a flow is waiting for a pasted topic link.
    /// </summary>
    /// <param name="flow">The chat's flow.</param>
    /// <returns><c>true</c> when it is on the topic step and asked for the link.</returns>
    public static bool AwaitsLink(FlowState flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return (flow.Params.ContainsKey(TopicKey) || flow.Params.ContainsKey(AnyChatKey))
            && flow.Params.TryGetValue(InputKey, out var input) && input == LinkInput;
    }

    /// <summary>
    /// Whether a picked chat answers the topic-promotion prompt.
    /// </summary>
    /// <param name="flow">The chat's flow.</param>
    /// <param name="requestId">The <c>request_id</c> of the <c>chat_shared</c>.</param>
    /// <returns><c>true</c> when the flow's live promotion picker has this request ID.</returns>
    public static bool IsPromotionRequest(FlowState flow, int requestId)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return flow.Params.ContainsKey(TopicKey)
            && flow.PickerRequestIds.Contains(requestId)
            && flow.Params.TryGetValue(PromotionKey, out var promotion)
            && promotion == Text(requestId);
    }

    /// <summary>
    /// Asks where to post: <c>[General topic] [Create a "Jellyfin" topic] [Use an existing topic…]</c>, as the wizard
    /// message (new, or edited when <paramref name="wizardMessageId"/> is given). The "Checking…" message is deleted.
    /// </summary>
    /// <param name="privateChatId">The private chat.</param>
    /// <param name="check">The passing check of the forum.</param>
    /// <param name="wizardMessageId">The wizard message to edit, or <c>null</c> to send a new one.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>A task.</returns>
    public async Task AskAsync(long privateChatId, ChatCheckResult check, int? wizardMessageId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(check);
        var previous = _memory.GetFlow(privateChatId);
        var flow = new FlowState { Screen = OnboardingFlow.FlowScreen };
        if (previous is not null && previous.Params.TryGetValue(OnboardingFlow.AnswerKey, out var answer))
        {
            flow.Params[OnboardingFlow.AnswerKey] = answer;
        }

        flow.Params[TopicKey] = "1";
        flow.Params[OnboardingFlow.ChatIdKey] = Text(check.ChatId);
        flow.Params[OnboardingFlow.TitleKey] = check.Title;
        flow.Params[OnboardingFlow.KindKey] = check.Kind.ToString();
        flow.Params[OnboardingFlow.IsForumKey] = "1";
        flow.Params[OnboardingFlow.PhotosBlockedKey] = check.PhotosBlocked ? "1" : "0";

        var rows = new[]
        {
            new[] { Button("topic.general", "tg") },
            new[] { Button("topic.create", "tc") },
            new[] { Button("topic.existing", "te") },
        };
        var html = "<b>" + _formatter.Escape(check.Title) + "</b>\n" + _formatter.Escape(_strings.Get("topic.ask"))
            + ButtonGuide.Lines(_strings, _formatter, ("topic.general", "topic.general.about"), ("topic.create", "topic.create.about"), ("topic.existing", "topic.existing.about"));
        flow.WizardMessageId = await _screens.ShowAsync(privateChatId, new Screen(html, new InlineKeyboardMarkup(rows)), wizardMessageId, ct).ConfigureAwait(false);
        _memory.SaveFlow(privateChatId, flow);
        await DeleteAsync(privateChatId, previous?.CheckingMessageId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The flow of a press on the live topic wizard.
    /// </summary>
    /// <param name="query">The button press.</param>
    /// <returns>The flow, or <c>null</c> when the pressed message isn't the live topic wizard.</returns>
    public FlowState? Wizard(CallbackQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Message is not { } message
            || _memory.GetFlow(message.Chat.Id) is not { } flow
            || flow.WizardMessageId != message.MessageId
            || !flow.Params.ContainsKey(TopicKey)
            || !flow.Params.TryGetValue(OnboardingFlow.ChatIdKey, out var chat) || !long.TryParse(chat, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            || !flow.Params.ContainsKey(OnboardingFlow.TitleKey))
        {
            return null;
        }

        return flow;
    }

    /// <summary>
    /// General topic (<c>tg</c>): stored as thread 0 (spec §6.2 step 3).
    /// </summary>
    /// <param name="chatId">The private chat.</param>
    /// <param name="flow">The wizard's flow.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>The choice.</returns>
    public async Task<TopicChoice?> OnGeneralAsync(long chatId, FlowState flow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(flow);
        await SpendAsync(chatId, flow, ct).ConfigureAwait(false);
        return new TopicChoice(Rebuild(flow), 0, flow.WizardMessageId);
    }

    /// <summary>
    /// Create a "Jellyfin" topic (<c>tc</c>): creates it when the bot can manage topics, otherwise sends the throwaway
    /// promotion prompt. A second press while this runs does nothing; one while the prompt waits (or after it expired)
    /// replaces the prompt with a fresh one.
    /// </summary>
    /// <param name="chatId">The private chat.</param>
    /// <param name="flow">The wizard's flow.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>The choice once the topic exists; <c>null</c> while the promotion is awaited or after a failure.</returns>
    public async Task<TopicChoice?> OnCreateAsync(long chatId, FlowState flow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (flow.Params.ContainsKey(OnboardingFlow.CreatingKey))
        {
            // A promotion prompt that is still waiting, expired or was cancelled in Telegram: spend it and ask afresh.
            // Without one, a creation is still running and this press does nothing.
            if (!flow.Params.ContainsKey(PromotionKey))
            {
                return null;
            }

            await SpendAsync(chatId, flow, ct).ConfigureAwait(false);
        }

        flow.Params[OnboardingFlow.CreatingKey] = "1";
        _memory.SaveFlow(chatId, flow);

        var check = Rebuild(flow);
        bool canManage;
        try
        {
            canManage = await CanManageTopicsAsync(check.ChatId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TgJelly could not read the bot's rights in {Title}", check.Title);
            return await FailAsync(chatId, flow, null, ct).ConfigureAwait(false);
        }

        if (canManage)
        {
            return await CreateTopicAsync(chatId, flow, check, ct).ConfigureAwait(false);
        }

        var requestId = _memory.NextRequestId();
        var keyboard = new ReplyKeyboardMarkup([[OnboardingFlow.PickerButton(PickerKind.TopicPromotion, requestId, _strings.Get("topic.allow"))]]) { ResizeKeyboard = true };
        Message prompt;
        try
        {
            prompt = await _connection.RequireSender()
                .SendPromptAsync(new OutgoingPrompt(chatId, _formatter.Escape(_strings.Get("topic.allowPrompt")), keyboard), ct).ConfigureAwait(false);
        }
        catch
        {
            // Nothing is awaited, so Create must stay available.
            flow.Params.Remove(OnboardingFlow.CreatingKey);
            _memory.SaveFlow(chatId, flow);
            throw;
        }

        flow.PromptMessageId = prompt.MessageId;
        flow.PromptSentAt = _time.GetUtcNow();
        flow.PickerRequestIds = [requestId];
        flow.Params[PromotionKey] = Text(requestId);
        _memory.SaveFlow(chatId, flow);
        return null;
    }

    /// <summary>
    /// The picked chat answered the promotion prompt: re-checks the forum and the bot's rights, deletes the prompt and
    /// creates the topic. A different chat is rejected and the prompt stays live.
    /// </summary>
    /// <param name="chatId">The private chat.</param>
    /// <param name="flow">The chat's flow, on the promotion step.</param>
    /// <param name="message">The private message carrying <see cref="Message.ChatShared"/>.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>The choice once the topic exists; <c>null</c> after a rejection or a failure.</returns>
    public async Task<TopicChoice?> OnPromotionSharedAsync(long chatId, FlowState flow, Message message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(message);
        var check = Rebuild(flow);
        if (message.ChatShared!.ChatId != check.ChatId)
        {
            var wrong = new OutgoingText(chatId, 0, _formatter.Escape(_strings.Get("topic.wrongChat")), ParseMode.Html, null, null, false);
            await _connection.RequireSender().SendTextAsync(wrong, ct).ConfigureAwait(false);
            return null;
        }

        // The prompt is spent: remember the answer so a redelivery is ignored, remove the reply keyboard with a short
        // message (kept as the "Checking…" message) and delete the prompt.
        var prompt = flow.PromptMessageId;
        flow.Params[OnboardingFlow.AnswerKey] = Text(message.MessageId);
        ClearPrompt(flow);
        _memory.SaveFlow(chatId, flow);
        var checking = _strings.Get("onboard.checking", ("title", _formatter.Escape(check.Title)));
        var sent = await _connection.RequireSender().SendPromptAsync(new OutgoingPrompt(chatId, checking, new ReplyKeyboardRemove()), ct).ConfigureAwait(false);
        flow.CheckingMessageId = sent.MessageId;
        _memory.SaveFlow(chatId, flow);
        await DeleteAsync(chatId, prompt, ct).ConfigureAwait(false);

        ChatCheckResult fresh;
        try
        {
            fresh = await _retrier.CheckAsync(check.ChatId, retry: true, ct).ConfigureAwait(false);
            if (fresh.Passed && !await _retrier.UntilAsync(() => CanManageTopicsAsync(check.ChatId, ct), ct).ConfigureAwait(false))
            {
                return await FailAsync(chatId, flow, null, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TgJelly could not check {Title} after the topic promotion", check.Title);
            return await FailAsync(chatId, flow, null, ct).ConfigureAwait(false);
        }

        return fresh.Passed
            ? await CreateTopicAsync(chatId, flow, fresh, ct).ConfigureAwait(false)
            : await FailAsync(chatId, flow, fresh.Message, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Use an existing topic (<c>te</c>): records <see cref="SetupFailureKind.TopicNotPickable"/> (Telegram has no topic
    /// picker, F35) and asks for the topic's link.
    /// </summary>
    /// <param name="chatId">The private chat.</param>
    /// <param name="flow">The wizard's flow.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns><c>null</c>: the link is awaited.</returns>
    public async Task<TopicChoice?> OnExistingAsync(long chatId, FlowState flow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(flow);
        await SpendAsync(chatId, flow, ct).ConfigureAwait(false);
        _memory.RecordFailure(SetupFailureKind.TopicNotPickable);
        await AskForLinkAsync(chatId, flow, null, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Cancel on the link prompt (<c>tb</c>): drops the link input and asks the topic question again in the wizard message.
    /// </summary>
    /// <param name="chatId">The private chat.</param>
    /// <param name="flow">The wizard's flow.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns><c>null</c>: the topic is still to be chosen.</returns>
    public async Task<TopicChoice?> OnBackAsync(long chatId, FlowState flow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(flow);
        await SpendAsync(chatId, flow, ct).ConfigureAwait(false);
        await AskAsync(chatId, Rebuild(flow), flow.WizardMessageId, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Use an existing topic from the Advanced options screen with no chat picked (spec §6.5): records
    /// <see cref="SetupFailureKind.TopicNotPickable"/> and asks for the topic's link, whose chat is then checked
    /// directly.
    /// </summary>
    /// <param name="chatId">The private chat.</param>
    /// <param name="wizardMessageId">The message the Advanced screen is on; later screens edit it.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>A task.</returns>
    public async Task AskForLinkAnywhereAsync(long chatId, int? wizardMessageId, CancellationToken ct)
    {
        var flow = new FlowState { Screen = OnboardingFlow.FlowScreen, WizardMessageId = wizardMessageId };
        flow.Params[AnyChatKey] = "1";
        _memory.RecordFailure(SetupFailureKind.TopicNotPickable);
        await AskForLinkAsync(chatId, flow, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The pasted topic link: it must parse and name the picked chat, by its <c>-100…</c> ID or its username; then the
    /// topic is checked with a typing action in its thread. A bad link is explained and asked for again.
    /// </summary>
    /// <param name="chatId">The private chat.</param>
    /// <param name="flow">The chat's flow, on the link step.</param>
    /// <param name="text">The pasted text.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>The choice when the topic passed; <c>null</c> when it asks again or the check failed.</returns>
    public async Task<TopicChoice?> OnLinkAsync(long chatId, FlowState flow, string text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (!TopicLink.TryParse(text, out var link))
        {
            await AskForLinkAsync(chatId, flow, _strings.Get("topic.badLink"), ct).ConfigureAwait(false);
            return null;
        }

        if (flow.Params.ContainsKey(AnyChatKey))
        {
            return await OnAnyChatLinkAsync(chatId, flow, link, ct).ConfigureAwait(false);
        }

        var picked = Rebuild(flow);
        try
        {
            var info = await _connection.RequireSender().GetChatAsync(picked.ChatId, ct).ConfigureAwait(false);
            var sameId = link.Chat.Identifier is { } id && id == picked.ChatId;
            var sameName = link.Chat.Username is { } name && info.Username is { } own
                && string.Equals(name.TrimStart('@'), own.TrimStart('@'), StringComparison.OrdinalIgnoreCase);
            if (!sameId && !sameName)
            {
                await AskForLinkAsync(chatId, flow, _strings.Get("topic.wrongChat"), ct).ConfigureAwait(false);
                return null;
            }

            var check = await _checker.CheckAsync(picked.ChatId, link.ThreadId, ct).ConfigureAwait(false);
            if (!check.Passed)
            {
                return await FailAsync(chatId, flow, check.Message, ct).ConfigureAwait(false);
            }

            flow.Params.Remove(InputKey);
            _memory.SaveFlow(chatId, flow);
            return new TopicChoice(check, link.ThreadId, flow.WizardMessageId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TgJelly could not check the topic of {Title}", picked.Title);
            return await FailAsync(chatId, flow, null, ct).ConfigureAwait(false);
        }
    }

    /// <summary>A link with no picked chat: the link's own chat is checked, with a typing action in its topic.</summary>
    private async Task<TopicChoice?> OnAnyChatLinkAsync(long chatId, FlowState flow, TopicLink link, CancellationToken ct)
    {
        try
        {
            var check = await _checker.CheckAsync(link.Chat, link.ThreadId, ct).ConfigureAwait(false);
            if (!check.Passed || !check.IsForum)
            {
                return await FailAsync(chatId, flow, check.Passed ? _strings.Get("destination.notForum") : check.Message, ct).ConfigureAwait(false);
            }

            flow.Params.Remove(InputKey);
            flow.Params.Remove(AnyChatKey);
            _memory.SaveFlow(chatId, flow);
            return new TopicChoice(check, link.ThreadId, flow.WizardMessageId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TgJelly could not check the chat of a topic link");
            return await FailAsync(chatId, flow, null, ct).ConfigureAwait(false);
        }
    }

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static void ClearPrompt(FlowState flow)
    {
        flow.PickerRequestIds = [];
        flow.PromptMessageId = null;
        flow.PromptSentAt = null;
        flow.Params.Remove(PromotionKey);
    }

    /// <summary>The picked forum, rebuilt from the flow (it passed the check before the question was asked).</summary>
    private static ChatCheckResult Rebuild(FlowState flow)
        => new(
            true,
            HealthStatus.Ok,
            null,
            long.Parse(flow.Params[OnboardingFlow.ChatIdKey], CultureInfo.InvariantCulture),
            flow.Params[OnboardingFlow.TitleKey],
            Enum.TryParse<ChatKind>(flow.Params.GetValueOrDefault(OnboardingFlow.KindKey), out var kind) ? kind : ChatKind.Supergroup,
            true,
            flow.Params.GetValueOrDefault(OnboardingFlow.PhotosBlockedKey) == "1");

    private async Task<bool> CanManageTopicsAsync(long forumId, CancellationToken ct)
    {
        var sender = _connection.RequireSender();
        var member = await sender.GetChatMemberAsync(forumId, _connection.BotId, ct).ConfigureAwait(false);
        return member is ChatMemberOwner || member is ChatMemberAdministrator { CanManageTopics: true };
    }

    private async Task<TopicChoice?> CreateTopicAsync(long chatId, FlowState flow, ChatCheckResult check, CancellationToken ct)
    {
        int thread;
        try
        {
            thread = await _connection.RequireSender().CreateForumTopicAsync(check.ChatId, TopicName, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TgJelly could not create a topic in {Title}", check.Title);
            return await FailAsync(chatId, flow, null, ct).ConfigureAwait(false);
        }

        return new TopicChoice(check, thread, flow.WizardMessageId);
    }

    /// <summary>
    /// Ends a live promotion prompt or link question when another answer is chosen: its picker is spent and its prompt
    /// deleted.
    /// </summary>
    private async Task SpendAsync(long chatId, FlowState flow, CancellationToken ct)
    {
        var prompt = flow.PromptMessageId;
        ClearPrompt(flow);
        flow.Params.Remove(InputKey);
        flow.Params.Remove(OnboardingFlow.CreatingKey);
        _memory.SaveFlow(chatId, flow);
        await DeleteAsync(chatId, prompt, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks for the topic's link inline, with Cancel (back to Advanced options without a picked chat, otherwise to the
    /// topic question). The first ask edits the wizard message; a repeat after a problem is a new message, which later
    /// steps then edit.
    /// </summary>
    private async Task AskForLinkAsync(long chatId, FlowState flow, string? problem, CancellationToken ct)
    {
        flow.Params[InputKey] = LinkInput;
        _memory.SaveFlow(chatId, flow);
        var html = problem is null ? string.Empty : _formatter.Escape(problem) + "\n\n";
        html += _formatter.Escape(_strings.Get("topic.linkPrompt")) + ButtonGuide.Lines(_strings, _formatter, ("input.cancel", "input.cancel.about"));
        var cancel = Button("input.cancel", flow.Params.ContainsKey(AnyChatKey) ? "ox" : "tb");
        var screen = new Screen(html, new InlineKeyboardMarkup(cancel));
        flow.WizardMessageId = await _screens.ShowAsync(chatId, screen, problem is null ? flow.WizardMessageId : null, ct).ConfigureAwait(false);
        _memory.SaveFlow(chatId, flow);
    }

    /// <summary>
    /// A failed promotion, check or creation (spec §6.2 step 3): records <see cref="SetupFailureKind.TopicFailed"/> and
    /// edits the wizard to <c>[General topic] [Advanced options]</c>. General still works on the same wizard.
    /// </summary>
    private async Task<TopicChoice?> FailAsync(long chatId, FlowState flow, string? message, CancellationToken ct)
    {
        var checking = flow.CheckingMessageId;
        var prompt = flow.PromptMessageId;
        ClearPrompt(flow);
        flow.Params.Remove(InputKey);
        flow.CheckingMessageId = null;
        _memory.SaveFlow(chatId, flow);
        await DeleteAsync(chatId, checking, ct).ConfigureAwait(false);
        await DeleteAsync(chatId, prompt, ct).ConfigureAwait(false);

        // Without a picked chat there is no General topic to fall back to.
        var anyChat = flow.Params.Remove(AnyChatKey);
        var buttons = new InlineKeyboardMarkup(anyChat ? [Button("onboard.advanced", "ox")] : new[] { Button("topic.general", "tg"), Button("onboard.advanced", "ox") });
        var html = _formatter.Escape(string.IsNullOrEmpty(message) ? _strings.Get("topic.failed") : message);
        flow.WizardMessageId = await _screens.ShowAsync(chatId, new Screen(html, buttons), flow.WizardMessageId, ct).ConfigureAwait(false);
        _memory.SaveFlow(chatId, flow);
        _memory.RecordFailure(SetupFailureKind.TopicFailed);
        return null;
    }

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
            _logger.LogWarning(ex, "TgJelly could not delete a topic step message");
        }
    }

    private InlineKeyboardButton Button(string textKey, string action)
        => Buttons.Callback(_strings, textKey, CallbackData.Format(action));
}
