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
/// Chats the bot was added to outside the setup flow (spec §6.5, "Detected chats"): recording them from
/// <c>my_chat_member</c>, the list under "Chats added outside setup (n)", and setting one up through the onboarding
/// continuation. A detected chat receives nothing until it is set up.
/// </summary>
public sealed class DetectedChatFlow
{
    private readonly BotMemory _memory;
    private readonly SettingsStore _settings;
    private readonly DestinationChecker _checker;
    private readonly OnboardingFlow _onboarding;
    private readonly ScreenSender _screens;
    private readonly MenuScreens _menus;
    private readonly StringTable _strings;
    private readonly Formatter _formatter;
    private readonly ILogger<DetectedChatFlow> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DetectedChatFlow"/> class.
    /// </summary>
    /// <param name="memory">The bot's persisted memory.</param>
    /// <param name="settings">The settings store.</param>
    /// <param name="checker">Checks a detected chat once before it is set up.</param>
    /// <param name="onboarding">The onboarding flow that continues after the check.</param>
    /// <param name="screens">Shows screens.</param>
    /// <param name="menus">The menu screens.</param>
    /// <param name="strings">The string table.</param>
    /// <param name="formatter">The formatter, for escaping.</param>
    /// <param name="logger">The logger.</param>
    public DetectedChatFlow(BotMemory memory, SettingsStore settings, DestinationChecker checker, OnboardingFlow onboarding, ScreenSender screens, MenuScreens menus, StringTable strings, Formatter formatter, ILogger<DetectedChatFlow> logger)
    {
        _memory = memory;
        _settings = settings;
        _checker = checker;
        _onboarding = onboarding;
        _screens = screens;
        _menus = menus;
        _strings = strings;
        _formatter = formatter;
        _logger = logger;
    }

    /// <summary>
    /// Follows the bot's own membership change: a group, supergroup or channel it joined is recorded unless it is a
    /// destination or the person who added it has a chat picker waiting; one it left is forgotten. A person who
    /// blocks the bot stops being a manager, and one who unblocks it is a manager again (spec §6.5).
    /// </summary>
    /// <param name="update">The membership change.</param>
    public void OnChatMember(ChatMemberUpdated update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var chat = update.Chat;
        var joined = update.OldChatMember.Status is ChatMemberStatus.Left or ChatMemberStatus.Kicked
            && update.NewChatMember.Status is ChatMemberStatus.Member or ChatMemberStatus.Administrator;
        var gone = update.NewChatMember.Status is ChatMemberStatus.Left or ChatMemberStatus.Kicked;

        if (chat.Type == ChatType.Private)
        {
            if (gone)
            {
                _memory.RemoveManager(chat.Id);
            }
            else if (joined)
            {
                _memory.TouchManager(chat.Id);
            }

            return;
        }

        if (gone)
        {
            _memory.RemoveDetected(chat.Id);
        }
        else if (joined
            && Kind(chat.Type) is { } kind
            && _settings.Current.Destinations.All(d => d.ChatId != chat.Id)
            && _memory.GetFlow(update.From.Id) is not { PickerRequestIds.Count: > 0 })
        {
            _memory.RecordDetected(chat.Id, string.IsNullOrEmpty(chat.Title) ? chat.Id.ToString(CultureInfo.InvariantCulture) : chat.Title, kind);
        }
    }

    /// <summary>
    /// Builds the list of detected chats, "Detected: <i>Title</i>, set it up?" with Set up and Dismiss for each. With
    /// nothing left to offer, the main menu.
    /// </summary>
    /// <param name="page">The zero-based page.</param>
    /// <returns>The screen.</returns>
    public Screen List(int page)
    {
        var chats = _memory.VisibleDetected(_settings.Current);
        if (chats.Count == 0)
        {
            return _menus.Main(_settings.Current.Destinations.Length, 0);
        }

        var shown = Paging.Page(chats, page);
        var lines = shown.Select(c => _strings.Get("detected.line", ("title", "<b>" + _formatter.Escape(c.Title) + "</b>")));
        var rows = new List<InlineKeyboardButton[]>();
        foreach (var chat in shown)
        {
            var title = MenuScreens.Label(chat.Title);
            var id = chat.ChatId.ToString(CultureInfo.InvariantCulture);
            rows.Add(
            [
                InlineKeyboardButton.WithCallbackData(_strings.Get("detected.setUp", ("title", title)), CallbackData.Format("xs", id)),
                InlineKeyboardButton.WithCallbackData(_strings.Get("detected.dismiss", ("title", title)), CallbackData.Format("xd", id)),
            ]);
        }

        if (Paging.NavRow(page, chats.Count, p => CallbackData.Format("xl", p.ToString(CultureInfo.InvariantCulture)), _strings.Get("menu.prev"), _strings.Get("menu.next")) is { } nav)
        {
            rows.Add(nav);
        }

        rows.Add([InlineKeyboardButton.WithCallbackData(_strings.Get("menu.back"), CallbackData.Format("m"))]);
        var html = "<b>" + _formatter.Escape(_strings.Get("detected.title")) + "</b>\n\n" + string.Join('\n', lines);
        return new Screen(html, new InlineKeyboardMarkup(rows));
    }

    /// <summary>
    /// Forgets a detected chat and shows the rest of the list.
    /// </summary>
    /// <param name="chatId">The detected chat.</param>
    /// <returns>The screen.</returns>
    public Screen Dismiss(long chatId)
    {
        _memory.RemoveDetected(chatId);
        return List(0);
    }

    /// <summary>
    /// Sets up a detected chat: one check, without retries, then the onboarding continues at the topic question or the
    /// preset step (spec §6.5), editing the list message.
    /// </summary>
    /// <param name="privateChatId">The private chat with the person who pressed Set up.</param>
    /// <param name="chatId">The detected chat.</param>
    /// <param name="messageId">The list message to turn into the wizard.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <returns><c>false</c> when the chat is no longer detected, so the button has expired.</returns>
    public async Task<bool> SetUpAsync(long privateChatId, long chatId, int messageId, CancellationToken ct)
    {
        if (!_memory.IsDetected(chatId))
        {
            return false;
        }

        ChatCheckResult check;
        try
        {
            check = await _checker.CheckAsync(chatId, 0, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TgJelly could not check the detected chat {Chat}", chatId);
            var back = new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData(_strings.Get("menu.back"), CallbackData.Format("xl", "0")));
            await _screens.ShowAsync(privateChatId, new Screen(_formatter.Escape(_strings.Get("token.unreachable")), back), messageId, ct).ConfigureAwait(false);
            return true;
        }

        await _onboarding.ContinueAfterCheckAsync(privateChatId, check, 0, messageId, ct).ConfigureAwait(false);
        return true;
    }

    private static ChatKind? Kind(ChatType type) => type switch
    {
        ChatType.Group => ChatKind.Group,
        ChatType.Supergroup => ChatKind.Supergroup,
        ChatType.Channel => ChatKind.Channel,
        _ => null,
    };
}
