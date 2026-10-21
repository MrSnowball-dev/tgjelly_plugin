# TgJelly Plan 6: Inline-only bot UX and dashboard dedupe — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the owner's post-v0.1 fixes as v0.2:
- the bot uses only inline buttons, with no reply keyboards and no ForceReply;
- the Events screen is grouped into Media / Security / Server submenus instead of pages;
- every bot screen explains what each of its buttons does;
- bot buttons and headings carry custom emoji icons from the `TgAndroidIcons` pack;
- the dashboard shows the bot's identity once.

**Architecture:**
- **Onboarding.** The reply-keyboard picker becomes an inline prompt message. It has two URL buttons, `t.me/<bot>?startchannel…` and `?startgroup=add`, which open Telegram's own chat chooser.
  - **Correlation.** The bot learns the choice from the `my_chat_member` update that the add produces. It matches the update to the private flow of `my_chat_member.from.id`. A group `/start@<bot> <payload>` from that same user counts too.
  - **One message.** The prompt *is* the wizard message: "Checking…", the topic question, the presets and Done are all edits of it.
- **Promotion and typed input.**
  - Topic promotion uses the same mechanism: an inline `?startgroup=topics&admin=manage_topics` link, then `my_chat_member` (member → administrator) in the flow's forum, or a **Check again** button.
  - The two typed inputs, the public URL and the topic link, become inline prompts with a Cancel button. The text routing that exists today takes the next message.
- **Dashboard.** It is a string-and-render change in `tgjelly.js` plus one status string on the server.

**Tech Stack:**
- C# / .NET 9 + .NET 10 (unchanged), Telegram.Bot 22.10.3.3;
- xunit.v3, NSubstitute, `Microsoft.Extensions.TimeProvider.Testing`;
- Node 22 (`node --test`) for the page;
- Python 3 stdlib for the smoke test.

**Spec:** `docs/superpowers/specs/2026-10-16-tgjelly-v1-design.md` (rev. 2; Task 1 amends it to rev. 3). This plan reverses D6 (the native `request_chat` picker) at the owner's request (2026-10-21). It implements the amended §6.2, §6.3, §6.5 and §6.6, plus §10.3 `flows`, §14.1, §14.3 and §14.4. Plans 1–5 are merged into `main`, and their Global Constraints still apply.

**Deliberate deviations and decisions (so reviewers don't flag them):**
- **Why no `request_chat`.** Bot API 10.3 has no `request_chat` on `InlineKeyboardButton`. This was checked against the live docs and Telegram.Bot 22.10.3.3, so the owner's "inline only" rule means deep links. They open the same native chooser.
- **Correlating the add.**
  - **Who.** The add is matched by `my_chat_member.from.id` to that user's private flow. A private chat's ID equals its user's ID. Two admins setting up at once each match only their own adds.
  - **What counts as an add.** The new status must be member, administrator, or restricted with `is_member`, **and** one of two things holds:
    - the bot was left or kicked before (a fresh add), or
    - the new status is administrator (rights granted in a chat the bot was already in, e.g. `startchannel` on a channel it's in).
  - **Detected chats.** A matched add is not recorded as a detected chat.
- **The bot already in the chat.** No `my_chat_member` arrives in that case, so there are three ways in:
  1. the prompt lists up to 3 detected chats that aren't set up yet as `[Set up <Title>]` (existing `xs` callback);
  2. a group `/start@<bot> <payload>` from the prompt's user counts as choosing that group;
  3. Advanced → Use an existing topic still works.

  Whether clients send `/start@bot payload` into a group the bot is already in is **unverified**. Task 1 records it as an open question (§15 Q10) and adds a §14.4 checklist item. If clients don't send it, nothing breaks.
- **Prompt lifetime.**
  - **Expiry.** The chat prompt expires after `BotMemory.PromptTtl` (10 min, unchanged). When it does, the tick edits the prompt to "That setup prompt expired." `[Add destination]` and records `PromptExpired` (existing).
  - **Promotion.** The topic-promotion screen has **no** expiry. `[Check again]` works at any time, and the `my_chat_member` match for it is restricted to the flow's own forum. The 24 h flow TTL still applies.
- **Try again re-checks the failed chat.** A failed check now keeps the chat it checked in the spent flow. `[Try again]` re-checks that chat once (no retries), in place, and continues. Without one, it sends a fresh prompt. The reason: after an inline add, fixing the bot's rights in a group may produce no `my_chat_member`, so a fresh prompt couldn't see the chat again.
- **Stale v0.1 clients.**
  - A tap on a stale v0.1 reply keyboard arrives as one of these texts: `📢 Choose a channel`, `👥 Choose a group`, `💬 Right here`, `Cancel`. A `chat_shared` from a stale picker also arrives.
  - Either one gets "That menu expired" carrying `ReplyKeyboardRemove`, then the menu. `ReplyKeyboardRemove` is the only non-inline markup left in the code; Telegram needs it to clear a keyboard the client still shows.
  - The four texts are hard-coded in `OnboardingFlow.LegacyReplyTexts`. Only English shipped in v0.1.
- **State document.** Removed from the state document: `FlowState.PickerRequestIds`, `PromptMessageId` and `CheckingMessageId`, and `TelegramSection.LastRequestId`. System.Text.Json ignores unknown members on read (`JsonDefaults` sets no `UnmappedMemberHandling`), so a v0.1 `state.json` still loads. A test pins this.
- **Typed-input prompts.**
  - **Where they show.** A typed-input prompt edits the message whose button asked for it.
  - **Invalid input.** The prompt is sent again as a **new** message under the user's text. For the topic link, that new message becomes the wizard message, so later steps edit the newest message.
  - **Cancel.** Cancel returns to the screen that asked:
    - `sp`, the Public URL screen;
    - `tb`, the topic question (new);
    - `ox`, Advanced options, when no chat was picked.
- **Events submenus.**
  - **Layout.** The group chooser shows `Media n/5`, `Security n/6` and `Server n/8`. The counts come from `EventCatalog.Groups` and `EventCatalog.AllKeys`.
  - **Toggling.** A toggle redraws its own group.
  - **Done.** On the onboarding Custom step, Done sits only on the chooser.
  - **Old buttons.** An old paged button `1|de|<id>|<page>` opens the chooser, because extra arguments are ignored.
  - **Other lists still page:** destinations, libraries and detected chats.
- **Dashboard.**
  - **Status.** It says "Connected to Telegram" (`web.status.connected`), with no username. The bot's `/status` screen keeps "Connected as @x" (`connection.Connected`), because the bot has no Bot section.
  - **Connect card.** It closes after a successful connect. Only the "webhook removed" note stays, and only when a webhook was removed.
  - **Bot section.** It shows `@x` once. The access line and the link label no longer repeat it.
- **Out of scope:**
  - README screenshots;
  - the §14.4 manual checklist run (owner);
  - pushing the `v0.2` tag (owner-gated, Task 10).

## Global Constraints

- Telegram.Bot **22.10.3.3**, pinned exactly; its API is called only from `ITelegramSender` and `BotSupervisor` (D2).
- Both builds compile and pass tests: net9.0 (1.x) and net10.0 (2.x) (D3).
- Callback data is `1|action|arg…`, at most 64 UTF-8 bytes (`CallbackData.Format`); every new action is added to `FlowEngine.Actions` (the length test reads it).
- No `ReplyKeyboardMarkup`, `KeyboardButton` or `ForceReplyMarkup` anywhere in `src/`. `ReplyKeyboardRemove` appears only in the stale-v0.1 handler.
- **Every button is explained on its screen** (owner, 2026-10-21). The wizard screens are:
  - the prompt;
  - the topic question;
  - the promotion screen;
  - the presets;
  - the Custom chooser;
  - the failure screen;
  - the expired prompt;
  - the typed-input prompts;
  - the events chooser.

  On each of them, the text ends with one line per button, in button order, built by `ButtonGuide.Lines`: `<b>Label</b> — what it does`. Back (`Back`) is the one exception. The label in the line comes from the same string key as the button, so the two can't drift apart.

  Task 7 extends the rule to every other bot screen and to the problem notification. List screens get one shared "Tap a … to …" line instead of a line per item, and Prev, Next and Back get no line.
- **Icons** (owner, 2026-10-21):
  - Every button whose label is a string key is built with `Buttons.Callback`/`Buttons.Url` (Task 2), and every screen heading with `Icons.Heading`.
  - A new action label or heading gets an icon from `TgAndroidIcons`, entered in `Icons.ForLabel`/`ForHeading`. Its text carries no leading emoji.
  - State marks keep their plain emoji and get no icon: ✅/▫️/⬜, 🔘/⚪, 🔔/🔕 per group, and the health badges.
  - Custom emoji appear only when the bot owner has Telegram Premium.
  - Notification posts to destinations get no icons, because channels can't show them.
- Every user-visible string lives in `Strings/en.json` and is read through `StringTable`; every key used is listed in `tests/…/Strings/StringKeys.cs` (`StringKeys.All`), and keys no longer used are removed from both. Telegram HTML is built with `Formatter.Escape` for every value.
- Owner decisions stay as they are:
  - no pairing or claim lock, trust BotFather (D5);
  - preset order `[Everything] [New media] [Admin alerts] [Custom…]` for every chat type;
  - an automatic test message for every new destination;
  - manual setup only under Advanced, after a failure.
- Persisted documents: fields may be removed, never renamed or retyped. `PluginConfiguration` XML is not touched.
- In groups and channels, the bot acts only on:
  - `my_chat_member`,
  - service messages it needs,
  - `/start@<its username> …` from a user (new; correlation only, never a reply in the group).
- Tests run as `export PATH="$HOME/.dotnet:$PATH" && dotnet test --project tests/Jellyfin.Plugin.TgJelly.Tests -c Release -f net10.0 -- --filter-class "*<Class>"`. The final task runs both TFMs.
- Commits: `git -c user.name="Daniil" -c user.email=igatorr19@gmail.com commit …`, message ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Never commit or log a bot token.

## Review Focus

1. **Two admins set up at the same time.** Each `my_chat_member` must land in the flow of its own `from.id`. An add by someone without a live prompt becomes a detected chat. *Pinned by* `TwoManagers_EachGetOnlyTheirOwnAdd` (Task 5).
2. **The bot is already in the chosen chat.** No `my_chat_member` arrives, so one of three ways must still work:
   - the detected rows on the prompt;
   - a group `/start@bot add`;
   - for a channel, rights re-granted (administrator → administrator).

   *Pinned by* `Start_ListsUpToThreeDetectedChats`, `GroupStart_ByThePromptsUser_CountsAsTheChoice` and `RightsGranted_InAChannelTheBotIsAlreadyIn_CountsAsTheChoice` (Task 5).
3. **Stale v0.1 UI on clients.** Three kinds of stale input must each get one "That menu expired" plus the menu, and must never crash or loop:
   - reply-keyboard texts;
   - `chat_shared`;
   - old `1|de|<id>|<page>` buttons.

   *Pinned by* `LegacyReplyInput_RemovesTheOldKeyboard_AndShowsTheMenu` (Task 5), `LegacyAllowTopicPick_IsExpired` (Task 6) and `OldPagedEventsButton_OpensTheGroups` (Task 3).
4. **Promotion done in a different group than the flow's forum.** The bot says so, keeps waiting, and creates nothing. *Pinned by* `Promotion_InAnotherGroup_SaysSo_AndKeepsWaiting` (Task 6).
5. **A matched add redelivered after a restart.** The poll loop can redeliver a batch. The second copy must not start a second check or become a detected chat. *Pinned by* `RedeliveredAdd_IsHandledOnce` (Task 5).

---

### Task 1: Amend the spec to rev. 3

**Files:**
- Modify: `docs/superpowers/specs/2026-10-16-tgjelly-v1-design.md`

No code; the reviewer checks the spec against the decisions above. Every later task argues from this text.

- [ ] **Step 1: Header and intent.**
  - Bump the revision line to rev. 3 (2026-10-21, "inline-only bot UX after v0.1 testing").
  - In §1.1 item 3 and §1.3 item 1, replace "native chat picker" with "Telegram's own chat chooser, opened from the bot's inline buttons".
- [ ] **Step 2: Decision log.** Rewrite D6: "Destinations are added through inline `startchannel` / `startgroup` deep-link buttons, which open Telegram's chat chooser. The bot matches the resulting `my_chat_member` to the admin's flow. Manual methods are under Advanced and appear only after a failure." Why: "Owner's setup correction; rev. 3: inline buttons only, no reply keyboards (owner, 2026-10-21). `request_chat` exists only on reply keyboards."
- [ ] **Step 3: Platform facts.** In §4, add two facts:
  - **F41.** `https://t.me/<bot>?startgroup=<payload>` and `?startchannel&admin=<rights>` open the client's chat chooser and add the bot, with the rights for a channel. The bot receives `my_chat_member` with `from` = the user who added it. `admin=` on `startgroup` asks for those rights for the bot (`manage_topics` for topic promotion).
  - **F42 (unverified, §15 Q10).** After a `startgroup` add, the client sends `/start@<bot> <payload>` into the group from that user, possibly even when the bot is already a member.

  Keep F33 (it is still true) but add: "No longer used since rev. 3."
- [ ] **Step 4: Rewrite §6.2.** Keep the five-step structure and the passing rules of step 2 verbatim. Change the rest as follows.
  - **Step 1 (prompt).**
    - The prompt is one inline wizard message: "Where should notifications go?" plus the hint.
    - Buttons:
      - `Add to a channel` (URL, `BotLinks.StartChannel`);
      - `Add to a group` (URL, `BotLinks.StartGroup`);
      - `Right here` (callback);
      - up to 3 `[Set up <Title>]` rows for detected chats not yet set up;
      - `Cancel` (callback).
    - Sent new for `/start`. Edits the pressed message for Add destination, Add another place and Try again.
    - The flow stores `wizardMessageId`, `promptSentAt` and `await=chat`.
  - **Step 2 (check).**
    - **Trigger.** A `my_chat_member` whose `from.id` has a live prompt, and whose change counts as an add (rule in the deviations above). A group `/start@bot` from that user (F42) counts the same.
    - The wizard message is edited to "Checking *Title*…" and the check runs with the existing retries.
    - Delete the `ReplyKeyboardRemove` / `checkingMessageId` paragraph.
    - **On failure:** the wizard is edited to the explanation with `[Try again] [Advanced options]`. Try again re-checks the same chat once, and offers a fresh prompt only when there is no chat.
  - **Step 3 (forum), Create without rights.**
    - The wizard is edited to "To create a topic, I need permission to manage topics…" with these buttons:
      - `Allow topic creation` (URL `?startgroup=topics&admin=manage_topics`),
      - `[Check again]`,
      - `[General topic]`,
      - `[Advanced options]`.
    - **Promotion detected:** a `my_chat_member` → administrator in that forum from the same user, a group `/start@bot` there, or Check again finding `can_manage_topics`. The bot then creates the topic.
    - **Promotion in another group:** said on the wizard, and the bot keeps waiting.
    - **Rights still missing on Check again:** said on the wizard, and the bot keeps waiting.
    - No expiry for this screen.
  - **Delete** the sentence "Reply-keyboard prompts are separate, throwaway messages". The whole flow is one message.
  - **Stale input.**
    - A v0.1 reply-keyboard text or a `chat_shared` → "That menu expired" with `ReplyKeyboardRemove`, then the menu.
    - A prompt unanswered for 10 min → edited to "That setup prompt expired." `[Add destination]`. It counts as a setup failure.
- [ ] **Step 5: §6.3.**
  - Add the icon rule. Action buttons and screen headings show custom emoji from the `TgAndroidIcons` pack: `icon_custom_emoji_id` on buttons, and `<tg-emoji>` with the old emoji as fallback in headings.
    - Their labels carry no plain emoji.
    - State marks stay plain emoji.
    - Custom emoji need Telegram Premium on the bot owner's account; without it they don't show.
    - If Telegram rejects a message because of them, the bot resends it without icons and stops using them until it reconnects.
    - Notification posts get none.
  - Add the rule: "Every bot screen explains each of its buttons in its text, one line per button, `Label — what it does`. Lists of items or switches get one shared line saying what tapping an item does. Back, Prev and Next need no line."
  - Change the Events branch to "Events (Media · Security · Server, each its own screen)".
  - Change Public URL `[Type another]` to "(inline prompt with [Cancel], validated §8.3)".
  - Add a line: "Long lists of related switches are grouped into submenus (Events); long lists of items page (destinations, libraries, detected chats)."
- [ ] **Step 6: §6.4.**
  - Change the dashboard reminder text to "Anyone who can open this bot can manage notifications. Keep it restricted in BotFather."
  - Add the `/start@bot` exception to "In groups and channels the bot ignores every command and message": correlation only, and the bot never replies in the group.
- [ ] **Step 7: §6.5.**
  - The trigger "a picker check failed" becomes "a chat check failed".
  - In the Advanced options screen, remove the `Add to a group` and `Add to a channel` rows; they moved to the prompt.
  - "Use an existing topic" asks with an inline prompt with `[Cancel]`.
  - Detected chats exception: "the add matches a setup flow of `my_chat_member.from.id` (§6.2 step 2)" replaces "a picker flow is waiting for a `chat_shared`".
- [ ] **Step 8: §6.1 and §6.6.**
  - §6.1 result: "The connect card closes. The Bot section (§6.6) shows @MyJellyBot with [Open in Telegram]."
  - §6.6 item 1: "Connected" instead of "Connected as @x".
  - §6.6 item 2: "@username (shown only here), the access reminder, [Open in Telegram], [Change token], [Disconnect bot]".
- [ ] **Step 9: §10.3, §14 and §15.**
  - **§10.3** `flows` row: drop `promptMessageId`, `checkingMessageId` and `pickerRequestIds`; add "`params.await` (chat)".
  - **§14.1** FlowEngine bullets:
    - "`request_id` correlation, `chat_shared` before and after `my_chat_member`" becomes "`my_chat_member` correlation by `from.id` (two admins, adds by others, redelivery, rights re-grant, group `/start@bot`)";
    - "expired menus and pickers" becomes "expired menus and prompts, stale v0.1 reply keyboards";
    - add "events submenus".
  - **§14.2:** keep `chat_shared` (legacy) and add `my_chat_member` promotion and rights-grant fixtures.
  - **§14.3:** add an end-to-end step 7: "`/start setup` → find the `startchannel` URL button → push `my_chat_member` (channel, left → administrator, from the admin) → press New media → the destination exists, its test message arrived, and the wizard was edited to Done".
  - **§14.4:**
    - (a) becomes "the inline add links can still add the restricted bot to a channel and a group";
    - the "Native picker" line becomes "Inline add links" (channel, group, forum: General, a created topic, the Allow topic creation promotion, an existing topic via link; and a chat the bot is already in);
    - add "does a group receive `/start@bot add` when the bot is already a member (Q10)".
  - **§15:** add Q10 (F42) and mark Q3 "superseded by rev. 3".
- [ ] **Step 10: Commit.**

```bash
git add docs/superpowers/specs/2026-10-16-tgjelly-v1-design.md
git -c user.name="Daniil" -c user.email=igatorr19@gmail.com commit -m "docs: spec rev. 3, inline-only bot onboarding and events submenus

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Custom emoji icons on buttons and headings

**Files:**
- Create: `src/Jellyfin.Plugin.TgJelly/Bot/Screens/Icons.cs`
- Create: `src/Jellyfin.Plugin.TgJelly/Bot/Screens/Buttons.cs`
- Modify: `src/Jellyfin.Plugin.TgJelly/Bot/TelegramSender.cs` (`SendTextAsync`, `EditTextAsync`)
- Modify: every screen that builds a keyed button or a heading:
  - `MenuScreens.cs`, `DestinationScreens.cs`, `SettingsScreens.cs`, `StatusScreen.cs`, `AdvancedScreen.cs`;
  - `OnboardingFlow.cs`, `TopicFlow.cs`, `DetectedChatFlow.cs`;
  - `ManagerNotifier.cs`.
- Modify: `en.json` (labels and headings lose their leading emoji), `StringKeys.cs`
- Test: `tests/…/Bot/Screens/IconsTests.cs` (new), `MenuScreensTests.cs`, `DestinationScreensTests.cs`, `tests/…/Bot/TelegramSenderTests.cs`; any existing test that compares a label or heading text

**Interfaces:**
- Produces:
  - `internal static class Icons`:
    - one `public const string` per icon, holding its `custom_emoji_id` from the pack `TgAndroidIcons` ("Telegram Android Icons");
    - `static IReadOnlyDictionary<string, string> ForLabel`, mapping a label's string key to its icon;
    - `static IReadOnlyDictionary<string, (string Icon, string Fallback)> ForHeading`, mapping a heading's string key to its icon and its fallback emoji;
    - `static string Heading(StringTable strings, Formatter formatter, string key, params (string Name, object? Value)[] args)` returns `<tg-emoji emoji-id="{Icon}">{Fallback}</tg-emoji> ` + the escaped text, or just the escaped text for a key without an icon;
    - `static string StripHtml(string html)` replaces each `<tg-emoji emoji-id="…">X</tg-emoji>` with `X`.
  - `internal static class Buttons`:
    - `static InlineKeyboardButton Callback(StringTable strings, string labelKey, string data, params (string Name, object? Value)[] args)`;
    - `static InlineKeyboardButton Url(StringTable strings, string labelKey, string url)`.

    Both set the text from `strings.Get(labelKey, args)` and `IconCustomEmojiId = Icons.ForLabel.GetValueOrDefault(labelKey)`. Buttons whose text is not a string key, such as destination items, switches with ✅/▫️ and the 🔘/⚪ choices, keep `InlineKeyboardButton.WithCallbackData` and get no icon.
  - `TelegramSender` falls back when Telegram rejects the icons. When `SendTextAsync` or `EditTextAsync` fails with an `ApiRequestException` whose `ErrorCode` is 400 and whose message contains `emoji` (ignoring case), and the outgoing message has icons:
    - it logs one warning: "TgJelly's custom icons were rejected, so messages are sent without them. Custom emoji need Telegram Premium on the bot owner's account.";
    - it sets the instance's `_iconsBlocked`;
    - it retries once, with a cloned markup without `IconCustomEmojiId` and with `Icons.StripHtml` applied to the text.

    While `_iconsBlocked` is set, every send strips icons first. The flag lasts as long as the sender (until the bot reconnects). Any other error is rethrown unchanged.
- Icons. The numbers are positions in the pack, for reference. A key added by a later task is marked with that task; that task adds the map entry.

  | Const | ID | # | Label keys (`ForLabel`) | Heading keys (`ForHeading`, fallback) |
  |---|---|---|---|---|
  | `Menu` | `5875271289605722323` | 11 | `onboard.menu` | `menu.title` (🏠) |
  | `Chats` | `5891169510483823323` | 202 | `menu.destinations` | `menu.destinations.title` (📬) |
  | `Add` | `5775937998948404844` | 121 | `menu.add`, `onboard.addAnother`, `detected.setUp` | |
  | `Settings` | `5877260593903177342` | 10 | `menu.settings` | `settings.title` (⚙️) |
  | `Status` | `5877613700344450910` | 129 | `menu.status` | `status.title` (🩺) |
  | `Search` | `5874960879434338403` | 44 | `menu.detected` | `detected.title` (🔎) |
  | `Back` | `5875082500023258804` | 26 | `menu.back` | |
  | `Channel` | `5771695636411847302` | 409 | `onboard.channel` | |
  | `Group` | `5942877472163892475` | 420 | `onboard.group` | |
  | `Chat` | `5994297722574737553` | 203 | `onboard.here` | |
  | `Cancel` | `5778527486270770928` | 120 | `onboard.cancel`, `dest.btn.cancel`, `input.cancel` (Task 4) | |
  | `Key` | `6005570495603282482` | 179 | `topic.allow` | |
  | `Refresh` | `5845943483382110702` | 105 | `onboard.tryAgain`, `dest.btn.check`, `topic.checkAgain` (Task 6) | |
  | `Hash` | `5951584964305755220` | 451 | `topic.general` | |
  | `NewTopic` | `5883973610606956186` | 189 | `topic.create` | |
  | `Link` | `5877465816030515018` | 64 | `topic.existing`, `advanced.topic`, `settings.url` | `settings.url.title` (🔗) |
  | `Sliders` | `5875431869842985304` | 12 | `onboard.advanced` | |
  | `Toggles` | `5875033614705495771` | 14 | `onboard.preset.custom` | |
  | `Laptop` | `5967816500415827773` | 71 | `advanced.dashboard` | |
  | `Grid` | `6008258140108231117` | 402 | `onboard.preset.everything` | |
  | `Video` | `6005986106703613755` | 220 | `onboard.preset.newMedia`, `events.group.media` (Task 3) | |
  | `ShieldAlert` | `5775887550262546277` | 169 | `onboard.preset.adminAlerts` | |
  | `Lock` | `5879895758202735862` | 175 | `events.group.security` (Task 3) | |
  | `Server` | `5877485980901971030` | 57 | `events.group.server` (Task 3) | |
  | `Bell` | `5909201569898827582` | 405 | `dest.btn.events` | `dest.events.title` (🔔) |
  | `Download` | `5899757765743615694` | 23 | `settings.updates` | `settings.updates.title` (🔔) |
  | `Folder` | `5877332341331857066` | 77 | `dest.btn.libraries` | `dest.libraries.title` (📁) |
  | `BellOff` | `5909123362839335003` | 406 | `dest.btn.silent` | `dest.silent.title` (🔕) |
  | `Send` | `5875465628285931233` | 0 | `dest.btn.test` | |
  | `Block` | `5872829476143894491` | 107 | `dest.btn.pause` | |
  | `Play` | `5775981206319402773` | 125 | `dest.btn.resume` | |
  | `Trash` | `5879896690210639947` | 90 | `dest.btn.remove` | |
  | `Exit` | `5877341274863832725` | 97 | `dest.btn.removeLeave` | |
  | `Layers` | `5924720918826848520` | 58 | `dest.libraries.all` | |
  | `Check` | `5825794181183836432` | 46 | `onboard.customDone`, `settings.url.use` | |
  | `Pencil` | `5879841310902324730` | 95 | `settings.url.type` | |
  | `Erase` | `5967355281057779430` | 475 | `settings.url.clear` | |
  | `Hide` | `5952055319059239589` | 460 | `detected.dismiss` | |
  | `Open` | `5884343982816759327` | 43 | `notify.btn.open` | |
  | `Palette` | `5814690801665446789` | 352 | `settings.messages` | `settings.messages.title` (🎨) |

  On the Events group screen (Task 3), the heading's group name gets the group's icon: Video, Lock or Server, with the fallbacks 🎬, 🔐 and 🖥.
- Strings. These lose their leading emoji (and `‹`):

  | Key | New text |
  |---|---|
  | `menu.title` | `Menu` |
  | `menu.destinations` | `Destinations ({count})` |
  | `menu.add` | `Add destination` |
  | `menu.settings` | `Settings` |
  | `menu.status` | `Status` |
  | `menu.back` | `Back` |
  | `menu.destinations.title` | `Destinations` |
  | `status.title` | `Status` |
  | `settings.title` | `Settings` |
  | `settings.messages`, `settings.messages.title` | `Message style` |
  | `settings.updates`, `settings.updates.title` | `Update checks` |
  | `settings.url`, `settings.url.title` | `Public URL` |
  | `onboard.here` | `Right here` |
  | `topic.allow` | `Allow topic creation` |

  `onboard.channel` and `onboard.group` are rewritten in Task 5 (`Add to a channel`, `Add to a group`), without emoji. `menu.prev`/`menu.next` keep `‹ Prev`/`Next ›` (no icon; the pack has no matching left/right pair).

- [ ] **Step 1: Write the failing tests.**

```csharp
// IconsTests
[Fact] public void EveryIconKey_IsAString()              // each ForLabel and ForHeading key: Strings.Get(key) != key
[Fact] public void NoLabelOrHeadingWithAnIcon_StartsWithAnEmoji()
// the first rune of each ForLabel/ForHeading key's English text is not in U+1F000–U+1FAFF, U+2600–U+27BF or U+2B00–U+2BFF, and is not '‹'
[Fact] public void Heading_WrapsTheFallbackEmoji()
// Icons.Heading(strings, formatter, "menu.title") == "<tg-emoji emoji-id=\"5875271289605722323\">🏠</tg-emoji> Menu"
[Fact] public void StripHtml_LeavesTheFallback()        // "<b><tg-emoji emoji-id=\"1\">🏠</tg-emoji> Menu</b>" → "<b>🏠 Menu</b>"
// MenuScreensTests
[Fact] public async Task MainMenu_ButtonsAndHeadingCarryTheirIcons()
// labels == ["Destinations (2)", "Add destination", "Settings", "Status"]; icon_custom_emoji_id == [Chats, Add, Settings, Status];
// text starts "<b><tg-emoji emoji-id=\"5875271289605722323\">🏠</tg-emoji> Menu</b>"
// DestinationScreensTests
[Fact] public async Task Detail_ButtonsCarryTheirIcons()   // Events→Bell, Libraries→Folder, … Remove→Trash, Back→Back
[Fact] public async Task Switches_HaveNoIcons()           // the event and library switch rows have no icon_custom_emoji_id
// TelegramSenderTests
[Fact] public async Task RejectedIcons_AreDroppedAndResent_ThenSkipped()
// sendMessage answers Error(400, "Bad Request: custom emoji entities can't be used") then Ok(message), twice:
// the 2nd request has no icon_custom_emoji_id and its text has "🏠 Menu" (no tg-emoji); a later send goes out stripped on its first try
[Fact] public async Task OtherBadRequests_AreNotRetried()  // Error(400, "Bad Request: chat not found") → thrown; one request
[Fact] public async Task MessagesWithoutIcons_AreNotRetried() // an emoji-mentioning 400 for a message without icons → thrown; one request
```

- [ ] **Step 2: Run to verify they fail.** Filter: `*IconsTests`, `*MenuScreensTests`, `*DestinationScreensTests`, `*TelegramSenderTests`. Expected: FAIL (`Icons` missing).
- [ ] **Step 3: Implement.**
  - **New classes.** Write `Icons` and `Buttons` as specified above.
  - **Screens.** Every keyed button in the listed screens goes through `Buttons.Callback`/`Buttons.Url`, and every heading key in `ForHeading` goes through `Icons.Heading`. Screens keep their `<b>` around headings.
  - **`TelegramSender`.** Add the fallback as specified, with a private `bool HasIcons(string html, InlineKeyboardMarkup? markup)` and a private `InlineKeyboardMarkup? WithoutIcons(InlineKeyboardMarkup? markup)` that clones every button.
  - **Strings.** Apply the table. Update the existing tests that compare these labels or headings.
- [ ] **Step 4: Run** the whole suite on net10.0. Expected: PASS.
- [ ] **Step 5: Commit** with the message `feat: custom emoji icons on bot buttons and headings, with a plain fallback`.

---

### Task 3: Events submenus (destination screen and onboarding Custom)

**Files:**
- Create: `src/Jellyfin.Plugin.TgJelly/Bot/Screens/EventPicker.cs`
- Create: `src/Jellyfin.Plugin.TgJelly/Bot/Screens/ButtonGuide.cs`
- Create: `tests/Jellyfin.Plugin.TgJelly.Tests/Bot/Screens/ButtonGuideTests.cs`
- Modify: `tests/Jellyfin.Plugin.TgJelly.Tests/Fakes/BotApiCall.cs` (add `Explains`)
- Modify: `src/Jellyfin.Plugin.TgJelly/Bot/Screens/DestinationScreens.cs` (`HandleAsync` `de`/`det`, `Detail` events button, `Events`, `ToggleEvent`)
- Modify: `src/Jellyfin.Plugin.TgJelly/Bot/Flows/OnboardingFlow.cs` (`OnPresetAsync` Custom, `OnCustomToggleAsync`, `ShowCustomAsync`)
- Modify: `src/Jellyfin.Plugin.TgJelly/Bot/Screens/FlowEngine.cs` (`Actions`, route `ocg`)
- Modify: `src/Jellyfin.Plugin.TgJelly/Strings/en.json`, `tests/…/Strings/StringKeys.cs`
- Test: `tests/Jellyfin.Plugin.TgJelly.Tests/Bot/Screens/DestinationScreensTests.cs`, `tests/…/Bot/Flows/OnboardingFlowTests.cs`

**Interfaces:**
- Produces:
  - `internal static class ButtonGuide`, which later tasks use on every wizard screen:
    - `static string Lines(StringTable strings, Formatter formatter, params (string LabelKey, string AboutKey)[] buttons)` returns `"\n\n"` followed by one line per button, joined with `\n`. Each line is `"<b>" + Escape(strings.Get(LabelKey)) + "</b> — " + Escape(strings.Get(AboutKey))`.
    - `static string Line(Formatter formatter, string label, string about)` builds one line for a label that isn't a fixed key (the prompt's `Set up …` rows).
  - Test helper `BotApiCall.Explains(params string[] labels)`: for each label, the call's `text` contains `"<b>" + label + "</b> — "` (labels already HTML-escaped as sent).
  - `internal static class EventPicker` with two methods:
    - `static List<InlineKeyboardButton[]> GroupRows(StringTable strings, IReadOnlyCollection<string> selected, Func<string, string> openGroup)`: one row per `EventCatalog.Groups` entry, labelled `events.group` with `name` = `events.group.<g>`, `on` = the selected keys in that group, and `total` = the keys in it. The data is `openGroup(g)`.
    - `static List<InlineKeyboardButton[]> ToggleRows(StringTable strings, string group, IReadOnlyCollection<string> selected, Func<string, string> toggle)`: one row per key of that group in catalog order, labelled `DestinationScreens.Mark(on) + " " + dest.event.<key>`. The data is `toggle(key)`.
  - Callback actions:
    - `deg` `[id, group]`: a destination's group screen;
    - `ocg` `[group]`: the onboarding Custom group screen;
    - `ocg` with no argument: the onboarding Custom chooser.
  - Changed meanings:
    - `de` `[id]` is the destination's chooser; extra arguments are ignored;
    - `det` `[id, key]` now redraws the key's group;
    - `oc` `[key]` now redraws the key's group.
- Strings (new):

  | Key | Text |
  |---|---|
  | `events.group` | `{name} {on}/{total}` |
  | `events.group.media` | `Media` |
  | `events.group.security` | `Security` |
  | `events.group.server` | `Server` |
  | `events.choose` | `Pick a group to switch its events. The numbers show how many are on.` |
  | `events.group.media.about` | `new movies, episodes, albums, music videos and books` |
  | `events.group.security.about` | `failed and successful sign-ins, lockouts, user and password changes` |
  | `events.group.server.about` | `server start, updates, plugins, failed tasks and pending restarts` |
  | `onboard.customDone.about` | `add the chat with the events you switched on` |
  | `onboard.preset.everything.about` | `every event below` |
  | `onboard.preset.newMedia.about` | `only new movies, episodes, music and books` |
  | `onboard.preset.adminAlerts.about` | `security and server alerts, without successful sign-ins` |
  | `onboard.preset.custom.about` | `choose the events yourself` |

  The group lines on the choosers use the group name (`events.group.<g>`, without the count) as the label. The preset screen, which isn't otherwise changed, gains its four lines too, because it is part of the wizard.

- [ ] **Step 1: Write the failing tests** in `DestinationScreensTests`. Use the existing helpers and a destination with events `[media.movie, media.episodes, server.started]`:

```csharp
[Fact] public async Task Events_ShowsOneButtonPerGroup_WithCounts()
// click "1|de|<id>" → labels == ["Media 2/5", "Security 0/6", "Server 1/8", "Back"]
// data == ["1|deg|<id>|media", "1|deg|<id>|security", "1|deg|<id>|server", "1|d|<id>"]; text contains "Pick a group to switch its events."
// and call.Explains("Media", "Security", "Server"); text contains "Media</b> — new movies, episodes, albums, music videos and books"
[Fact] public async Task EventGroup_ShowsOnlyItsSwitches_AndBackToTheGroups()
// click "1|deg|<id>|security" → 6 switch rows, labels start "▫️ ", data "1|det|<id>|security.*" in catalog order, last data "1|de|<id>"
[Fact] public async Task EventSwitch_StaysOnItsGroup_AndRecomputesThePreset()
// click "1|det|<id>|server.task_failed" → 8 server rows, the task_failed row starts "✅ "; the saved destination's Events contain it; Preset == Custom
[Fact] public async Task OldPagedEventsButton_OpensTheGroups()
// click "1|de|<id>|1" → the same chooser as Events_ShowsOneButtonPerGroup_WithCounts
[Fact] public async Task UnknownGroup_OpensTheGroups()   // "1|deg|<id>|nope" → chooser
```

  In `OnboardingFlowTests` (the wizard is reached the same way the existing preset tests reach it), replace the existing Custom tests with these:

```csharp
[Fact] public async Task Custom_OpensTheGroups_WithDone()
// click "1|op|Custom" → labels == ["Media 0/5", "Security 0/6", "Server 0/8", "Done"], data[..3] == "1|ocg|media"… , last "1|ocd"
// and call.Explains("Media", "Security", "Server", "Done")
[Fact] public async Task PresetScreen_ExplainsEachPreset()
// the preset wizard message Explains("Everything", "New media", "Admin alerts", "Custom…")
[Fact] public async Task CustomGroup_SwitchesStayInTheGroup_AndBackReturns()
// "1|ocg|media" → 5 rows + "Back" ("1|ocg"); "1|oc|media.movie" → still 5 media rows, movie ✅; "1|ocg" → "Media 1/5"
[Fact] public async Task CustomDone_CreatesWithTheChosenEvents()
// toggle media.movie and server.started, then "1|ocd" → destination Events == ["media.movie", "server.started"], Preset Custom
```

  In `ButtonGuideTests`:

```csharp
[Fact] public void Lines_ListsEachButtonInOrder_Escaped()
// Lines(strings, formatter, ("onboard.tryAgain", "onboard.tryAgain.about"), ("onboard.advanced", "onboard.advanced.about"))
// == "\n\n<b>Try again</b> — …\n<b>Advanced options</b> — …" (use a StringTable with an override "<b>" label to see it escaped)
```

- [ ] **Step 2: Run to verify they fail.** Run `--filter-class "*DestinationScreensTests" --filter-class "*OnboardingFlowTests" --filter-class "*ButtonGuideTests"`. Expected: FAIL, with labels still paged, `deg`/`ocg` unknown, and `ButtonGuide` missing.
- [ ] **Step 3: Implement.**
  - **`ButtonGuide`** and the `Explains` helper, as specified above. `onboard.tryAgain.about` and `onboard.advanced.about` come from Task 5's table; add them now, since the test uses them.
  - **`EventPicker`.** Implement it as specified above.
  - **Choosers and presets.** Both choosers append the group lines; the onboarding chooser also appends Done's. `PresetScreen` appends the four preset lines.
  - **`DestinationScreens`.**
    - `Events(string id)` builds the chooser: `Heading("dest.events.title", d)` + `"\n\n"` + the escaped `events.choose`, then `GroupRows`, then Back → `d`.
    - `EventGroup(string id, string group)` builds a group screen: `Heading("dest.events.title", d)` + `"\n\n<b>"` + the escaped group name + `"</b>"`, then `ToggleRows`, then Back → `de`. An unknown group falls back to `Events`.
    - `ToggleEvent` returns `void`; `det` then shows `EventGroup(id, EventCatalog.GroupOf(key))`. An unknown key shows `Events(id)`.
    - The Detail button becomes `Button("dest.btn.events", "de", d.Id)`.
  - **`OnboardingFlow`.**
    - `ShowCustomAsync(long chatId, FlowState flow, string? group, CancellationToken ct)`. With `group == null`, or an unknown group, it shows the chooser plus a `[Done]` row (`ocd`). Otherwise it shows that group's switches plus `[Back]` (`ocg`).
    - `OnCustomGroupAsync(CallbackQuery query, string? group, CancellationToken ct)` handles `ocg`. It has the same `Wizard`/`EventsKey` guard as `OnCustomToggleAsync`.
    - The toggle passes `EventCatalog.GroupOf(key)`.
  - **`FlowEngine`.** Add `"deg"` and `"ocg"` to `Actions`. Route `ocg` → `_onboarding.OnCustomGroupAsync(query, args.Count > 0 ? args[0] : null, ct)`; `deg` reaches `DestinationScreens.HandleAsync` through the default branch.
- [ ] **Step 4: Run** the same filter plus `--filter-class "*CallbackDataTests" --filter-class "*StringTableTests"`. Expected: PASS.
- [ ] **Step 5: Commit** with the message `feat: events grouped into Media, Security and Server submenus`.

---

### Task 4: Inline prompts for typed input (public URL, topic link)

**Files:**
- Modify: `src/Jellyfin.Plugin.TgJelly/Bot/Screens/SettingsScreens.cs` (`spt`, `OnTextAsync`, `AskForUrlAsync` → `UrlPrompt`)
- Modify: `src/Jellyfin.Plugin.TgJelly/Bot/Flows/TopicFlow.cs` (`AskForLinkAsync`, new `OnBackAsync`)
- Modify: `src/Jellyfin.Plugin.TgJelly/Bot/Flows/OnboardingFlow.cs` (`OnTopicAsync` `tb`)
- Modify: `src/Jellyfin.Plugin.TgJelly/Bot/Screens/FlowEngine.cs` (`Actions` + route `tb`)
- Modify: `en.json`, `StringKeys.cs`
- Test: `tests/…/Bot/Screens/SettingsScreensTests.cs`, `tests/…/Bot/Flows/TopicFlowTests.cs`, `tests/…/Bot/Screens/AdvancedScreenTests.cs`

**Interfaces:**
- Produces:
  - `TopicFlow.OnBackAsync(long chatId, FlowState flow, CancellationToken ct) : Task<TopicChoice?>`: drops the link input and re-asks the topic question in `flow.WizardMessageId`. It returns `null`.
  - Callback `tb` (no arguments): back from the topic-link prompt to the topic question.
  - `SettingsScreens.OnTextAsync` keeps its signature. For an invalid URL it now returns `(true, <prompt screen>)`, which `FlowEngine` already sends as a new message.
- Strings:

  | Key | Change |
  |---|---|
  | `input.cancel` | New: `Cancel` |
  | `input.cancel.about` | New: `go back without changing anything` |
  | `settings.url.prompt` | Becomes `Send the public URL as a message.` |
  | `topic.linkPrompt` | Becomes `Send me the topic's link as a message. Long-press the topic → Copy link.` |
  | `settings.url.placeholder` | Removed |
  | `topic.general.about` | New: `post in the group's General topic` |
  | `topic.create.about` | New: `I make a new topic called Jellyfin and post there` |
  | `topic.existing.about` | New: `post in a topic you already have; you'll send me its link` |

  The topic question (`TopicFlow.AskAsync`) is part of the wizard, so it gains its three lines here.

- [ ] **Step 1: Write the failing tests.**

```csharp
// SettingsScreensTests
[Fact] public async Task TypeAnother_EditsToAnInlinePrompt_WithCancel()
// click "1|spt" on message 5 → one editMessageText on 5: text contains "Send the public URL as a message." and the rule; data == ["1|sp"]; Explains("Cancel")
// and no call anywhere has reply_markup.force_reply
[Fact] public async Task InvalidUrl_SendsThePromptAgain_AsANewMessage()
// spt, then text "jellyfin" → a sendMessage whose text starts "not usable" and contains the prompt, data ["1|sp"]; still awaiting (a valid URL next is saved)
[Fact] public async Task PromptCancel_StopsWaiting()
// spt, click "1|sp", then text "https://jf.example.com" → PublicUrl unchanged; the reply is the set-up menu
// TopicFlowTests (adapt Existing_RecordsTopicNotPickable_AndAsksForTheLink)
[Fact] public async Task Existing_RecordsTopicNotPickable_AndAsksInTheWizard()
// "1|te" on the wizard → editMessageText on WizardId() with "Send me the topic's link as a message.", data ["1|tb"], Explains("Cancel"); TopicNotPickable recorded; no force_reply
[Fact] public async Task TopicQuestion_ExplainsEachChoice()
// the forum's topic question Explains("General topic", "Create a &quot;Jellyfin&quot; topic", "Use an existing topic…")
[Fact] public async Task LinkCancel_ReturnsToTheTopicQuestion()
// te, then "1|tb" → the wizard shows the three topic buttons again; a link text afterwards is not treated as a link
[Fact] public async Task BadLink_AsksAgainInANewMessage_ThatLaterStepsEdit()
// te, text "nope" → sendMessage "That isn't a topic link.…" with ["1|tb"]; flow.WizardMessageId == that message's id;
// then a good link → the presets are shown by editing that new message
// AdvancedScreenTests
[Fact] public async Task AdvancedTopic_AsksInPlace_WithCancelBackToAdvanced()
// click "1|ol" on message 9 (no chat picked) → editMessageText on 9 with the link prompt, data ["1|ox"]
```

- [ ] **Step 2: Run to verify they fail.** Filter: `*SettingsScreensTests`, `*TopicFlowTests`, `*AdvancedScreenTests`. Expected: FAIL (ForceReply is still sent).
- [ ] **Step 3: Implement.**
  - **`SettingsScreens`.**
    - `private Screen UrlPrompt(string? problem)` sets the input flag (as `AskForUrlAsync` does) and builds the screen: the escaped problem + `"\n\n"` when given, then the prompt and the rule, with `[Cancel]` → `sp`.
    - `spt` returns `UrlPrompt(null)`. An invalid text returns `(true, UrlPrompt(_strings.Get("settings.url.invalid")))`.
    - `SettingsScreens` no longer calls `SendPromptAsync`; drop the `BotConnection` dependency if nothing else uses it.
  - **`TopicFlow.AskForLinkAsync`.**
    - It builds the screen with `[Cancel]`: `ox` when `AnyChatKey` is set, otherwise `tb`.
    - With `problem == null`, it edits `flow.WizardMessageId` through `_screens.ShowAsync`.
    - With a problem, it sends a new message through `_screens.ShowAsync(…, null, …)` and stores the returned ID as `flow.WizardMessageId`.
  - **Routing.** `OnboardingFlow.OnTopicAsync` maps `"tb"` → `_topics.OnBackAsync`. Add `"tb"` to `FlowEngine.Actions` and to its `tg`/`tc`/`te` case.
- [ ] **Step 4: Run** the filter plus `*FlowEngineTests` and `*StringTableTests`. Expected: PASS.
- [ ] **Step 5: Commit** with the message `feat: typed input uses inline prompts with Cancel instead of ForceReply`.

---

### Task 5: Inline onboarding through deep links and `my_chat_member`

**Files:**
- Modify: `src/Jellyfin.Plugin.TgJelly/Bot/Flows/OnboardingFlow.cs` (`StartAsync`, `OnRightHereAsync`, `OnCancelAsync`, the new correlation handlers, `FailAsync`, `ContinueWithChatAsync`; remove `BeginCheckAsync`, `PickerExpiredAsync`, the Channel/Group pickers)
- Modify: `src/Jellyfin.Plugin.TgJelly/Bot/Flows/PickerKind.cs` (remove `Channel`, `Group`)
- Modify: `src/Jellyfin.Plugin.TgJelly/Bot/Flows/DetectedChatFlow.cs` (`OnChatMember`: drop the picker exception)
- Modify: `src/Jellyfin.Plugin.TgJelly/Bot/IBotFlows.cs`, `src/Jellyfin.Plugin.TgJelly/Bot/UpdateRouter.cs`
- Modify: `src/Jellyfin.Plugin.TgJelly/Bot/Screens/FlowEngine.cs` (`OnPickerAnswerAsync` → legacy handling, `oa`/`ot`/`oh`/`on`, `OnChatMemberAsync`, `TickAsync`, `OnGroupStartAsync`)
- Modify: `src/Jellyfin.Plugin.TgJelly/Bot/Screens/AdvancedScreen.cs` (remove the group/channel rows)
- Modify: `README.md` line 32 ("pick a chat … with Telegram's own picker" → "tap Add to a channel or Add to a group and choose the chat in Telegram")
- Modify: `en.json`, `StringKeys.cs`
- Create: `tests/…/Fixtures/Telegram/my_chat_member_channel_rights.json` (channel, `administrator` with `can_post_messages:false` → `administrator` with `can_post_messages:true`, `from` `{{user_id}}`)
- Create: `tests/…/Fixtures/Telegram/group_command_start.json` (a copy of `group_text.json`; the text is filled through `{{text}}`)
- Test: `tests/…/Bot/Flows/OnboardingFlowTests.cs` (rewrite the prompt, `chat_shared` and Right here tests), `tests/…/Bot/Flows/DetectedChatFlowTests.cs`, `tests/…/Bot/UpdateRouterTests.cs`, `tests/…/Bot/Screens/AdvancedScreenTests.cs`, `tests/…/Fakes/RecordingBotFlows.cs`

**Interfaces:**
- Consumes: `ShowCustomAsync` (Task 3), `TopicFlow` (unchanged here, apart from Task 4).
- Produces (all in `OnboardingFlow` unless marked):
  - `internal const string AwaitKey = "await";` with the value `"chat"` while a prompt waits for a chat.
  - `internal static bool AwaitsChat(FlowState flow)`: `Params[AwaitKey] == "chat"` and `PromptSentAt` is not null.
  - `internal static readonly string[] LegacyReplyTexts = ["📢 Choose a channel", "👥 Choose a group", "💬 Right here", "Cancel"];`
  - `Task StartAsync(long chatId, int? editMessageId, CancellationToken ct)`: `null` sends the prompt as a new message, otherwise it edits that message.
  - `Task OnRightHereAsync(CallbackQuery query, CancellationToken ct)` handles `oh`.
  - `Task OnCancelAsync(CallbackQuery query, CancellationToken ct)` handles `on`.
  - `Task OnTryAgainAsync(CallbackQuery query, CancellationToken ct)` handles `ot`: it re-checks the failed chat in place, or sends a fresh prompt in place when there is none.
  - `Task<bool> OnChatMemberAsync(ChatMemberUpdated update, CancellationToken ct)` returns `true` when a setup flow took the update. Task 6 extends it.
  - `Task OnGroupStartAsync(Message message, CancellationToken ct)` handles a group `/start@bot …`.
  - `Task OnLegacyReplyAsync(Message message, CancellationToken ct)` handles a stale v0.1 reply text or a `chat_shared` that isn't a live promotion answer.
  - `Task ExpireAsync(long chatId, CancellationToken ct)` handles a prompt that `BotMemory.Prune` just expired.
  - `IBotFlows.OnGroupStartAsync(Message message, CancellationToken ct)` (new member; `FlowEngine` and `RecordingBotFlows` implement it).
  - Callbacks `oh` (Right here) and `on` (Cancel prompt).
- Strings:

  | Key | Change |
  |---|---|
  | `onboard.channel` | Becomes `Add to a channel` |
  | `onboard.group` | Becomes `Add to a group` |
  | `onboard.hint` | New: `After you choose a chat in Telegram, I'll continue here.` |
  | `onboard.channel.about` | New: `choose one of your channels in Telegram; I join it as an admin who can post and edit` |
  | `onboard.group.about` | New: `choose a group in Telegram; I join it as a member` |
  | `onboard.here.about` | New: `send notifications to this private chat` |
  | `onboard.detected.about` | New: `a chat I'm already in that isn't set up yet` |
  | `onboard.cancel.about` | New: `stop adding a destination` |
  | `onboard.tryAgain.about` | New: `check this chat again after you've fixed it` |
  | `onboard.advanced.about` | New: `other ways to add a chat: an existing topic's link, or the dashboard` |
  | `onboard.expired.about` | New: `start setup again` (the line for `menu.add` on the expired prompt) |

  The prompt's text is `onboard.prompt` + `"\n"` + `onboard.hint` + the button lines. When any `Set up …` rows are shown, they share one line: `ButtonGuide.Line(formatter, strings.Get("detected.setUp", ("title", "…")), strings.Get("onboard.detected.about"))`.
  | `onboard.hereChosen` | Becomes `Checking this chat…` |
  | `onboard.expired` | Becomes `That setup prompt expired.` |
  | `onboard.cancelled` | Removed |
  | `advanced.group`, `advanced.channel` | Removed |

- [ ] **Step 1: Write the failing tests.**
  - **Setup.** All the cases below are `[Fact]`s in `OnboardingFlowTests`, apart from the router tests at the end.
    - Replace the `Shared`/`ChannelRequest`/`GroupRequest` helpers with `Added(string fixture, long chatId, long userId = Manager)`. It delivers `TelegramFixtures.Update(fixture, ("chat_id", …), ("user_id", …))`.
    - Add `GroupStart(long chatId, string text, long userId = Manager)`, which delivers `group_command_start`.
    - Add `Edits(int messageId)`, which returns the `editMessageText` calls on that message.
  - **The prompt.**
    - `Start_SendsAnInlinePrompt_WithAddLinks`:
      - the labels are `["Add to a channel","Add to a group","Right here","Cancel"]`;
      - the URLs are `https://t.me/tgjelly_test_bot?startchannel&admin=post_messages+edit_messages` and `https://t.me/tgjelly_test_bot?startgroup=add`;
      - the data is `"1|oh"` and `"1|on"`;
      - the reply markup has no `keyboard`;
      - the text Explains `"Add to a channel"`, `"Add to a group"`, `"Right here"` and `"Cancel"`, in that order;
      - the flow has `WizardMessageId` = the sent ID, `PromptSentAt` = now and `Params["await"] == "chat"`.
    - `StartFromAButton_EditsThatMessage`: a click on `"1|oa"` on message 7 produces one edit on 7 with the prompt and no `sendMessage`.
    - `Start_ListsUpToThreeDetectedChats`: record 4 detected chats and advance past `BotMemory.DetectedGrace`. The prompt shows exactly 3 `Set up <Title>` rows with `"1|xs|<id>"`, and the text contains `<b>Set up …</b> — a chat I'm already in that isn't set up yet` exactly once. With 0 detected chats there is no such line.
  - **Matched adds.**
    - `ChannelAdded_ByThePromptsUser_ChecksAndShowsPresets_InTheSameMessage`: with `AddChannel()`, Start, then `Added("my_chat_member_channel_admin", Channel)`.
      - `Edits(prompt)[0]` text is `Checking <b>News</b>…` (the update's title);
      - the last edit has the four preset labels and the title `Family` (from getChat);
      - only one `sendMessage` (the prompt);
      - `IsDetected(Channel)` is false.
    - `GroupAdded_ByThePromptsUser_ContinuesTheWizard`: the same, with `my_chat_member_group_member` and a group set up through `_h.Api.AddChat`/`SetMember`.
    - `RightsGranted_InAChannelTheBotIsAlreadyIn_CountsAsTheChoice`: `my_chat_member_channel_rights` → presets.
    - `GroupStart_ByThePromptsUser_CountsAsTheChoice`: `GroupStart(Group, "/start@tgjelly_test_bot add")` → presets.
  - **Unmatched and repeated adds.**
    - `AddedByAnotherUser_IsADetectedChat_AndTheWizardWaits`: `user_id` 99 → `IsDetected` is true and the flow `AwaitsChat` still.
    - `RedeliveredAdd_IsHandledOnce`: the same update twice → one `Checking` edit, getChat on the channel called only by the first, `IsDetected` false.
    - `TwoManagers_EachGetOnlyTheirOwnAdd`: managers 42 and 43 both Start; 43 adds a group → 43's prompt continues, 42's flow still `AwaitsChat`, and nothing is edited on 42's prompt.
  - **Expiry.**
    - `UnansweredPrompt_Expires_AndSaysSo`: advance `BotMemory.PromptTtl`, then `await _rig.Flow.TickAsync(ct)`.
      - the prompt is edited to `That setup prompt expired.` with data `["1|oa"]`, and the text Explains `"Add destination"`;
      - `GetFlow(Manager)` is null;
      - a later `Added(...)` makes the chat detected.
  - **Right here and Cancel.**
    - `RightHere_ChecksThisChat_InTheSameMessage`: `"1|oh"` on the prompt → edit `Checking this chat…`, then the presets in the same message.
    - `RightHere_OnAnOldPrompt_IsExpired`: Start twice → the first prompt is deleted; `"1|oh"` on it gives `That menu expired` + the menu.
    - `Cancel_ShowsTheMenuInPlace`: `"1|on"` on the prompt → the prompt is edited to the menu, and the flow is null.
  - **Stale v0.1 input.** `[Theory]` over the four `LegacyReplyTexts`, plus a `chat_shared` with no flow: `LegacyReplyInput_RemovesTheOldKeyboard_AndShowsTheMenu`.
    - a `sendMessage` with text `That menu expired` and `reply_markup.remove_keyboard == true`;
    - then the menu;
    - no other `remove_keyboard` anywhere in the test.
  - **Failure and Try again.**
    - `CheckFails_ExplainsInTheWizard_WithTryAgainAndAdvanced`: the bot is a plain member of a channel. The prompt is edited to the failure with data `["1|ot","1|ox"]` and Explains `"Try again"` and `"Advanced options"`, and `CheckFailed` is recorded.
    - `TryAgain_AfterAFailedChat_RechecksThatChat`: make the bot an admin, then `"1|ot"` on that message → the presets in the same message, with no new prompt.
    - `TryAgain_AfterRightHereFails_RechecksThisChat`: Right here fails (`_h.Api.On("getChat", _ => FakeBotApi.Error(400, "Bad Request: chat not found"))`). Restore getChat, then `ot` → the presets in the same message.
    - `TryAgain_WithoutAFailedChat_SendsAFreshPromptInPlace`: a v0.1-style spent flow without `failed`, saved through `Memory.SaveFlow`, with the wizard ID set → `ot` edits that message to the prompt.
  - **`DetectedChatFlowTests`.** Replace the picker exception test with `AddMatchingAPrompt_IsNotRecorded`; add by another user → recorded.
  - **`UpdateRouterTests`.**
    - `GroupStartForThisBot_IsQueued`: `/start@tgjelly_test_bot add` and `/START@TgJelly_Test_Bot` both reach `RecordingBotFlows.GroupStarts`.
    - `GroupStartForAnotherBot_OrOtherCommands_AreIgnored`: `/start@other_bot add`, `/menu@tgjelly_test_bot` and a message from a bot.
  - **`AdvancedScreenTests`.** `Options_HasTopicAndDashboardOnly`: the labels are `["Use an existing topic","Open dashboard"]`.
- [ ] **Step 2: Run to verify they fail.** Filter: `*OnboardingFlowTests`, `*DetectedChatFlowTests`, `*UpdateRouterTests`, `*AdvancedScreenTests`. Expected: FAIL (compile errors in the helpers first).
- [ ] **Step 3: Implement the prompt and the expiry.**
  - **`StartAsync`.**
    - It deletes the previous flow's `WizardMessageId` when the previous flow `AwaitsChat` and that message isn't `editMessageId`.
    - It shows the prompt screen through `_screens.ShowAsync(chatId, PromptScreen(), editMessageId, ct)`.
    - It saves `new FlowState { Screen = FlowScreen, WizardMessageId = id, PromptSentAt = now }` with `Params[AwaitKey] = "chat"`.
  - **`PromptScreen()`.** It builds the rows in the order given in Task 1 step 4. The detected rows come from `_memory.VisibleDetected(_settings.Current).Take(3)`, with labels made by `MenuScreens.Label`.
  - **`ExpireAsync`.**
    - When the flow's `Params[AwaitKey] == "chat"`, it edits `WizardMessageId` (when set) to `onboard.expired` + `[menu.add → oa]`, then calls `ClearFlow`.
    - Exceptions are logged and swallowed.
  - **`FlowEngine.TickAsync`.** It becomes `foreach (var id in _memory.Prune()) await _onboarding.ExpireAsync(id, ct);`, then the notifier.
- [ ] **Step 4: Implement the correlation.**
  - **`OnChatMemberAsync(update)`.** It returns `false` for private chats and for users with no flow. Then, for the flow of `update.From.Id`:
    - when the flow's `Params[ChatIdKey]` equals `update.Chat.Id` (a repeat), it returns `true` and does nothing;
    - when the flow `AwaitsChat` and the change counts as an add, it runs `OnChatChosenAsync(update.From.Id, flow, update.Chat, ct)` and returns `true`;
    - otherwise it returns `false`.

    Use this predicate for "counts as an add":

```csharp
private static bool IsAdd(ChatMemberUpdated u)
    => (u.NewChatMember is ChatMemberMember or ChatMemberAdministrator || u.NewChatMember is ChatMemberRestricted { IsMember: true })
       && (u.OldChatMember.Status is ChatMemberStatus.Left or ChatMemberStatus.Kicked || u.NewChatMember is ChatMemberAdministrator);
```

  - **`OnGroupStartAsync(message)`.** It applies the same flow lookup and the same repeat rule with `message.From!.Id` and `message.Chat`. A flow that `AwaitsChat` → `OnChatChosenAsync`.
  - **`OnChatChosenAsync`.**
    - It edits the wizard to `onboard.checking` with the escaped chat title, or the ID when the title is empty.
    - It updates the flow: remove `AwaitKey`, set `PromptSentAt = null`, set `ChatIdKey`/`TitleKey`, and save.
    - It runs `_retrier.CheckAsync(chat.Id, retry: true, ct)` → `ContinueAfterCheckAsync(privateChatId, check, 0, flow.WizardMessageId, ct)`.
    - An exception → `FailAsync(token.unreachable)`.
  - **`FlowEngine.OnChatMemberAsync`.** It becomes `if (!await _onboarding.OnChatMemberAsync(update, ct)) _detected.OnChatMember(update);`.
  - **`DetectedChatFlow.OnChatMember`.** Drop the `PickerRequestIds` clause and update its summary.
  - **`UpdateRouter.RouteMessage`.** Add a case for a group or supergroup message whose `From` is not a bot and whose first word, case-insensitively, is `/start@` + `_connection.BotUsername`. It enqueues `"group-start"` → `_flows.OnGroupStartAsync`.
- [ ] **Step 5: Implement the buttons, failures and stale input.**
  - **Right here and Cancel.** Both `OnRightHereAsync` and `OnCancelAsync` need the pressed message to equal `WizardMessageId` and the flow to `AwaitsChat`; otherwise `MenuExpiredAsync`.
    - Right here edits to `onboard.hereChosen`, clears the await state like `OnChatChosenAsync` does, and checks with `retry: false`.
    - Cancel calls `ClearFlow`, then shows the main menu in the pressed message.
  - **`FailAsync`.** Its signature becomes `FailAsync(long chatId, string message, int? wizardMessageId, long? failedChatId, CancellationToken ct)`.
    - A non-null `failedChatId` is stored in the spent flow as `Params[ChatIdKey]` plus `Params["failed"] = "1"`.
    - Every caller passes the chat it checked: the chosen chat, the private chat for Right here, or the target in `CreateAsync`.
    - The repeat rule in `OnChatMemberAsync` skips a flow that has `failed`. After a failure, a later add of that chat is treated like any other add: it is a detected chat unless a new prompt is waiting.
  - **Try again.** `OnTryAgainAsync(CallbackQuery query, CancellationToken ct)` handles `ot`.
    - When the pressed message is the flow's wizard and the flow has `failed`, it re-checks that chat once in place: edit to `onboard.checking`, then `ContinueAfterCheckAsync`.
    - Otherwise it calls `StartAsync(chatId, message.MessageId, ct)`.
  - **Remove the old paths.**
    - Remove `CheckingMessageId` use from `OnboardingFlow` (`TopicFlow` keeps its own until Task 6).
    - Remove `BeginCheckAsync`, `PickerExpiredAsync` and the Channel/Group `PickerButton` arms (`PickerKind.TopicPromotion` stays for Task 6).
  - **Stale input routing.**
    - `FlowEngine.OnPickerAnswerAsync` becomes `OnLegacyReplyAsync`. A `chat_shared` that `TopicFlow.IsPromotionRequest` accepts still goes to the promotion path (until Task 6). Any other `chat_shared`, or a text in `LegacyReplyTexts`, goes to `_onboarding.OnLegacyReplyAsync`.
    - `OnLegacyReplyAsync` sends `menu.expired` with `ReplyKeyboardRemove` through `SendPromptAsync`, then the main menu.
  - **Routing.**
    - `oa` → `StartAsync(chatId, message.MessageId, ct)`;
    - `ot` → `OnTryAgainAsync`;
    - `oh` → `OnRightHereAsync`;
    - `on` → `OnCancelAsync`.

    Add `"oh"` and `"on"` to `Actions`.
  - **`AdvancedScreen.Options()`.** It keeps only the `ol` and `od` rows, and `BotConnection` leaves its constructor if unused. Update the README line.
- [ ] **Step 6: Run** the Step 2 filter, then the whole suite on net10.0: `dotnet test --project tests/Jellyfin.Plugin.TgJelly.Tests -c Release -f net10.0`. Expected: PASS. Update any `TopicFlowTests` helper that started onboarding through `chat_shared` to use `Added(...)`; their assertions stay.
- [ ] **Step 7: Commit** with the message `feat: inline onboarding through startchannel/startgroup links and my_chat_member`.

---

### Task 6: Inline topic promotion and removal of the picker plumbing

**Files:**
- Modify: `src/Jellyfin.Plugin.TgJelly/Bot/BotLinks.cs` (add `PromoteForTopics`)
- Modify: `src/Jellyfin.Plugin.TgJelly/Bot/Flows/TopicFlow.cs` (`OnCreateAsync`, new `AwaitsPromotion`/`OnPromotedAsync`/`OnOtherChatAsync`; remove `IsPromotionRequest`, `OnPromotionSharedAsync`, `CheckingMessageId` and `PromptMessageId` handling)
- Modify: `src/Jellyfin.Plugin.TgJelly/Bot/Flows/OnboardingFlow.cs` (`OnChatMemberAsync`/`OnGroupStartAsync` promotion cases, `tk`; remove `OnChatSharedAsync`, `PickerButton`, `AnswerKey`, `IsRepeat`)
- Delete: `src/Jellyfin.Plugin.TgJelly/Bot/Flows/PickerKind.cs`
- Modify: `src/Jellyfin.Plugin.TgJelly/State/Documents/FlowState.cs` (remove `PromptMessageId`, `CheckingMessageId` and `PickerRequestIds`; fix the `Params` doc), `State/Documents/TelegramSection.cs` (remove `LastRequestId`)
- Modify: `src/Jellyfin.Plugin.TgJelly/Bot/BotMemory.cs` (remove `NextRequestId`; `Prune`/`Copy` drop the removed fields)
- Modify: `src/Jellyfin.Plugin.TgJelly/Bot/OutgoingPrompt.cs` (doc: `ReplyKeyboardRemove` only, for stale v0.1 keyboards)
- Modify: `src/Jellyfin.Plugin.TgJelly/Bot/Screens/FlowEngine.cs` (`Actions` + `tk`; `chat_shared` always goes to legacy)
- Modify: `en.json`, `StringKeys.cs`
- Create: `tests/…/Fixtures/Telegram/my_chat_member_group_promoted.json` (supergroup `{{chat_id}}`, `is_forum:true`, `from` `{{user_id}}`, `member` → `administrator` with `can_manage_topics:true`)
- Test: `tests/…/Bot/Flows/TopicFlowTests.cs`, `tests/…/Bot/BotMemoryTests.cs`, `tests/…/State/StateStoreTests.cs`

**Interfaces:**
- Consumes:
  - `OnboardingFlow.OnChatMemberAsync`, `OnGroupStartAsync` and `OnLegacyReplyAsync` (Task 5);
  - `TopicFlow.AskForLinkAsync`/`OnBackAsync` (Task 4).
- Produces:
  - `BotLinks.PromoteForTopics(string bot) => Base(bot) + "?startgroup=topics&admin=manage_topics"`.
  - `TopicFlow.AwaitsPromotion(FlowState flow) : bool` (static): the topic step with `Params["promotion"] == "1"`.
  - `TopicFlow.OnPromotedAsync(long chatId, FlowState flow, CancellationToken ct) : Task<TopicChoice?>`:
    - it edits the wizard to `onboard.checking`;
    - then `_retrier.CheckAsync(forum, retry: true)` and `_retrier.UntilAsync(CanManageTopicsAsync)`;
    - rights present → `CreateTopicAsync`;
    - rights still missing → the promotion screen with `topic.stillNoRights`, still waiting, returning `null`;
    - a check or creation failure → the existing `FailAsync`.
  - `TopicFlow.OnOtherChatAsync(long chatId, FlowState flow, Chat other, CancellationToken ct) : Task`: the promotion screen with `topic.otherGroup`, still waiting.
  - Callback `tk` (Check again on the promotion screen).
- Strings:

  | Key | Change |
  |---|---|
  | `topic.allowPrompt` | Becomes `To create a topic, I need permission to manage topics in <b>{title}</b>.` (HTML; title escaped; the button lines explain the rest) |
  | `topic.checkAgain` | New: `Check again` |
  | `topic.allow.about` | New: `opens Telegram so you can give me that permission; choose the group and confirm` |
  | `topic.checkAgain.about` | New: `if you or another admin already gave me the permission, tap this and I'll create the topic` |

  On the promotion screen, General topic and Advanced options reuse `topic.general.about` (Task 4) and `onboard.advanced.about` (Task 5). The `topic.stillNoRights` and `topic.otherGroup` notes go above the button lines.
  | `topic.stillNoRights` | New: `I still can't manage topics in {title}.` |
  | `topic.otherGroup` | New: `You changed my rights in {other}, not in {title}.` |

- [ ] **Step 1: Write the failing tests.**
  - **`TopicFlowTests`.** Replace the reply-keyboard promotion tests (`Create_WithoutRights_SendsThePromotionPicker_ThenCreates`, `Promotion_ForAnotherChat_IsRejected`, `AbandonedPromotion_DoesNotBlockCreate`, `ExpiredPromotion_CreateAgain_SendsAFreshPrompt`, `IgnoredPromotion_CreateAgain_SpendsTheOldPromptAndSendsAFreshOne`, `Promotion_WhoseRightsArriveLate_RetriesThenCreates`) with these `[Fact]`s:
    - `Create_WithoutRights_EditsTheWizardToThePromotionLinks`:
      - the labels are `["Allow topic creation","Check again","General topic","Advanced options"]`;
      - the first button's url is `https://t.me/tgjelly_test_bot?startgroup=topics&admin=manage_topics`;
      - the data is `["1|tk","1|tg","1|ox"]`;
      - the text Explains `"Allow topic creation"`, `"Check again"`, `"General topic"` and `"Advanced options"`, in that order;
      - there is no `sendMessage` after the wizard, and `AwaitsPromotion` holds.
    - `Promotion_InTheFlowsForum_CreatesTheTopic`: `my_chat_member_group_promoted` for the forum from 42, with `SetMember` = admin with topics → `createForumTopic` name `Jellyfin` once, then the wizard is edited to the presets.
    - `Promotion_WhoseRightsArriveLate_RetriesThenCreates`: keep this test's intent, with the `getChatMember` sequence no-topics, no-topics, topics.
    - `Promotion_InAnotherGroup_SaysSo_AndKeepsWaiting`: the same fixture for chat −1009:
      - the wizard text contains `You changed my rights in`;
      - no `createForumTopic`;
      - `AwaitsPromotion` still holds, and `IsDetected(-1009)` is false.
    - `CheckAgain_WithRights_CreatesTheTopic`: set the rights, then `"1|tk"` → created.
    - `CheckAgain_WithoutRights_SaysSo_AndKeepsTheLinks`: the text contains `I still can't manage topics in`, with the same four buttons and their four explanation lines.
    - `GroupStartTopics_InTheFlowsForum_CountsAsThePromotion`: `/start@tgjelly_test_bot topics` in the forum from 42, with the rights set → created.
    - `PromotedButCreationFails_FallsBackToGeneralAndAdvanced_RecordsTopicFailed`: adapt `PromotionFails_…` to the new trigger.
    - `LegacyAllowTopicPick_IsExpired`: a `chat_shared` while `AwaitsPromotion` → `remove_keyboard` + `That menu expired`, and the flow is unchanged.
  - **`StateStoreTests`.** Add `V01State_WithPickerFields_StillLoads`. It writes a state file in the existing raw-file style with these fields:
    - a flow with `"pickerRequestIds":[3,4]`, `"promptMessageId":5`, `"checkingMessageId":6`, `"promptSentAt"`;
    - `"telegram":{"lastRequestId":4}`.

    It loads it and asserts that the flow's `PromptSentAt` and `Screen` survive.
  - **`BotMemoryTests`.** Delete the `NextRequestId` tests.
- [ ] **Step 2: Run to verify they fail.** Filter: `*TopicFlowTests`, `*StateStoreTests`, `*BotMemoryTests`. Expected: FAIL.
- [ ] **Step 3: Implement.**
  - **`OnCreateAsync`.** When the bot can't manage topics, it sets `Params["promotion"] = "1"`, removes `CreatingKey`, and edits the wizard to `PromotionScreen(title, problem: null)`. That screen is the four rows, with the URL from `BotLinks.PromoteForTopics(_connection.BotUsername)`. A second `tc` can't happen, because the promotion screen has no `tc` button.
  - **Promotion cases in `OnboardingFlow.OnChatMemberAsync`**, checked before the chat-await case, for a flow that `TopicFlow.AwaitsPromotion`:
    - same chat and `update.NewChatMember is ChatMemberAdministrator` → `ContinueWithTopicAsync(await _topics.OnPromotedAsync(...))`, return `true`;
    - another non-private chat with an add or admin change → `_topics.OnOtherChatAsync`, then return `false`, so `DetectedChatFlow` still sees a real join.
  - **`OnGroupStartAsync`.** The flow's forum → `OnPromotedAsync`.
  - **`tk`.** It passes the `_topics.Wizard(query)` guard → `OnPromotedAsync`. Add `"tk"` to `Actions`.
  - **Delete the picker plumbing:**
    - `PickerKind.cs`, `OnboardingFlow.PickerButton`, `OnChatSharedAsync`, `AnswerKey` and `IsRepeat`;
    - `TopicFlow.IsPromotionRequest`, `OnPromotionSharedAsync`, `ClearPrompt`'s prompt fields and every `CheckingMessageId`/`PromptMessageId` use;
    - `FlowState.PromptMessageId`/`CheckingMessageId`/`PickerRequestIds`, `TelegramSection.LastRequestId` and `BotMemory.NextRequestId`, plus their lines in `Prune` and `Copy`.

    `FlowEngine` sends every `chat_shared` to `OnLegacyReplyAsync`.
  - **Final checks.** `grep -rn "KeyboardButton\b\|ReplyKeyboardMarkup\|ForceReply\|RequestChat\|ChatShared" src/` must show only the `ChatShared` check in `FlowEngine`'s legacy routing. Update `OutgoingPrompt`'s doc.
- [ ] **Step 4: Run** the whole suite on net10.0. Expected: PASS.
- [ ] **Step 5: Commit** with the message `feat: topic promotion through an inline startgroup link; remove the request_chat plumbing`.

---

### Task 7: Every other bot screen explains its buttons

**Files:**
- Modify: `src/Jellyfin.Plugin.TgJelly/Bot/Screens/MenuScreens.cs` (`Build`)
- Modify: `src/Jellyfin.Plugin.TgJelly/Bot/Screens/DestinationScreens.cs` (`Listing`, `Detail`, `EventGroup`, `Libraries`, `SilentGroups`, `ConfirmRemove`)
- Modify: `src/Jellyfin.Plugin.TgJelly/Bot/Screens/SettingsScreens.cs` (`Menu`, `Messages`, `Updates`, `PublicUrl`)
- Modify: `src/Jellyfin.Plugin.TgJelly/Bot/Flows/DetectedChatFlow.cs` (`List`)
- Modify: `src/Jellyfin.Plugin.TgJelly/Bot/Screens/AdvancedScreen.cs` (`Options`)
- Modify: `src/Jellyfin.Plugin.TgJelly/Bot/Flows/OnboardingFlow.cs` (`DoneScreen`, `ShowCustomAsync` group screen)
- Modify: `src/Jellyfin.Plugin.TgJelly/Bot/ManagerNotifier.cs` (the problem message)
- Modify: `en.json`, `StringKeys.cs`
- Test: `tests/…/Bot/Screens/MenuScreensTests.cs`, `DestinationScreensTests.cs`, `SettingsScreensTests.cs`, `AdvancedScreenTests.cs`, `tests/…/Bot/Flows/DetectedChatFlowTests.cs`, `OnboardingFlowTests.cs`, `tests/…/Bot/ManagerNotifierTests.cs`

**Interfaces:**
- Consumes: `ButtonGuide.Lines` / `ButtonGuide.Line` and the test helper `BotApiCall.Explains` (Task 3).
- Rule for lists. Screens whose buttons are items rather than actions get **one** shared line in place of a line per item:
  - destinations, the `/test` picker;
  - event, library, message-style and silent-group switches;
  - detected chats.

  Prev, Next and Back get no line. Fixed action buttons on the same screen, such as `All libraries`, keep their own line.

- Strings (all new). A row whose label reads "(line)" is a shared line, sent as a plain sentence after the button lines; every other row is the `about` text after `<b>Label</b> — `.

  | Key | Text |
  |---|---|
  | `menu.destinations.about` | `see and change the chats I send to` |
  | `menu.add.about` | `send notifications to another channel, group or chat` |
  | `menu.settings.about` | `message style, update checks and the public URL` |
  | `menu.status.about` | `connection, destination health and recent errors` |
  | `menu.detected.about` | `chats someone added me to outside setup; set them up or dismiss them` |
  | `menu.destinations.pick` (line) | `Tap a destination to open it.` |
  | `dest.picker.pick` (line) | `Tap a destination to send it a test message.` |
  | `dest.btn.events.about` | `choose which events this chat receives` |
  | `dest.btn.libraries.about` | `limit new-media posts to some libraries` |
  | `dest.btn.silent.about` | `deliver some groups without a notification sound` |
  | `dest.btn.test.about` | `send a test message to this chat now` |
  | `dest.btn.check.about` | `check again that I can still post here` |
  | `dest.btn.pause.about` | `stop sending here for now; nothing is deleted` |
  | `dest.btn.resume.about` | `start sending here again` |
  | `dest.btn.remove.about` | `stop sending here and forget this chat` |
  | `dest.remove.only.about` | `forget this chat; I stay in it` (the `Remove` button on the confirm screen) |
  | `dest.btn.removeLeave.about` | `forget this chat and leave it` |
  | `dest.btn.cancel.about` | `keep this destination` |
  | `dest.events.pick` (line) | `Tap an event to switch it on (✅) or off (▫️).` (also the onboarding Custom group screen) |
  | `dest.libraries.all.about` | `post new media from every library` |
  | `dest.libraries.pick` (line) | `Tap a library to include it (✅) or leave it out (▫️).` |
  | `dest.silent.pick` (line) | `Tap a group to switch it between 🔔 with sound and 🔕 silent.` |
  | `settings.messages.about` | `what notifications show: poster, overview and buttons` |
  | `settings.updates.about` | `turn release checks on or off, and choose which versions count` |
  | `settings.url.about` | `the address the Open in Jellyfin button uses` |
  | `settings.messages.pick` (line) | `Tap a switch to turn it on (✅) or off (⬜).` |
  | `settings.updates.enabled.about` | `look for new releases and tell you here` |
  | `settings.updates.mode.SameMajor.about` | `only versions with the same major number as yours` |
  | `settings.updates.mode.AnyNewer.about` | `every newer version` |
  | `settings.url.use.about` | `save the suggested address` |
  | `settings.url.type.about` | `send me a different address` |
  | `settings.url.clear.about` | `remove the address; posts lose the Open in Jellyfin button` |
  | `detected.setUp.about` | `add the chat as a destination` (one line, labelled `Set up …`) |
  | `detected.dismiss.about` | `hide it from this list; I stay in the chat` (one line, labelled `Dismiss …`) |
  | `advanced.topic.about` | `post in a topic you already have; you'll send me its link` |
  | `advanced.dashboard.about` | `add a chat by its ID in the Jellyfin dashboard` |
  | `onboard.addAnother.about` | `set up one more chat` |
  | `onboard.menu.about` | `back to the main menu` |
  | `notify.btn.open.about` | `open this destination's settings` |

  The existing hints `dest.libraries.hint`, `dest.silent.hint`, `settings.messages.hint` and `settings.updates.hint` stay as they are, above the button lines.

- [ ] **Step 1: Write the failing tests.** One `[Fact]` per screen, each asserting `Explains(...)` with the screen's labels in button order and, for list screens, the shared line:
  - `MainMenu_ExplainsEachButton`: `"Destinations (2)"`, `"Add destination"`, `"Settings"`, `"Status"`, and with a detected chat also `"Chats added outside setup (1)"`.
  - `DestinationList_SaysWhatTappingDoes`: contains `Tap a destination to open it.`, and no `<b>‹ Prev</b>`/`<b>Next ›</b>`/`<b>Back</b>` lines.
  - `TestPicker_SaysWhatTappingDoes`.
  - `Detail_ExplainsEachButton`: `Events`, `Libraries`, `Silent groups`, `Send test`, `Check again`, `Pause`, `Remove`. A paused destination shows `Resume` with `start sending here again`.
  - `ConfirmRemove_ExplainsEachChoice`: `Remove` (`forget this chat; I stay in it`), `Remove and leave`, `Cancel`.
  - `EventGroup_SaysWhatTappingDoes`, `Libraries_ExplainsAllAndTheSwitches`, `SilentGroups_SaysWhatTappingDoes`.
  - `SettingsMenu_ExplainsEachButton`, `MessageStyle_SaysWhatTappingDoes`, `Updates_ExplainsEachButton`.
  - `PublicUrl_ExplainsOnlyTheButtonsShown`: no suggestion and no URL → only `Type another`; with both → `Use the suggested URL`, `Type another`, `Clear`.
  - `DetectedList_ExplainsSetUpAndDismiss_Once`: two detected chats → each of the two lines appears exactly once.
  - `AdvancedOptions_ExplainsEachButton`.
  - `Done_ExplainsAddAnotherAndMenu`.
  - `ProblemNotification_ExplainsOpenAndCheck`.
- [ ] **Step 2: Run to verify they fail.** Filter: `*MenuScreensTests`, `*DestinationScreensTests`, `*SettingsScreensTests`, `*AdvancedScreenTests`, `*DetectedChatFlowTests`, `*OnboardingFlowTests`, `*ManagerNotifierTests`. Expected: FAIL.
- [ ] **Step 3: Implement.** Each screen appends `ButtonGuide.Lines(...)` with the `(labelKey, aboutKey)` pairs of the buttons it actually shows, in row order. List screens append `"\n\n" + Escape(<pick line>)` instead of per-item lines. Labels with arguments (`menu.destinations`, `menu.detected`, `Set up …`) go through `ButtonGuide.Line` with the label as shown.
  - **Notification length.** `ManagerNotifier` appends its two lines to `notify.problem`. That message stays well under Telegram's 4096-character limit, and the title is the only free text, already capped by Telegram.
  - **Existing assertions.** Existing tests that compare a whole screen's text with `Assert.Equal` are updated to the new text; tests that only use `Contains` stay.
- [ ] **Step 4: Run** the whole suite on net10.0. Expected: PASS.
- [ ] **Step 5: Commit** with the message `feat: every bot screen explains what its buttons do`.

---

### Task 8: Smoke test drives the inline onboarding

**Files:**
- Modify: `tests/integration/smoke.py` (`onboarding_checks`, new helpers `url_button`, `chat_member_update`)
- Test: `tests/integration/test_smoke.py`

**Interfaces:**
- Produces:
  - `chat_member_update(chat_id: int, chat_type: str, title: str, old_status: str, new_status: str, rights: dict | None = None) -> dict`: a `my_chat_member` update. `from` is the smoke admin. The bot user ID is taken from `SMOKE_TOKEN` (the digits before `:`). The rights dict is merged into `new_chat_member`.
  - `url_button(markup, url_part: str)`: the first inline button whose `url` contains `url_part`, or `None`.

- [ ] **Step 1: Write the failing tests** in `test_smoke.py`:

```python
def test_chat_member_update_comes_from_the_admin(self):
    u = smoke.chat_member_update(smoke.SMOKE_CHAT_3, "channel", "Smoke channel", "left", "administrator", {"can_post_messages": True})
    m = u["my_chat_member"]
    self.assertEqual(m["from"]["id"], smoke.SMOKE_ADMIN)
    self.assertEqual((m["chat"]["id"], m["chat"]["type"]), (smoke.SMOKE_CHAT_3, "channel"))
    self.assertEqual((m["old_chat_member"]["status"], m["new_chat_member"]["status"]), ("left", "administrator"))
    self.assertEqual(m["new_chat_member"]["user"]["id"], int(smoke.SMOKE_TOKEN.split(":")[0]))
    self.assertTrue(m["new_chat_member"]["can_post_messages"])

def test_url_button_finds_by_url(self):
    markup = {"inline_keyboard": [[{"text": "a", "callback_data": "1|oh"}], [{"text": "b", "url": "https://t.me/x?startchannel&admin=post_messages"}]]}
    self.assertEqual(smoke.url_button(markup, "startchannel")["text"], "b")
    self.assertIsNone(smoke.url_button(markup, "startgroup"))
```

- [ ] **Step 2: Run** `python3 -m unittest discover -s tests/integration -p 'test_*.py' -v`. Expected: FAIL (the helpers are missing).
- [ ] **Step 3: Implement** the helpers, then rewrite `onboarding_checks` after `/start setup`:
  1. Wait until `url_button(stub.last_reply_markup("sendMessage", SMOKE_ADMIN), "startchannel")` exists.
  2. Store `prompt_id = stub.sent_message_id(SMOKE_ADMIN, "Where should notifications go?")`.
  3. Push `chat_member_update(SMOKE_CHAT_3, "channel", "Smoke channel", "left", "administrator", {"can_post_messages": True, "can_edit_messages": True, plus the remaining `ChatMemberAdministrator` booleans the stub's getChatMember returns})`.
  4. Wait for a "New media" callback button in `stub.last_reply_markup("editMessageText", SMOKE_ADMIN)`.
  5. Press it with `callback_update(data, prompt_id, "What should I send there?")`.

  The three existing final waits stay: the destination is added, the test message arrived, and Done was edited. Update the docstring to "inline onboarding".
- [ ] **Step 4: Run** the Step 2 command and `python3 -m py_compile tests/integration/smoke.py`. Expected: PASS. The Docker smoke run itself happens in CI (Task 10).
- [ ] **Step 5: Commit** with the message `test: smoke onboarding pushes my_chat_member instead of chat_shared`.

---

### Task 9: Dashboard shows the bot's identity once

**Files:**
- Modify: `src/Jellyfin.Plugin.TgJelly/Api/TgJellyController.cs:85-90`
- Modify: `src/Jellyfin.Plugin.TgJelly/Web/tgjelly.js` (`renderConnect` Connected branch, `renderBot`)
- Modify: `en.json`, `StringKeys.cs`
- Test: `tests/Jellyfin.Plugin.TgJelly.Tests/Api/TgJellyControllerTests.cs`, `tests/web/tgjelly.test.mjs`

**Interfaces:**
- Strings:

  | Key | Change |
  |---|---|
  | `web.status.connected` | New: `Connected to Telegram` |
  | `web.bot.open` | New: `Open in Telegram` |
  | `web.bot.access` | Becomes `Anyone who can open this bot can manage notifications. Keep it restricted in BotFather.` (no `{bot}`) |
  | `web.connect.connected`, `web.connect.open` | Removed |
  | `connection.Connected` | Unchanged (the bot's `/status`) |

- [ ] **Step 1: Write the failing tests.**
  - **`TgJellyControllerTests`.** `Status_WhenConnected_SaysConnected_WithoutTheUsername`: `ConnectionMessage == "Connected to Telegram"`, and `BotUsername` is still `tgjelly_test_bot`.
  - **`tgjelly.test.mjs`.** Change the status fixture's `ConnectionMessage` to `'Connected to Telegram'` (line ~29) and the line-251 assertion to match.
    - Rename `'Connected shows open link from Links.OpenBot'` to `'Connected closes the connect card; the Bot section names the bot once'`. It asserts:
      - `el('tgj-connect').innerHTML === ''`;
      - `el('tgj-bot').innerHTML` contains `'@jf_bot'` exactly once (`split('@jf_bot').length === 2`), `href="https://t.me/jf_bot?start=setup"` and `'Open in Telegram'`.
    - `'a removed webhook is mentioned after Connected'` keeps its assertion; the card holds only that note.
    - The switch-bot test (line ~713) asserts the Bot section instead of `Connected as @new`.
    - Add `'Bot section access line does not repeat the username'`: `renderBot(...)` output contains `'Anyone who can open this bot'`.
- [ ] **Step 2: Run** `node --test 'tests/web/*.test.mjs'` and the filter `*TgJellyControllerTests`. Expected: FAIL.
- [ ] **Step 3: Implement.**
  - **`TgJellyController`.** It maps `ConnectionState.Connected` to `_strings.Get("web.status.connected")`; the other states are unchanged.
  - **`renderConnect`.** With `result.kind === 'Connected'` it returns `''`, or with `webhookDeleted` only `<div class="verticalSection"><p class="fieldDescription">${t(S, 'web.connect.webhookRemoved')}</p></div>`.
  - **`renderBot`.** It shows `<p><b>@${esc(bot)}</b></p>`, then `t(S, 'web.bot.access')`, then `telegramLink(links && links.OpenBot, t(S, 'web.bot.open'))`.
- [ ] **Step 4: Run** the Step 2 commands, `node --check src/Jellyfin.Plugin.TgJelly/Web/tgjelly.js`, and the filters `*StringTableTests` and `*WebPagesTests`. Expected: PASS.
- [ ] **Step 5: Commit** with the message `feat: dashboard names the bot once, in the Bot section`.

---

### Task 10: Verify, open the PR, owner-gated v0.2

**Files:** none new.

- [ ] **Step 1: Run the full verification.**
  - `export PATH="$HOME/.dotnet:$PATH" && dotnet build Jellyfin.Plugin.TgJelly.slnx -c Release && dotnet test --solution Jellyfin.Plugin.TgJelly.slnx -c Release --no-build`, covering both TFMs, with 0 warnings as errors and 0 failures;
  - `node --test 'tests/web/*.test.mjs'`;
  - `python3 -m unittest discover -s build -p 'test_*.py'`;
  - `python3 -m unittest discover -s tests/integration -p 'test_*.py'`.

  Expected: all pass. Record the counts in the PR body.
- [ ] **Step 2: Grep the Global Constraints.**
  - `grep -rn "ReplyKeyboardMarkup\|KeyboardButton(\|ForceReplyMarkup\|RequestChat" src/` → no output.
  - `grep -rn "ReplyKeyboardRemove" src/` → only `OnboardingFlow.OnLegacyReplyAsync`.
- [ ] **Step 3: Push and open the PR.** Push `feat/plan6-inline-ux` (pre-approved for feature branches), then open the PR into `main`: `gh pr create --title "Plan 6: inline-only bot UX, events submenus, dashboard dedupe"`. The body should hold:
  - a summary per task;
  - the Review Focus list;
  - the unverified F43 / Q10 (spec numbering; F41 was already Scriban);
  - `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
- [ ] **Step 4: Wait for CI**, including the Docker smoke matrix with the new onboarding check. A smoke failure is debugged on the branch.
- [ ] **Step 5: Owner gate.** Ask the owner to:
  - review and merge the PR;
  - run the §14.4 items that changed (the inline add links on Android/iOS/Desktop, a chat the bot is already in, and the topic promotion);
  - approve the `v0.2` tag.

  Do not push a tag without that approval.
