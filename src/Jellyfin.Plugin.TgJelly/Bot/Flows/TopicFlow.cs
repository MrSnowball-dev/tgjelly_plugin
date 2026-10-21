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
using Telegram.Bot.Types.ReplyMarkups;

namespace Jellyfin.Plugin.TgJelly.Bot.Flows;

/// <summary>
/// The forum step of onboarding (spec §6.2 step 3, §6.5 "Use an existing topic"): where in the forum to post, all in
/// the wizard message. Each handler returns the <see cref="TopicChoice"/> once a topic is chosen, and <c>null</c> while
/// it still waits (for the promotion or for the link) or after it ended with its own message. Steps key off their own
/// markers in the chat's flow, never off <see cref="FlowState.Screen"/> alone.
/// </summary>
public sealed class TopicFlow
{
    private const string TopicKey = "topic";
    private const string PromotionKey = "promotion";

    /// <summary>v0.1's marker of a pending promotion through the chat picker, which also left <c>creating</c> set.</summary>
    private const string LegacyPromotionKey = "promotionRequest";
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
    /// <param name="logger">The logger.</param>
    public TopicFlow(BotConnection connection, BotMemory memory, ScreenSender screens, ChatCheckRetrier retrier, DestinationChecker checker, StringTable strings, Formatter formatter, ILogger<TopicFlow> logger)
    {
        _connection = connection;
        _memory = memory;
        _screens = screens;
        _retrier = retrier;
        _checker = checker;
        _strings = strings;
        _formatter = formatter;
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
    /// Whether a flow is on the topic-promotion screen, waiting for the bot to be allowed to manage topics. The screen
    /// has no expiry (spec §6.2 step 3).
    /// </summary>
    /// <param name="flow">The chat's flow.</param>
    /// <returns><c>true</c> when it is on the topic step and waits for the promotion.</returns>
    public static bool AwaitsPromotion(FlowState flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return flow.Params.ContainsKey(TopicKey) && flow.Params.TryGetValue(PromotionKey, out var promotion) && promotion == "1";
    }

    /// <summary>
    /// Asks where to post: <c>[General topic] [Create a "Jellyfin" topic] [Use an existing topic…]</c>, as the wizard
    /// message (new, or edited when <paramref name="wizardMessageId"/> is given).
    /// </summary>
    /// <param name="privateChatId">The private chat.</param>
    /// <param name="check">The passing check of the forum.</param>
    /// <param name="wizardMessageId">The wizard message to edit, or <c>null</c> to send a new one.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>A task.</returns>
    public async Task AskAsync(long privateChatId, ChatCheckResult check, int? wizardMessageId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(check);
        var flow = new FlowState { Screen = OnboardingFlow.FlowScreen };
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
    public Task<TopicChoice?> OnGeneralAsync(long chatId, FlowState flow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(flow);
        Spend(chatId, flow);
        return Task.FromResult<TopicChoice?>(new TopicChoice(Rebuild(flow), 0, flow.WizardMessageId));
    }

    /// <summary>
    /// Create a "Jellyfin" topic (<c>tc</c>): creates it when the bot can manage topics, otherwise edits the wizard to
    /// the promotion screen and waits (spec §6.2 step 3). A second press while a creation runs does nothing; the
    /// promotion screen has no Create button.
    /// </summary>
    /// <param name="chatId">The private chat.</param>
    /// <param name="flow">The wizard's flow.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>The choice once the topic exists; <c>null</c> while the promotion is awaited or after a failure.</returns>
    public async Task<TopicChoice?> OnCreateAsync(long chatId, FlowState flow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(flow);

        // A v0.1 flow caught mid-promotion at the upgrade: its creating marker belongs to the old chat-picker
        // promotion, which can no longer finish, so it is dropped and Create starts afresh.
        if (flow.Params.Remove(LegacyPromotionKey))
        {
            flow.Params.Remove(OnboardingFlow.CreatingKey);
        }

        if (flow.Params.ContainsKey(OnboardingFlow.CreatingKey))
        {
            return null;
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

        flow.Params.Remove(OnboardingFlow.CreatingKey);
        flow.Params[PromotionKey] = "1";
        await ShowPromotionAsync(chatId, flow, null, ct).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// The bot may have been promoted in the flow's forum: its <c>my_chat_member</c> made it an admin there, a group
    /// <c>/start@bot</c> came from there, or Check again (<c>tk</c>) was pressed. The wizard shows "Checking…" while
    /// the forum and the bot's rights are re-checked with retries; with the rights the topic is created, without them
    /// the promotion screen says so and keeps waiting.
    /// </summary>
    /// <param name="chatId">The private chat.</param>
    /// <param name="flow">The chat's flow, on the promotion screen.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>The choice once the topic exists; <c>null</c> while it still waits or after a failure.</returns>
    public async Task<TopicChoice?> OnPromotedAsync(long chatId, FlowState flow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var check = Rebuild(flow);
        var checking = new Screen(_strings.Get("onboard.checking", ("title", _formatter.Escape(check.Title))), null);
        flow.WizardMessageId = await _screens.ShowAsync(chatId, checking, flow.WizardMessageId, ct).ConfigureAwait(false);
        _memory.SaveFlow(chatId, flow);

        ChatCheckResult fresh;
        var canManage = false;
        try
        {
            fresh = await _retrier.CheckAsync(check.ChatId, retry: true, ct).ConfigureAwait(false);
            if (fresh.Passed)
            {
                canManage = await _retrier.UntilAsync(() => CanManageTopicsAsync(check.ChatId, ct), ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TgJelly could not check {Title} after the topic promotion", check.Title);
            return await FailAsync(chatId, flow, null, ct).ConfigureAwait(false);
        }

        if (!fresh.Passed)
        {
            return await FailAsync(chatId, flow, fresh.Message, ct).ConfigureAwait(false);
        }

        if (!canManage)
        {
            await ShowPromotionAsync(chatId, flow, _strings.Get("topic.stillNoRights", ("title", check.Title)), ct).ConfigureAwait(false);
            return null;
        }

        flow.Params.Remove(PromotionKey);
        flow.Params[OnboardingFlow.CreatingKey] = "1";
        _memory.SaveFlow(chatId, flow);
        return await CreateTopicAsync(chatId, flow, fresh, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The bot's rights changed in a chat other than the flow's forum while the promotion screen waits: the screen says
    /// so and keeps waiting.
    /// </summary>
    /// <param name="chatId">The private chat.</param>
    /// <param name="flow">The chat's flow, on the promotion screen.</param>
    /// <param name="other">The chat the rights changed in.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns>A task.</returns>
    public Task OnOtherChatAsync(long chatId, FlowState flow, Chat other, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(other);
        var name = string.IsNullOrEmpty(other.Title) ? Text(other.Id) : other.Title;
        var note = _strings.Get("topic.otherGroup", ("other", name), ("title", Rebuild(flow).Title));
        return ShowPromotionAsync(chatId, flow, note, ct);
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
        Spend(chatId, flow);
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
        Spend(chatId, flow);
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
    /// Ends a waiting promotion screen or link question when another answer is chosen.
    /// </summary>
    private void Spend(long chatId, FlowState flow)
    {
        flow.Params.Remove(PromotionKey);
        flow.Params.Remove(InputKey);
        flow.Params.Remove(OnboardingFlow.CreatingKey);
        _memory.SaveFlow(chatId, flow);
    }

    /// <summary>
    /// The promotion screen (spec §6.2 step 3), in the wizard message: Allow topic creation opens Telegram's group
    /// chooser asking for the right to manage topics, then Check again, General topic and Advanced options. A note
    /// (rights still missing, or changed in another chat) goes above the explanation of the buttons.
    /// </summary>
    private async Task ShowPromotionAsync(long chatId, FlowState flow, string? note, CancellationToken ct)
    {
        var rows = new[]
        {
            new[] { Buttons.Url(_strings, "topic.allow", BotLinks.PromoteForTopics(_connection.BotUsername)) },
            new[] { Button("topic.checkAgain", "tk") },
            new[] { Button("topic.general", "tg") },
            new[] { Button("onboard.advanced", "ox") },
        };
        var html = _strings.Get("topic.allowPrompt", ("title", _formatter.Escape(Rebuild(flow).Title)));
        if (note is not null)
        {
            html += "\n\n" + _formatter.Escape(note);
        }

        html += ButtonGuide.Lines(
            _strings,
            _formatter,
            ("topic.allow", "topic.allow.about"),
            ("topic.checkAgain", "topic.checkAgain.about"),
            ("topic.general", "topic.general.about"),
            ("onboard.advanced", "onboard.advanced.about"));
        flow.WizardMessageId = await _screens.ShowAsync(chatId, new Screen(html, new InlineKeyboardMarkup(rows)), flow.WizardMessageId, ct).ConfigureAwait(false);
        _memory.SaveFlow(chatId, flow);
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
    /// edits the wizard to <c>[General topic] [Advanced options]</c>, each explained. General still works on the same
    /// wizard.
    /// </summary>
    private async Task<TopicChoice?> FailAsync(long chatId, FlowState flow, string? message, CancellationToken ct)
    {
        flow.Params.Remove(PromotionKey);
        flow.Params.Remove(InputKey);

        // Without a picked chat there is no General topic to fall back to.
        var anyChat = flow.Params.Remove(AnyChatKey);
        var buttons = new InlineKeyboardMarkup(anyChat ? [Button("onboard.advanced", "ox")] : new[] { Button("topic.general", "tg"), Button("onboard.advanced", "ox") });
        var html = _formatter.Escape(string.IsNullOrEmpty(message) ? _strings.Get("topic.failed") : message)
            + (anyChat
                ? ButtonGuide.Lines(_strings, _formatter, ("onboard.advanced", "onboard.advanced.about"))
                : ButtonGuide.Lines(_strings, _formatter, ("topic.general", "topic.general.about"), ("onboard.advanced", "onboard.advanced.about")));
        flow.WizardMessageId = await _screens.ShowAsync(chatId, new Screen(html, buttons), flow.WizardMessageId, ct).ConfigureAwait(false);
        _memory.SaveFlow(chatId, flow);
        _memory.RecordFailure(SetupFailureKind.TopicFailed);
        return null;
    }

    private InlineKeyboardButton Button(string textKey, string action)
        => Buttons.Callback(_strings, textKey, CallbackData.Format(action));
}
