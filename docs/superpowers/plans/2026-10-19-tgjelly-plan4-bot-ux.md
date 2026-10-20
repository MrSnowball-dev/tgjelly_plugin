# TgJelly Plan 4: Bot UX — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The bot becomes the main way to set TgJelly up. An admin opens the bot, taps `📢 Choose a channel`, picks a chat in Telegram's native picker, picks what to receive, and the chat is a destination. The bot also covers forum topics, `/menu` with every destination, setting and status screen, chats added outside setup, the Advanced fallbacks after a failed setup, and private messages to managers when a destination breaks. CI proves the picker path end to end inside real Jellyfin through the stub Bot API.

**Architecture:**
- `UpdateRouter` replaces plan 2's `BasicUpdateHandler` as the `IUpdateHandler` the poll loop calls. It stays fast:
  - it answers callback queries at once;
  - it ignores everything from groups and channels except `my_chat_member` and the supergroup-migration service message;
  - it turns each remaining update into a work item on the `BotWorkQueue`.
- One `BotWorker` hosted service runs those items one at a time. Flow code therefore never runs concurrently with itself, and the poll loop never waits on a 5-second check or a forum-topic creation.
- `FlowEngine` is the work items' entry point. It dispatches commands, callbacks, `chat_shared`, typed replies and `my_chat_member` to small screen and flow classes:
  - `MenuScreens`, `DestinationScreens`, `SettingsScreens`, `StatusScreen`;
  - `OnboardingFlow`, `TopicFlow`, `DetectedChatFlow`.

  Each screen is a `Screen(Html, Buttons)` value shown by `ScreenSender`, which edits the clicked message or sends a new one.
- Per-chat flow state, managers, detected chats and setup failures live in `state.json`, behind `BotMemory`.
- Destination creation and chat migration move out of the API controller and `OutboxDelivery` into `DestinationCreator` and `DestinationMigration`, so the bot, the API and delivery share one implementation.

**Tech Stack:** C# / .NET 9 + .NET 10 (unchanged), Telegram.Bot 22.10.3.3 (`KeyboardButtonRequestChat`, `ChatShared`, `ReplyKeyboardRemove`, `ForceReplyMarkup`, `CreateForumTopic`, `DeleteMessage`), xunit.v3, NSubstitute, `Microsoft.Extensions.TimeProvider.Testing`, Python 3 stdlib for the CI stub.

**Spec:** `docs/superpowers/specs/2026-10-16-tgjelly-v1-design.md` (rev. 2). This plan implements:
- §6.2–§6.5 (onboarding, menus, access model, fallbacks and detected chats);
- §9.2's "Long work goes to the bot work queue";
- §9.4's manager notifications (`health.notified`);
- §10.3's `flows`, `managers`, `detectedChats` and `setupFailures`, and their place in §10.4's resets;
- §11.2's `DetectedChats` endpoints and the detected chats and manual-block flag in `GET Destinations`;
- §14.1's FlowEngine tests and §14.2's Telegram fixtures.

It settles §15 Q1 and Q3 (owner-gated Task 15). Q2, Q4 and Q9's parse-error text were settled on the owner's bot while this plan was written; Q2's answer makes IPv6-literal public URLs invalid (Task 7). Plans 1–3 are merged code, or are in PR #3 when this plan starts; rebase onto `main` once #3 merges. Their Global Constraints still apply.

**Deliberate deviations and deferrals (so reviewers don't flag them):**
- **Menu screens are stateless; wizard steps are not.**
  - A menu button's callback data carries everything it needs (destination ID, page, toggle). The screen re-reads settings and renders again on the clicked message. An old menu message therefore keeps working, and acts on today's settings (§10.4: "Bot screens always re-read the settings").
  - Wizard callbacks (topic choice, preset) work only while the chat's flow is live and the click is on the flow's wizard message. Any other click is answered "That menu expired" and shows `/menu`.
- **One worker, sequential.** `BotWorker` runs work items in order. A 5-second chat check holds up other admins' clicks for that long. With at most 10 people on the BotFather allow-list this is fine, and it removes every flow race.
- **Lists page at 8 items.** Settings put no cap on destinations, and Telegram allows 100 inline buttons. Destination, library and detected-chat lists show 8 per page with `‹ Prev` / `Next ›`.
- **Copy:**
  - Every string the spec quotes is used verbatim; each task's table marks those with §.
  - Copy the spec doesn't fix is the implementer's, in the same tone: short, plain, sentence case. It still goes into `en.json` and `StringKeys`.
- **A prompt's expiry deletes nothing.** An expired reply keyboard can still be tapped on the client. The tap arrives as a `chat_shared` or "💬 Right here" with no live flow, and gets §6.2's "That picker expired" answer with `ReplyKeyboardRemove`.
- **Plan 5 builds the dashboard UI** for detected chats and the manual block. This plan adds their API fields and endpoints only.

## Global Constraints

Plans 1–3's Global Constraints apply unchanged. That covers:
- identity, two builds, analyzers with zero warnings and one type per file;
- host safety, no static mutable state, the zip whitelist;
- Telegram.Bot used only through `ITelegramSender`/`BotSupervisor`, redaction, strings from `en.json`;
- PascalCase API JSON, the `export PATH="$HOME/.dotnet:$PATH" &&` prefix;
- one commit per task, made with `git -c user.name=Daniil -c user.email=igatorr19@gmail.com commit` plus the harness's trailer lines.

In addition, from the spec:
- **Private chats only (§6.4).** In groups and channels the bot ignores every command and message. It acts only on `my_chat_member`, the supergroup-migration service message (`migrate_to_chat_id`), and its own sends.
- **Reply-keyboard messages are never edited (F34).** They are separate throwaway messages. The wizard (topic, preset, done) is one inline-keyboard message that gets edited.
- **Callback data is at most 64 bytes and versioned (§14.1).** The format is `1|<action>|<arg>|…`. Anything else is an expired menu.
- **Picker requests (§6.2):**
  - channel: `request_chat {chat_is_channel=true, bot_administrator_rights={can_post_messages, can_edit_messages}, user_administrator_rights=same, request_title=true}`;
  - group: `{chat_is_channel=false, bot_is_member=true, request_title=true}`;
  - topic promotion: `{chat_is_channel=false, chat_is_forum=true, bot_administrator_rights={can_manage_topics}, user_administrator_rights={can_manage_topics}, request_title=true}`.

  Each picker button gets a fresh `request_id` (a positive `int`).
- **Check retries (§6.2 step 2):** `getChat` + `getChatMember(botId)` at t = 0, 1.5, 3 and 5 s, stopping at the first pass. A detected chat's *Set up* checks once, without retries.
- **Expiries and caps (§10.3, §6.2, §6.4, §6.5):**

  | What | Expiry | Cap → on overflow |
  |---|---|---|
  | flows | 24 h | 200 → evict the least recently updated |
  | an unanswered prompt | 10 min, then counts as a setup failure | — |
  | managers | 90 days | 50 → evict oldest |
  | detected chats | — | 50 → evict oldest |
  | setup failures | 7 days | 50 → evict oldest |

  A detected chat stays hidden for its first **30 s**. Every eviction counts in `status.counters.evictions`.
- **Manual-block gate (§6.5):** unlocked when any setup failure in the last 7 days, any destination whose health isn't OK, or any visible detected chat exists.
- **Resets (§10.4):** both the different-bot reset and Disconnect clear `flows`, `managers`, `detectedChats` and `setupFailures`.

## Review Focus

These are the five inputs the spec implies but its happy paths don't exercise, most likely first. Each has a test in the task that owns the code:

1. **A redelivered update batch** (a crash or an in-process restart before the offset was confirmed, §9.2). The same `chat_shared`, callback or `/start` arrives twice. Expected: one destination, one test message, one wizard; the second callback is answered as expired or is a no-op. Tests:
   - `OnboardingFlowTests.RedeliveredChatShared_AddsOnce` (Task 8);
   - `UpdateRouterTests.RedeliveredCallback_IsAnsweredAndHarmless` (Task 4).
2. **An old menu message clicked after settings changed elsewhere** (the dashboard, another admin, another bot screen). Expected: the action applies to the current settings and never restores an older snapshot. A removed destination's screen says it is gone. Tests:
   - `DestinationScreensTests.ToggleEvent_AppliesToCurrentSettings`;
   - `DestinationScreensTests.RemovedDestination_SaysGone` (Task 6).
3. **A message the bot wants to edit is gone or can't be edited** (the user deleted it, or it carries a reply keyboard). Expected: the bot sends a new message instead and continues; the flow's `wizardMessageId` follows it. Test: `ScreenSenderTests.EditFails_SendsNew_AndReportsTheNewId` (Task 5).
4. **Chat titles, topic names and library names with `& < >` or emoji, and long lists** (30 destinations, 40 libraries). Expected: everything is escaped in HTML and button labels, labels are cut to 40 code points, and lists page at 8. Tests:
   - `MenuScreensTests.HostileTitles_AreEscaped_AndCut`;
   - `DestinationScreensTests.ThirtyDestinations_PageAtEight` (Tasks 5, 6).
5. **Telegram fails in the middle of a flow** (a transport error or 5xx on `getChat`, `createForumTopic` or `sendMessage`). Expected:
   - the work item's exception is logged and recorded in `lastErrors`, and the worker keeps running;
   - the user gets the failure message with `[Try again] [Advanced options]` wherever a send is still possible;
   - the flow is never stuck: `/menu` always answers.

   Tests:
   - `BotWorkerTests.ThrowingItem_IsLoggedAndTheNextRuns` (Task 4);
   - `OnboardingFlowTests.TransportErrorDuringCheck_ReportsFailure` (Task 8).

---

## File Structure

```
src/Jellyfin.Plugin.TgJelly/
  Bot/              OutgoingPrompt, UpdateRouter (replaces BasicUpdateHandler), BotWorkQueue, BotWorker, BotMemory,
                    ManagerNotifier
  Bot/Screens/      CallbackData, Screen, ScreenSender, Paging, FlowEngine, MenuScreens, DestinationScreens,
                    SettingsScreens, StatusScreen
  Bot/Flows/        OnboardingFlow, TopicFlow, DetectedChatFlow, PickerKind, ChatCheckRetrier
  Destinations/     DestinationCreator, CreateOutcome, CreateResult, DestinationMigration, SetupGate
  State/Documents/  FlowState, DetectedChat, SetupFailure, SetupFailureKind (+ StateDocument sections)
  Api/              DetectedChatsController, DetectedChatDto, SetUpDetectedChatRequest (+ DestinationsDto fields)
tests/Jellyfin.Plugin.TgJelly.Tests/
  Fixtures/Telegram/*.json, Fakes/TelegramFixtures, Fakes/BotRig
  Bot/ Bot/Screens/ Bot/Flows/ Destinations/ Api/   tests per unit
tests/integration/botapi_stub.py (injected updates), smoke.py (picker onboarding step)
```

---

### Task 1: New Telegram calls in the sender, the fake Bot API and the stub

**Files:**
- Create: `Bot/OutgoingPrompt.cs`
- Modify: `Bot/ITelegramSender.cs`, `Bot/TelegramSender.cs`, `tests/…/Fakes/FakeBotApi.cs`, `tests/integration/botapi_stub.py`, `tests/integration/test_botapi_stub.py`
- Test: `tests/…/Bot/TelegramSenderTests.cs`

**Interfaces:**
- Produces:
  - `sealed record OutgoingPrompt(long ChatId, string Html, ReplyMarkup Markup)`, where `Markup` is a `ReplyKeyboardMarkup`, `ReplyKeyboardRemove` or `ForceReplyMarkup`;
  - `ITelegramSender.SendPromptAsync(OutgoingPrompt prompt, CancellationToken ct) : Task<Message>` (HTML parse mode);
  - `ITelegramSender.DeleteMessageAsync(long chatId, int messageId, CancellationToken ct) : Task<bool>`. A 400 whose description contains `message to delete not found` or `message can't be deleted` returns `false`; other errors throw;
  - `ITelegramSender.CreateForumTopicAsync(long chatId, string name, CancellationToken ct) : Task<int>` (the `message_thread_id`);
  - `FakeBotApi` answers `deleteMessage` with `true` and `createForumTopic` with `{"message_thread_id":<next, from 101>,"name":…,"icon_color":7322096}` by default;
  - the stub adds `deleteMessage` to `TRUE_METHODS` and answers `createForumTopic` the same way.

- [ ] **Step 1: Write the failing tests**
  - `TelegramSenderTests.SendPrompt_SendsTheReplyKeyboard`: a prompt with one `KeyboardButton.WithRequestChat(new KeyboardButtonRequestChat { RequestId = 7, ChatIsChannel = true, RequestTitle = true })` and `ResizeKeyboard = true`, `OneTimeKeyboard = true`. The recorded `sendMessage` body has `parse_mode = "HTML"` and `reply_markup.keyboard[0][0].request_chat.request_id == 7`.
  - `DeleteMessage_TreatsNotFoundAsFalse`: `On("deleteMessage", Error(400, "Bad Request: message to delete not found"))` → `false`. With `Error(400, "Bad Request: chat not found")` it throws `ApiRequestException`.
  - `CreateForumTopic_ReturnsTheThreadId` → `101`; the body has `name == "Jellyfin"`.
  - `test_botapi_stub.py`: `test_delete_message_is_true`, `test_create_forum_topic_returns_a_thread`.
- [ ] **Step 2: Run, expect failure** (the build lacks the new members; the stub returns 404).
- [ ] **Step 3: Implement** with named arguments, as in the existing sender methods.
- [ ] **Step 4: Run all tests** (`dotnet test …` and `python3 -m unittest discover -s tests/integration -p 'test_*.py'`). PASS.
- [ ] **Step 5: Commit**: `feat: prompt, delete and forum-topic calls in the sender, the fake Bot API and the stub`

---

### Task 2: Bot state: flows, managers, detected chats and setup failures

**Files:**
- Create: `State/Documents/FlowState.cs`, `DetectedChat.cs`, `SetupFailure.cs`, `SetupFailureKind.cs`, `Bot/BotMemory.cs`
- Modify: `State/Documents/StateDocument.cs`, `Bot/BotStateReset.cs`, `PluginServiceRegistrator.cs`
- Test: `tests/…/Bot/BotMemoryTests.cs`, extend `Bot/BotStateResetTests.cs`

**Interfaces:**
- Produces:
  - `sealed class FlowState`:
    - `string Screen = ""`, `Dictionary<string,string> Params`;
    - `int? WizardMessageId`, `int? PromptMessageId`, `int? CheckingMessageId`;
    - `DateTimeOffset? PromptSentAt`, `List<int> PickerRequestIds`, `DateTimeOffset UpdatedAt`.

    `Params` keys used by later tasks: `kind` (`channel|group|here|topic`), `chatId`, `title`, `threadId`, `destinationId`, `input` (`publicUrl|topicLink`).
  - `sealed class DetectedChat { long ChatId; string Title = ""; ChatKind Kind; DateTimeOffset DetectedAt; }`
  - `enum SetupFailureKind { CheckFailed, PromptExpired, TopicFailed, TopicNotPickable }`; `sealed class SetupFailure { SetupFailureKind Kind; DateTimeOffset At; }`
  - `StateDocument`:
    - `Dictionary<string, FlowState> Flows` (key = private chat ID, invariant culture);
    - `Dictionary<string, DateTimeOffset> Managers`;
    - `List<DetectedChat> DetectedChats`;
    - `List<SetupFailure> SetupFailures`.
  - `sealed class BotMemory(StateStore state, TimeProvider time)`, a singleton. Every method reads or writes under `StateStore.Update`/`Read`, and returns copies:
    - `const int FlowsCap = 200, ManagersCap = 50, DetectedCap = 50, FailuresCap = 50`;
    - `static readonly TimeSpan FlowTtl = 24 h, PromptTtl = 10 min, ManagerTtl = 90 days, FailureTtl = 7 days, DetectedGrace = 30 s`;
    - flows: `FlowState? GetFlow(long chatId)`, `void SaveFlow(long chatId, FlowState flow)` (sets `UpdatedAt = now`), `void ClearFlow(long chatId)`;
    - `int NextRequestId()`: an increasing positive int, stored as `state.telegram.lastRequestId`, which this task adds;
    - managers: `void TouchManager(long chatId)`, `IReadOnlyList<long> RecentManagers()`, `void RemoveManager(long chatId)`;
    - detected chats: `void RecordDetected(long chatId, string title, ChatKind kind)` (an upsert keeps the first `DetectedAt`), `void RemoveDetected(long chatId)`, `IReadOnlyList<DetectedChat> VisibleDetected(IReadOnlyCollection<long> destinationChatIds)` (older than `DetectedGrace` and not a destination, oldest first);
    - failures: `void RecordFailure(SetupFailureKind kind)`, `bool HasRecentFailure()`;
    - `IReadOnlyList<long> Prune()`: expires flows, managers and failures; returns the chat IDs whose prompt just passed `PromptTtl`. For those it clears `PromptMessageId`, `PromptSentAt` and `PickerRequestIds`, and records a `PromptExpired` failure. Caps are applied on every write, oldest first, each counted in `evictions`; expiry removals are not counted.
  - `BotStateReset.ForDifferentBot` and `ForDisconnect` also clear `Flows`, `Managers`, `DetectedChats` and `SetupFailures`.

- [ ] **Step 1: Write the failing tests** (`BotTestHarness` + its `FakeTimeProvider`)
  - `Flow_RoundTrips_AndExpiresAfter24h`.
  - `Flows_Cap200_EvictsLeastRecentlyUpdated_AndCounts`.
  - `NextRequestId_Increases_AndSurvivesReload` (a `FlushAll` and a new `StateStore`).
  - `Managers_90Days_Cap50`.
  - `Detected_HiddenFor30s_AndNeverForADestination`.
  - `Detected_Upsert_KeepsFirstSeen`.
  - `Failures_7Days`.
  - `Prune_ExpiresPromptsAfter10Min_AndRecordsAFailure_Once`.
  - `BotStateResetTests.BothResets_ClearTheBotSections`.
- [ ] **Step 2: Run, expect failure.**
- [ ] **Step 3: Implement.** Register `BotMemory`.
- [ ] **Step 4: Run all tests.** PASS.
- [ ] **Step 5: Commit**: `feat: persisted bot flows, managers, detected chats and setup failures`

---

### Task 3: Shared destination creation and migration

**Files:**
- Create: `Destinations/DestinationCreator.cs`, `CreateOutcome.cs`, `CreateResult.cs`, `DestinationMigration.cs`
- Modify: `Api/DestinationsController.cs` (`Add` uses the creator), `Delivery/OutboxDelivery.cs` (`Migrate` moves out), `PluginServiceRegistrator.cs`
- Test: `tests/…/Destinations/DestinationCreatorTests.cs`, `DestinationMigrationTests.cs`; the existing controller and delivery tests stay green unchanged except for constructor arguments

**Interfaces:**
- Produces:
  - `enum CreateOutcome { Added, AlreadyExists, Conflict, Invalid }`;
  - `sealed record CreateResult(CreateOutcome Outcome, Destination? Destination, TestSendResult? Test, IReadOnlyList<SettingsError> Errors, PluginConfiguration Configuration)`;
  - `sealed class DestinationCreator(SettingsStore settings, HealthService health, DestinationTester tester, ILogger<DestinationCreator> logger)` with `Task<CreateResult> CreateAsync(ChatCheckResult check, int threadId, string[] events, PresetKind preset, long? expectedRevision, CancellationToken ct)`. It is the controller's existing logic after the check, unchanged:
    - the duplicate rule, which re-checks the existing destination;
    - `UpdateIf`, with the race back to `AlreadyExists`;
    - `SetChecked`, then the test send.

    `PhotosBlocked` reaches health through `SetChecked`, as today.
  - `static Destination? DestinationCreator.Find(PluginConfiguration c, long chatId, int threadId)`;
  - `sealed class DestinationMigration(SettingsStore settings, Outbox outbox, HealthService health)` with `Destination? Migrate(long oldChatId, long newChatId)`. It is `OutboxDelivery.Migrate`'s body (rewrite, or merge on collision), applied to **every** destination of the old chat, i.e. each of its topics. It returns the destination the first one became, or `null` when none matched.

- [ ] **Step 1: Write the failing tests**
  - `DestinationCreatorTests`: `Added_SavesChecksAndTests`, `Duplicate_ReChecksAndReturnsExisting`, `RaceWithAnotherAdd_IsAlreadyExists`, `StaleRevision_IsConflict`, `CustomEvents_AreSavedAsGiven`.
  - `DestinationMigrationTests`: `Rewrite_MovesEveryTopicOfTheChat`, `Collision_MergesEventsSilentGroupsAndLibraries_RepointsTheOutbox`, `UnknownChat_ReturnsNull`.
- [ ] **Step 2: Run, expect failure.**
- [ ] **Step 3: Implement**, then make `DestinationsController.Add` and `OutboxDelivery` call the new classes. This is a pure move: their existing tests are the regression net.
- [ ] **Step 4: Run all tests.** PASS.
- [ ] **Step 5: Commit**: `refactor: share destination creation and chat migration between the API, delivery and the bot`

---

### Task 4: UpdateRouter, the bot work queue, callback data and Telegram fixtures

**Files:**
- Create: `Bot/UpdateRouter.cs`, `Bot/BotWorkQueue.cs`, `Bot/BotWorker.cs`, `Bot/IBotFlows.cs`, `Bot/Screens/CallbackData.cs`, `Bot/Screens/FlowEngine.cs` (shell), `tests/…/Fakes/TelegramFixtures.cs`, `tests/…/Fixtures/Telegram/*.json`
- Modify: `PluginServiceRegistrator.cs` (`IUpdateHandler` → `UpdateRouter`; `BotWorker` hosted, registered **before** `BotSupervisor` so it stops after it), the test csproj (`<None Update="Fixtures/**" CopyToOutputDirectory="PreserveNewest" />`)
- Delete: `Bot/BasicUpdateHandler.cs` and its tests; their cases move to `UpdateRouterTests`
- Test: `tests/…/Bot/UpdateRouterTests.cs`, `Bot/BotWorkerTests.cs`, `Bot/Screens/CallbackDataTests.cs`, extend `PluginServiceRegistratorTests`

**Interfaces:**
- Produces:
  - `interface IBotFlows`:
    - `Task OnPrivateMessageAsync(Message message, CancellationToken ct)`;
    - `Task OnCallbackAsync(CallbackQuery query, CancellationToken ct)`;
    - `Task OnChatMemberAsync(ChatMemberUpdated update, CancellationToken ct)`;
    - `Task TickAsync(CancellationToken ct)`.

    This task creates `Bot/Screens/FlowEngine.cs` as a shell that implements it with no-op methods, and registers it as `IBotFlows`. Task 5 fills it in.
  - `sealed class BotWorkQueue(ILogger<BotWorkQueue> logger)`:
    - `const int Capacity = 500`;
    - `bool Enqueue(string name, Func<CancellationToken, Task> work)`. When full, it drops the new item and logs a warning;
    - `internal Task RunPendingAsync(CancellationToken ct)`, used by tests;
    - `Task WaitAsync(TimeSpan timeout, CancellationToken ct)`.
  - `sealed class BotWorker(InstanceGate gate, CompatResult compat, BotWorkQueue queue, IBotFlows flows, StateStore state, TimeProvider time, ILogger<BotWorker> logger) : BackgroundService`:
    - it runs items in order, each in try/catch: it logs, calls `RecordError("bot", …)`, and goes on;
    - it calls `flows.TickAsync` every 30 s;
    - `StopAsync` runs what is queued, for at most 2 s.
  - `sealed class UpdateRouter(BotWorkQueue queue, IBotFlows flows, BotConnection connection, HealthService health, DestinationMigration migration, ILogger<UpdateRouter> logger) : IUpdateHandler`, with these rules:
    - **CallbackQuery:** `AnswerCallbackAsync` at once ("query is too old" ignored). If `query.Message?.Chat.Type == Private`, enqueue `flows.OnCallbackAsync`.
    - **Message in a private chat** (not from a bot): enqueue `flows.OnPrivateMessageAsync`.
    - **Message in a group or supergroup with `MigrateToChatId`:** enqueue `migration.Migrate(message.Chat.Id, newId)`, then `health.RequestCheckForChat(newId)`.
    - **Any other group or channel message:** ignored.
    - **MyChatMember:** `health.RequestCheckForChat(chat.Id)` (plan 2's behaviour), then enqueue `flows.OnChatMemberAsync`.
    - **Anything else:** ignored. The router never throws (a top-level try/catch logs).
  - `static class CallbackData`:
    - `const int MaxBytes = 64`, `const string Version = "1"`;
    - `string Format(string action, params string[] args)`, which throws `ArgumentException` when the UTF-8 length > 64 or any part contains `|`;
    - `bool TryParse(string? data, out string action, out IReadOnlyList<string> args)`, which is false for another version, an empty action or `null`.
  - `TelegramFixtures.Update(string name, params (string Placeholder, string Value)[] values) : Update`. It reads `Fixtures/Telegram/<name>.json`, replaces `{{placeholder}}` tokens, and parses with `JsonBotAPI.Options`. The fixtures follow §14.2:
    - `private_text`, `private_command_start`;
    - `chat_shared`;
    - `callback_query`;
    - `my_chat_member_channel_admin`, `my_chat_member_group_member`, `my_chat_member_kicked`, `my_chat_member_private_blocked`, `my_chat_member_private_unblocked`;
    - `group_text`, `group_migrate_to`;
    - `get_chat_forum`, `get_chat_group_no_photos`, `get_chat_member_restricted_no_photos`, `get_chat_member_admin_topics`, `get_chat_member_admin_no_topics`;
    - `forum_topic_created`.

- [ ] **Step 1: Write the failing tests**
  - `CallbackDataTests`:
    - `RoundTrips`;
    - `RejectsOtherVersions`;
    - `Over64Bytes_Throws`;
    - `EveryActionThisPlanUses_FitsWithMaxLengthArgs`: a `[Theory]` over the action list Task 5 defines in `FlowEngine.Actions`, with 8-char destination IDs, 32-char library IDs, the longest event key and page `99`.
  - `UpdateRouterTests` (with a recording `IBotFlows` fake):
    - `Callback_IsAnsweredBeforeTheWorkRuns`;
    - `PrivateMessage_IsQueued`;
    - `GroupAndChannelMessages_AreIgnored`;
    - `MigrateToChatId_RewritesTheDestination`;
    - `MyChatMember_ReChecksAndQueues`;
    - `RedeliveredCallback_IsAnsweredAndHarmless`: the same `callback_query` twice → two answers, two queued calls, no throw;
    - `ThrowingFlows_NeverEscapeTheRouter`.
  - `BotWorkerTests`:
    - `RunsInOrder`;
    - `ThrowingItem_IsLoggedAndTheNextRuns`;
    - `Full_DropsNew`;
    - `Stop_RunsWhatIsQueued_WithinTwoSeconds`;
    - `Incompatible_NeverRuns`.
  - `PluginServiceRegistratorTests`: `BotWorker` sits directly before `BotSupervisor` in the hosted order, and `IUpdateHandler` resolves to `UpdateRouter`.
- [ ] **Step 2: Run, expect failure.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run all tests.** PASS.
- [ ] **Step 5: Commit**: `feat: UpdateRouter, the bot work queue and versioned callback data`

---

### Task 5: FlowEngine core: screens, `/menu`, `/help`, `/status`, stale input and managers

**Files:**
- Create: `Bot/Screens/Screen.cs`, `ScreenSender.cs`, `Paging.cs`, `MenuScreens.cs`, `StatusScreen.cs`, `tests/…/Fakes/BotRig.cs`
- Modify: `Bot/Screens/FlowEngine.cs` (Task 4's shell), `Strings/en.json`, `tests/…/Strings/StringKeys.cs`
- Test: `tests/…/Bot/Screens/ScreenSenderTests.cs`, `MenuScreensTests.cs`, `StatusScreenTests.cs`, `FlowEngineTests.cs`

**Interfaces:**
- Produces:
  - `sealed record Screen(string Html, InlineKeyboardMarkup? Buttons)`.
  - `sealed class ScreenSender(BotConnection connection)` with `Task<int> ShowAsync(long chatId, Screen screen, int? editMessageId, CancellationToken ct)`:
    - it edits when an ID is given, and returns that ID;
    - it sends a new message when no ID is given, or when the edit fails with classifier row 10 (`message to edit not found`, `can't be edited`), and returns the new ID;
    - `message is not modified` counts as success.
  - `static class Paging`: `const int PageSize = 8`; `IReadOnlyList<T> Page<T>(IReadOnlyList<T> items, int page)`; `InlineKeyboardButton[]? NavRow(int page, int count, Func<int, string> callback)` (null when one page).
  - `string Label(string text)`: escaped where HTML is used, and cut to 40 code points with `…` for button labels. It lives on `MenuScreens` as `internal static`.
  - `sealed class FlowEngine : IBotFlows`. Its constructor takes `BotConnection`, `BotMemory`, `ScreenSender`, `SettingsStore`, `StringTable`, `Formatter`, `TimeProvider`, `ILogger<FlowEngine>` plus the screen and flow classes as later tasks add them.
    - `static IReadOnlyList<string> Actions`: every callback action, used by `CallbackDataTests`.
    - Actions this task adds: `m` (main menu), `st` (status), `dl` (destination list, `page`), `noop`.
  - Behaviour:
    - every private message or callback calls `memory.TouchManager(chatId)`;
    - `/menu` → the main menu as a new message;
    - `/status` → the status screen;
    - `/help` → help text and the dashboard path ("Dashboard → Plugins → Telegram");
    - `/test` → Task 6's picker; until Task 6 exists, `/menu`;
    - `/start` with no destinations → Task 8's onboarding; until Task 8 exists, `/menu`;
    - `/start setup|add` → onboarding;
    - a plain `/start` when destinations exist, an unknown payload, unrecognised text or another command → the main menu headed "TgJelly is set up: N destinations" (§6.2);
    - a callback whose data doesn't parse → answer text "That menu expired" (sent as a new message), then the main menu;
    - `TickAsync` → `memory.Prune()`, plus Task 13's notifier.
  - `MenuScreens.Main(int destinations, bool hasDetected, bool gateOpen)`. Buttons: `📬 Destinations (n)`, `➕ Add destination`, `⚙️ Settings`, `🩺 Status`, plus `Chats added outside setup (n)` when n > 0 (Task 11 wires `hasDetected`).
  - `StatusScreen.Build()`: connection state and message, compat, each destination's health badge, pending media, Outbox count, the last 3 errors. Built from the same services `TgJellyController.GetStatus` uses.
  - `BotRig` (tests): a `BotTestHarness`, connected, with a real `FlowEngine` and a `Deliver(Update)` helper that runs the router and then `RunPendingAsync`.
- `en.json` (§ = verbatim from the spec):

  | Key | Copy |
  |---|---|
  | `menu.title` | `🏠 Menu` |
  | `menu.setUp.one` / `.other` | `TgJelly is set up: {count} destination` / `TgJelly is set up: {count} destinations` (§6.2) |
  | `menu.destinations` | `📬 Destinations ({count})` § |
  | `menu.add` | `➕ Add destination` § |
  | `menu.settings` | `⚙️ Settings` § |
  | `menu.status` | `🩺 Status` § |
  | `menu.detected` | `Chats added outside setup ({count})` § |
  | `menu.expired` | `That menu expired` |
  | `menu.prev` / `menu.next` / `menu.back` | `‹ Prev` / `Next ›` / `‹ Back` |
  | `help.text` | the implementer's: what TgJelly does and where the dashboard is |
  | `status.*` | the implementer's |

- [ ] **Step 1: Write the failing tests**
  - `ScreenSenderTests`:
    - `NoId_Sends`;
    - `Edit_Edits`;
    - `EditFails_SendsNew_AndReportsTheNewId`;
    - `NotModified_IsSuccess`.
  - `MenuScreensTests`:
    - `Main_ShowsDetectedOnlyWhenAny`;
    - `HostileTitles_AreEscaped_AndCut`: a title of `<b>A&B</b>` plus 60 × `😀` → escaped in HTML, and the button label is 40 code points ending in `…`.
  - `FlowEngineTests`:
    - `Menu_SendsTheMainMenu_AndTouchesTheManager`;
    - `PlainStartWithDestinations_ShowsSetUpMenu`;
    - `UnknownText_ShowsSetUpMenu`;
    - `UnparseableCallback_SaysExpired_AndShowsMenu`;
    - `Help_MentionsTheDashboardPath`;
    - `Status_ListsHealthAndCounts`;
    - `Tick_PrunesTheMemory`.
- [ ] **Step 2: Run, expect failure.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run all tests.** PASS.
- [ ] **Step 5: Commit**: `feat: bot menu, status and help screens`

---

### Task 6: Destination screens and `/test`

**Files:**
- Create: `Bot/Screens/DestinationScreens.cs`
- Modify: `Bot/Screens/FlowEngine.cs`, `Strings/en.json`, `StringKeys.cs`
- Test: `tests/…/Bot/Screens/DestinationScreensTests.cs`

**Interfaces:**
- Consumes: `SettingsStore`, `HealthService`, `DestinationTester`, `ILibraryAccess`, `EventCatalog`, `BotConnection.Sender.LeaveChatAsync`, `Outbox.RemoveDestination`.
- Produces the actions below (args in brackets). Each one re-reads `settings.Current`, applies the change with `settings.Update(null, …)` to the destination found by ID at save time, and renders again on the clicked message.

  | Action | Does |
  |---|---|
  | `d [id]` | the destination screen: title, kind, health badge, preset, groups, `Enabled`; buttons `Events`, `Libraries`, `Silent groups`, `Send test`, `Check again`, `Pause`/`Resume`, `Remove`, `‹ Back` |
  | `de [id, page]` / `det [id, key]` | event switches (✅/▫️ per key, grouped media/security/server); a toggle sets `Preset` to the matching preset if the new set equals one of them, else `Custom` |
  | `dlb [id, page]` / `dlt [id, libN]` | library switches from `GetVirtualFolders()`, plus `All libraries` (empty filter) and a `(removed)` line per removed ID (`LibraryFilter.Removed`) |
  | `dsg [id]` / `dsgt [id, group]` | silent-group switches for media/security/server |
  | `dt [id]` | `DestinationTester.SendTestAsync`; shows ✅ or the error on the screen |
  | `dc [id]` | `health.CheckNowAsync`; shows the new badge |
  | `dp [id]` | flip `Enabled` |
  | `dr [id]` / `drx [id, leave(0/1)]` | asks "Also make the bot leave the chat?" (§6.3) with `[Remove] [Remove and leave] [Cancel]`. Then it removes the destination from settings, calls `outbox.RemoveDestination`, `health.Remove`, and `LeaveChatAsync` when asked, ignoring errors from `LeaveChatAsync` |
  | `tp [page]` | `/test`'s picker: one button per destination → `dt` |

  A screen for an ID that no longer exists says "This destination was removed." with `‹ Back`.

- [ ] **Step 1: Write the failing tests**
  - `List_ShowsHealthBadges`.
  - `ThirtyDestinations_PageAtEight`: 4 pages, and `Next ›` on the last page is absent.
  - `ToggleEvent_AppliesToCurrentSettings`: the dashboard changes `SilentGroups` between rendering and the click; after the click both changes are present.
  - `ToggleEvent_BackToAPreset_SetsThatPreset`.
  - `Libraries_ShowRemovedIds_AndAllLibraries`.
  - `Pause_FlipsEnabled`.
  - `Remove_AsksAboutLeaving_ThenLeavesWhenAsked`.
  - `RemovedDestination_SaysGone`.
  - `SendTest_ShowsTheResult`.
  - `CheckAgain_ShowsTheNewBadge`.
  - `TestCommand_ListsDestinations`.
- [ ] **Step 2: Run, expect failure.**  - [ ] **Step 3: Implement.**  - [ ] **Step 4: Run all tests.** PASS.
- [ ] **Step 5: Commit**: `feat: destination screens in the bot`

---

### Task 7: Settings screens

**Files:**
- Create: `Bot/Screens/SettingsScreens.cs`
- Modify: `Bot/Screens/FlowEngine.cs`, `Strings/en.json`, `StringKeys.cs`
- Test: `tests/…/Bot/Screens/SettingsScreensTests.cs`

**Interfaces:**
- Consumes: `SettingsStore`, `LinkBuilder.Suggest()`/`Normalize`, `PublicUrlRules.IsValid`.
- Produces these actions:
  - `s`: the settings menu;
  - `sm` / `smt [toggle]`: message style switches for the six `MessageOptions` booleans (§8.4);
  - `su` / `sut` / `sum`: update checks on/off and mode `SameMajor`/`AnyNewer`;
  - `sp` / `spu` / `spt` / `spc`: public URL:
    - `sp` shows the current URL with its validity, and the normalized suggestion marked "verify";
    - `spu` saves the suggestion;
    - `spt` sends a `ForceReplyMarkup` prompt "Send the public URL" and stores `flow.Params["input"] = "publicUrl"`;
    - `spc` clears the URL.
  - A private message that replies to that prompt (or any text while `input == publicUrl`) is normalized with `LinkBuilder.Normalize`:
    - valid → it is saved and the settings screen is shown as a new message;
    - invalid → the bot explains §8.3's rule (absolute http(s), the host has a dot, no IPv6 literal) and asks again.
- **§15 Q2's answer changes `PublicUrlRules.IsValid`:** an IPv6-literal host is now invalid (Telegram answers `BUTTON_URL_INVALID`). `http://` and private IPv4 stay valid. Update `PublicUrlRulesTests`/`LinkBuilderTests` (IPv6 rows flip to invalid) and the dashboard hint string if it mentions IPv6.

- [ ] **Step 1: Write the failing tests**
  - `MessageToggle_FlipsOnlyThatOption`.
  - `UpdateMode_Switches`.
  - `PublicUrl_UseSuggested_SavesTheNormalizedSuggestion`.
  - `PublicUrl_TypeAnother_Valid_Saves`.
  - `PublicUrl_TypeAnother_NoDotHost_IsRejectedAndAskedAgain`.
  - `PublicUrl_Clear`.
  - `PublicUrlRulesTests.Ipv6Literal_IsInvalid` (`https://[2001:db8::1]:8096` and `http://[fd00::1]`); `PrivateIpv4_IsValid`.
  - `TypedUrlWithoutAFlow_IsTreatedAsUnknownText`.
- [ ] **Step 2–4: fail, implement, pass.**
- [ ] **Step 5: Commit**: `feat: settings screens in the bot; IPv6 public URLs are invalid (§15 Q2)`

---

### Task 8: Onboarding: picker prompt, check, preset and Done (non-forum chats and "Right here")

**Files:**
- Create: `Bot/Flows/OnboardingFlow.cs`, `Bot/Flows/PickerKind.cs`, `Bot/Flows/ChatCheckRetrier.cs`
- Modify: `Bot/Screens/FlowEngine.cs`, `Strings/en.json`, `StringKeys.cs`
- Test: `tests/…/Bot/Flows/OnboardingFlowTests.cs`, `ChatCheckRetrierTests.cs`

**Interfaces:**
- Consumes: `BotMemory`, `ScreenSender`, `DestinationChecker`, `DestinationCreator`, `HealthService`, `EventCatalog`, `TestSendResult`.
- Produces:
  - `enum PickerKind { Channel, Group, TopicPromotion }`.
  - `sealed class ChatCheckRetrier(DestinationChecker checker, TimeProvider time)`:
    - `static readonly TimeSpan[] Schedule = [0, 1.5 s, 3 s, 5 s]` (offsets from the first try);
    - `Task<ChatCheckResult> CheckAsync(long chatId, bool retry, CancellationToken ct)`;
    - `internal Func<TimeSpan, CancellationToken, Task> Delay`, defaulting to `Task.Delay(d, time, ct)`.
  - `OnboardingFlow`:
    - `Task StartAsync(long chatId, CancellationToken ct)`;
    - `Task OnChatSharedAsync(Message message, CancellationToken ct)`;
    - `Task OnRightHereAsync(Message message, CancellationToken ct)`;
    - `Task OnCancelAsync(Message message, CancellationToken ct)`;
    - `Task OnPresetAsync(CallbackQuery query, string preset, CancellationToken ct)`;
    - `Task ContinueAfterCheckAsync(long privateChatId, ChatCheckResult check, int threadId, int? wizardMessageId, CancellationToken ct)`, which Tasks 9 and 11 reuse.
  - Actions: `op [preset]` (`Everything|NewMedia|AdminAlerts|Custom`), `oc [key]` (a custom toggle in the wizard), `ocd` (custom done), `oa` (add another place → `StartAsync`), `ot` (Try again → `StartAsync`), `ox` (Advanced options → Task 12's screen; until Task 12, the main menu).
- **Flow (§6.2):**
  1. `StartAsync` clears any earlier flow, takes three `NextRequestId()`s, and sends the prompt "Where should notifications go?" as a reply keyboard with four rows:
     - `📢 Choose a channel`;
     - `👥 Choose a group`;
     - `💬 Right here`;
     - `Cancel`.

     It saves `PromptMessageId`, `PromptSentAt` and `PickerRequestIds`, with `Params["requestChannel"]` and `["requestGroup"]` holding their IDs.
  2. A `chat_shared` whose `RequestId` is in the live flow's `PickerRequestIds`:
     1. send "Checking *Title*…" with `ReplyKeyboardRemove` and save `CheckingMessageId`;
     2. delete the prompt;
     3. `ChatCheckRetrier.CheckAsync(chatId, retry: true)`.

     A `chat_shared` with no live match gets "That picker expired" with `ReplyKeyboardRemove`, plus `[Add destination]` (§6.2).
  3. **Pass, not a forum:**
     - the duplicate rule (`DestinationCreator.Find`): "*Title* is already set up", a `health.CheckNowAsync`, then that destination's screen (Task 6), and the flow is cleared;
     - otherwise the wizard message "What should I send there?" `[Everything] [New media] [Admin alerts] [Custom…]`, sent as a new inline message; then delete the Checking message.

     **Pass, forum** → `TopicFlow` (Task 9).
  4. **Fail:**
     - delete the Checking message;
     - send the inline message: the check's `Message` + `[Try again] [Advanced options]`;
     - `RecordFailure(CheckFailed)`;
     - clear the picker IDs.
  5. **`💬 Right here`** (only while a flow is live): the destination is this private chat (`ChatKind.Private`, a check without retries). It continues at step 3, after removing the keyboard with a short "Right here." message that carries `ReplyKeyboardRemove`. **`Cancel`** removes the keyboard and shows `/menu`.
  6. **Preset `op`:**
     - `Custom` → the event switches with nothing selected (`oc`), then `ocd`;
     - otherwise `DestinationCreator.CreateAsync(check, threadId, EventCatalog.Expand(preset), preset, null)`;
     - then edit the wizard to "✅ Done: *Title* will receive *groups*. `[Add another place] [Menu]`" (§6.2), where *groups* is formatted like the test message;
     - a failed test send adds its error under the Done line;
     - the flow is cleared.

     The check result travels in `Params` (`chatId`, `title`, `kind`, `isForum`, `photosBlocked`, `threadId`). Before creating, the flow re-checks without retries, because the chat may have changed while the admin chose.
  7. **PhotosBlocked** adds §6.2's note to the Done message.
- `en.json` (§ = verbatim):

  | Key | Copy |
  |---|---|
  | `onboard.prompt` | `Where should notifications go?` § |
  | `onboard.channel` / `.group` / `.here` / `.cancel` | `📢 Choose a channel` § / `👥 Choose a group` § / `💬 Right here` § / `Cancel` § |
  | `onboard.checking` | `Checking <b>{title}</b>…` § |
  | `onboard.duplicate` | reuse `destination.duplicate` (`{title} is already set up`) § |
  | `onboard.preset` | `What should I send there?` § |
  | `onboard.preset.everything` / `.newMedia` / `.adminAlerts` / `.custom` | `Everything` / `New media` / `Admin alerts` / `Custom…` § |
  | `onboard.done` | `✅ Done: <b>{title}</b> will receive <i>{groups}</i>.` § |
  | `onboard.addAnother` / `onboard.menu` | `Add another place` § / `Menu` § |
  | `onboard.tryAgain` / `onboard.advanced` | `Try again` § / `Advanced options` § |
  | `onboard.expired` | `That picker expired` § |
  | `onboard.photosBlocked` | reuse `check.photosBlocked` |

- [ ] **Step 1: Write the failing tests** (`BotRig`, with `ChatCheckRetrier.Delay` recording its delays and returning at once)
  - `Start_SendsTheReplyKeyboard_WithFreshRequestIds`: assert the channel button's rights and `request_title`, the group button's `bot_is_member`, and that the IDs differ from a second `StartAsync`.
  - `ChannelShared_ChecksThenAsksForThePreset_AndDeletesCheckingAndPrompt`.
  - `ChatSharedBeforeMyChatMember_RetriesUntilThePass`: `getChatMember` answers `left` twice, then `administrator`; the delays recorded are 1.5 s, 1.5 s.
  - `ChatSharedAfterMyChatMember_PassesFirstTry`.
  - `CheckFails_DeletesChecking_OffersTryAgainAndAdvanced_RecordsFailure`.
  - `TransportErrorDuringCheck_ReportsFailure`.
  - `Preset_Creates_SendsTheTest_AndEditsTheWizardToDone`.
  - `Custom_StartsWithNothingSelected_ThenCreatesWithTheChosenKeys`.
  - `Duplicate_SaysAlreadySetUp_ReChecks_AndOpensTheDestination`.
  - `RedeliveredChatShared_AddsOnce`: the same update twice, and the same `op` callback twice → one destination and one test send.
  - `RightHere_UsesThisChat`.
  - `Cancel_RemovesTheKeyboard`.
  - `ExpiredPicker_SaysExpired_WithKeyboardRemove`.
  - `PromptExpiry_ViaTick_RecordsAFailure`.
  - `GroupWithoutPhotos_DoneMentionsPhotosBlocked`.
- [ ] **Step 2–4: fail, implement, pass.**
- [ ] **Step 5: Commit**: `feat: native-picker onboarding in the bot`

---

### Task 9: Forum topics

**Files:**
- Create: `Bot/Flows/TopicFlow.cs`
- Modify: `Bot/Flows/OnboardingFlow.cs`, `Bot/Screens/FlowEngine.cs`, `Strings/en.json`, `StringKeys.cs`
- Test: `tests/…/Bot/Flows/TopicFlowTests.cs`

**Interfaces:**
- Consumes: `OnboardingFlow.ContinueAfterCheckAsync`, `ITelegramSender.CreateForumTopicAsync`, `TopicLink.TryParse`, `DestinationChecker`, `BotMemory.RecordFailure`.
- Produces these actions:
  - `tg`: General, with `threadId = 0`. The duplicate rule runs for (chat, 0), then the preset step;
  - `tc`: create a "Jellyfin" topic. With `can_manage_topics` (from `getChatMember`), call `CreateForumTopicAsync(chat, "Jellyfin")` and use the returned thread ID. Without it, send the throwaway promotion prompt: a reply keyboard with the single `🗂 Allow topic creation` picker (`PickerKind.TopicPromotion`, a fresh request ID). Its `chat_shared` must name the same chat:
    - re-check;
    - delete the prompt;
    - create the topic.

    A failure of the promotion, the check or the creation records `TopicFailed` and edits the wizard to `[General topic] [Advanced options]`;
  - `te`: use an existing topic. It records `TopicNotPickable` and sends a `ForceReplyMarkup` prompt asking for the topic's link ("Long-press the topic → Copy link"), setting `Params["input"] = "topicLink"`. The reply must parse (`TopicLink.TryParse`) and name the picked chat, either by `-100…` ID or by the chat's username. Then a check with that thread ID runs (its probe is the `SendTypingAsync` the checker already does), followed by the duplicate rule for (chat, thread) and the preset step. A bad link explains why and asks again.

  The wizard for a forum is "This group has topics. Where should I post?" `[General topic] [Create a "Jellyfin" topic] [Use an existing topic…]` (§6.2 step 3).
- `en.json`: `topic.ask`, `topic.general` (`General topic` §), `topic.create` (`Create a "Jellyfin" topic` §), `topic.existing` (`Use an existing topic…` §), `topic.allow` (`🗂 Allow topic creation` §), `topic.linkPrompt`, `topic.badLink`, `topic.wrongChat`, `topic.failed`.

- [ ] **Step 1: Write the failing tests**
  - `Forum_AsksWhereToPost`.
  - `General_UsesThreadZero_AndAppliesTheDuplicateRule`.
  - `Create_WithTopicRights_CreatesAndUsesTheThread`.
  - `Create_WithoutRights_SendsThePromotionPicker_ThenCreates`.
  - `Promotion_ForAnotherChat_IsRejected`.
  - `PromotionFails_FallsBackToGeneralAndAdvanced_RecordsTopicFailed`.
  - `Existing_RecordsTopicNotPickable_AndAsksForTheLink`.
  - `ExistingLink_ForThisChat_ByIdAndByUsername_Works`.
  - `ExistingLink_ForAnotherChat_IsRejected`.
  - `ExistingTopic_AlreadyADestination_SaysSo`.
- [ ] **Step 2–4: fail, implement, pass.**
- [ ] **Step 5: Commit**: `feat: forum topics in onboarding`

---

### Task 10: Supergroup migration and redelivery hardening

**Files:**
- Modify: `Bot/UpdateRouter.cs`, `Bot/Flows/OnboardingFlow.cs`, `Bot/BotMemory.cs`
- Test: extend `UpdateRouterTests.cs`, `OnboardingFlowTests.cs`, `BotMemoryTests.cs`

**Interfaces:**
- A group upgraded to a supergroup sends two service messages: `migrate_to_chat_id` in the old chat and `migrate_from_chat_id` in the new one. Only the first is acted on.
- After a migration, these follow the chat to its new ID: detected chats, and any live flow whose `Params["chatId"]` is the old ID.
- Any wizard callback's handler must be idempotent:
  - a second `op` on a cleared flow is answered "That menu expired";
  - a second `tc` while a topic creation is running is ignored. The flow stores `Params["creating"] = "1"` before the call.

- [ ] **Step 1: Write the failing tests**
  - `MigrateFrom_IsIgnored`.
  - `Migration_MovesDetectedChatsAndLiveFlows`.
  - `DoubleCreateTopicClick_CreatesOnce`.
  - `SecondPresetClick_AfterDone_SaysExpired`.
- [ ] **Step 2–4: fail, implement, pass.**
- [ ] **Step 5: Commit**: `fix: follow chat migrations in bot state, idempotent wizard clicks`

---

### Task 11: Detected chats (bot and API)

**Files:**
- Create: `Bot/Flows/DetectedChatFlow.cs`, `Api/DetectedChatsController.cs`, `Api/DetectedChatDto.cs`, `Api/SetUpDetectedChatRequest.cs`
- Modify: `Bot/Screens/FlowEngine.cs`, `Bot/Screens/MenuScreens.cs`, `Destinations/DestinationCreator.cs` (gains a `BotMemory` constructor parameter), `Api/DestinationsDto.cs`, `Api/DestinationsController.cs` (`List`), `Strings/en.json`, `StringKeys.cs`
- Test: `tests/…/Bot/Flows/DetectedChatFlowTests.cs`, `tests/…/Api/DetectedChatsControllerTests.cs`, extend `DestinationsControllerTests.cs`

**Interfaces:**
- **Recording (`FlowEngine.OnChatMemberAsync`, §6.5).** A detected chat is recorded when:
  - the chat type is group, supergroup or channel;
  - the old status was `left` or `kicked` and the new one is `member` or `administrator`;
  - the chat isn't a destination;
  - no live flow of the private chat `update.From.Id` has picker IDs.

  `left`/`kicked` for a recorded chat removes it. A private chat's `kicked` removes that manager; `member` re-adds it.
- **Bot actions:**
  - `xl [page]`: the list "Detected: *Title*, set it up?" with `[Set up] [Dismiss]` per chat (§6.5 copy);
  - `xs [chatId]`: a single check, then `OnboardingFlow.ContinueAfterCheckAsync` (forum → Task 9, else the preset step);
  - `xd [chatId]`: dismiss.

  `MenuScreens.Main`'s detected button counts `VisibleDetected`.
- **API:**
  - `DestinationsDto` gains `IReadOnlyList<DetectedChatDto> DetectedChats` (`ChatId`, `Title`, `Kind`, `DetectedAt`) and `bool ManualUnlocked` (Task 12; `false` until then).
  - `POST DetectedChats/{chatId}` `{ExpectedRevision, Preset, ThreadId?}`:
    - check once; a failure is 400 with the check's message;
    - otherwise `DestinationCreator.CreateAsync`, then remove the detected chat;
    - the response is the same `AddDestinationResponse`.
  - `DELETE DetectedChats/{chatId}` dismisses it. Both endpoints use `ApiGuard`.
- **Removal also happens** when any path (picker, API, detected set-up) creates a destination for that chat: `DestinationCreator` calls `memory.RemoveDetected(chatId)` on `Added` and `AlreadyExists`.

- [ ] **Step 1: Write the failing tests**
  - `BotAddedToAGroup_IsRecorded_ShownAfter30s`.
  - `WhilePickerWaits_NotRecorded`.
  - `BecomesADestinationWithin30s_NeverShown`.
  - `Kicked_RemovesIt`.
  - `SetUp_ChecksOnce_ThenAsksForThePreset`.
  - `Dismiss_RemovesIt`.
  - `PrivateBlockAndUnblock_UpdateManagers`.
  - API: `GetDestinations_IncludesVisibleDetectedChats`, `PostDetected_CreatesAndRemoves`, `PostDetected_FailingCheck_Is400`, `DeleteDetected_Dismisses`.
- [ ] **Step 2–4: fail, implement, pass.**
- [ ] **Step 5: Commit**: `feat: chats added outside setup, in the bot and the API`

---

### Task 12: Setup-failure gate and the Advanced options screen

**Files:**
- Create: `Destinations/SetupGate.cs`
- Modify: `Bot/Screens/FlowEngine.cs`, `Api/DestinationsController.cs`, `Strings/en.json`, `StringKeys.cs`
- Test: `tests/…/Destinations/SetupGateTests.cs`, `tests/…/Bot/Screens/AdvancedScreenTests.cs`, extend `DestinationsControllerTests.cs`

**Interfaces:**
- Produces:
  - `sealed class SetupGate(BotMemory memory, HealthService health, SettingsStore settings)` with `bool IsOpen()`. It is open when any of these hold:
    - a recent failure;
    - any destination's health is not OK;
    - any visible detected chat.
  - `DestinationsDto.ManualUnlocked = gate.IsOpen()`.
  - Action `ox`, the Advanced options screen (§6.5):
    - `Add to a group` → URL `https://t.me/<bot>?startgroup=add`;
    - `Add to a channel` → URL `https://t.me/<bot>?startchannel&admin=post_messages+edit_messages`;
    - `Use an existing topic` → Task 9's link prompt; with no picked chat in the flow, the link's chat is checked directly;
    - `Open dashboard` → the text "Dashboard → Plugins → Telegram → Advanced → Add chat by ID".

    The URLs come from the same builder as `GET Links`; move it to a shared `static class BotLinks` in `Bot/` if it lives only in the controller.
  - The screen is reachable only from failure messages (`ox` buttons). The main menu never links it (§6.3).

- [ ] **Step 1: Write the failing tests**
  - `Closed_WhenEverythingIsFine`.
  - `Opens_OnARecentFailure_ExpiresAfter7Days`.
  - `Opens_OnUnhealthy`.
  - `Opens_OnVisibleDetected`.
  - `Advanced_HasTheFourOptions_WithTheSpecLinks`.
  - `GetDestinations_ReportsManualUnlocked`.
- [ ] **Step 2–4: fail, implement, pass.**
- [ ] **Step 5: Commit**: `feat: setup-failure gate and the Advanced options screen`

---

### Task 13: Manager notifications about destination problems

**Files:**
- Create: `Bot/ManagerNotifier.cs`
- Modify: `Destinations/HealthService.cs` (event), `Bot/Screens/FlowEngine.cs` (`TickAsync`), `Bot/BotStateReset.cs` if needed, `Strings/en.json`, `StringKeys.cs`, `PluginServiceRegistrator.cs`
- Test: `tests/…/Bot/ManagerNotifierTests.cs`

**Interfaces:**
- Produces:
  - `HealthService.ProblemFound` (`event EventHandler<string>?`, with the destination ID). It is raised by `MarkProblem` when the status changes to `Lost`, `NeedsPermissions` or `TopicMissing`.
  - `sealed class ManagerNotifier(BotMemory memory, HealthService health, SettingsStore settings, BotConnection connection, StateStore state, StringTable strings, ILogger<ManagerNotifier> logger)` with `Task NotifyAsync(CancellationToken ct)`:
    - for each destination with a problem status and `Notified == false`, it sends each recent manager one private message: "⚠️ *Title*: *problem text*" + `[Open]` (→ `d [id]`) + `[Check again]` (→ `dc [id]`);
    - it sets `Notified = true` whether or not a manager exists, so a problem is reported once (§9.4);
    - a 403 from a manager's chat (they blocked the bot) removes that manager;
    - other send errors leave `Notified = false`, to retry on the next tick.
  - `UpdateRouter` subscribes nothing. `FlowEngine.TickAsync` calls `NotifyAsync`, and `BotWorker` also enqueues `NotifyAsync` when `ProblemFound` fires.

- [ ] **Step 1: Write the failing tests**
  - `NewProblem_MessagesEachRecentManagerOnce`.
  - `SameProblemAgain_NotRepeated`.
  - `NewKindOfProblem_IsReportedAgain`.
  - `NoManagers_StillMarksNotified`.
  - `BlockedManager_IsRemoved`.
  - `TransportError_RetriesNextTick`.
  - `Recovery_ResetsNotified`: plan 2 already does this; this test pins it.
- [ ] **Step 2–4: fail, implement, pass.**
- [ ] **Step 5: Commit**: `feat: tell recent managers about destination problems`

---

### Task 14: CI end to end: the picker onboarding through real Jellyfin

**Files:**
- Modify: `tests/integration/botapi_stub.py`, `test_botapi_stub.py`, `smoke.py`, `test_smoke.py`

**Interfaces:**
- **Stub:**
  - `push_update(update: dict)` queues an update. `getUpdates` returns the queued updates with `update_id > offset - 1` at once, otherwise sleeps 1 s and returns `[]`. Updates get increasing `update_id`s.
  - `last_reply_markup(method, chat_id)` returns the `reply_markup` of the latest recorded `sendMessage`/`editMessageText` to that chat, parsed from JSON.
- **`smoke.py --stub`**, after the media and restart steps:
  1. `push_update` a private `/start setup` from user `SMOKE_ADMIN = 777000111`;
  2. wait (30 s) for a `sendMessage` to `SMOKE_ADMIN` whose keyboard has `request_chat` with `chat_is_channel` true, and read its `request_id`;
  3. push a `chat_shared` message `{request_id, chat_id: SMOKE_CHAT_3 = -1005555555555, title: "Smoke channel"}`;
  4. wait for the preset wizard and read the `callback_data` of `New media`;
  5. push a `callback_query` with that data on that message;
  6. wait until `GET /TgJelly/Destinations` lists `SMOKE_CHAT_3` with preset `NewMedia`, and the stub recorded the test message to it and a `✅ Done` edit to `SMOKE_ADMIN`.

  The stub's `getChat` for negative IDs says supergroup. Add a channel branch: IDs starting `-1005` are `"type":"channel"`, and `getChatMember` for them returns `administrator` with `can_post_messages`.

- [ ] **Step 1: Write the failing script tests**
  - `test_pushed_updates_are_returned_once_offset_confirms`.
  - `test_last_reply_markup_parses_json`.
  - `test_channel_ids_are_channels`.
- [ ] **Step 2: Run, expect failure.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the script tests, push, and watch CI.** All six jobs pass. The `--stub` logs show the new `ok -` lines.
- [ ] **Step 5: Commit**: `ci: picker onboarding end to end through the stub Bot API`

---

### Task 15 (owner-gated): Settle §15 Q1 and Q3 on the owner's bot

Q2, Q4 and Q9's parse error were settled on the owner's bot on 2026-10-19 (spec §15). Q8 is answered too. What is left needs the owner at a phone, and Task 14's onboarding working:
- the owner's test bot is @kaskadyorbot;
- the token is handed over outside the repo;
- the test forum supergroup is `-1003514547050`.

- [ ] **Step 1: Q1.** The owner gives BotFather's exact menu path for the access restriction. Record it in spec §15; plan 5's README uses it.
- [ ] **Step 2: Q3.** With TgJelly connected to the owner's bot, the owner runs `/start setup` on Android or iOS and on Desktop, for a channel, a group and the forum. Record in spec §15:
  - what the picker shows;
  - whether a confirmation appears before the bot gets admin rights;
  - the "Allow topic creation" promotion prompt.
- [ ] **Step 3:** Q9's "can't initiate conversation", if a user who knows the bot but never pressed Start is at hand. Otherwise leave it noted as unverified.
- [ ] **Step 4: Commit**: `docs: answer spec 15 Q1 and Q3 from a real bot`

---

## Self-check against the spec (done while writing this plan)

| Spec | Task |
|---|---|
| §6.2 triggers (`/start setup`, `/start add`, plain `/start` without destinations, Add destination) | 5, 8 |
| §6.2 step 1 prompt, request IDs, prompt state | 8 |
| §6.2 step 2 Checking message, prompt deletion, retries, pass rules, PhotosBlocked, duplicate, failure | 8 |
| §6.2 step 3 forum: General, create with and without rights, existing topic | 9 |
| §6.2 steps 4–5 preset, Custom, test message, Done | 8 |
| §6.2 stale input, expired picker, 10-min expiry | 5, 8 |
| §6.3 menus, `/status`, `/test`, `/help` | 5, 6, 7 |
| §6.4 private chats only; managers (90 days) | 4, 5, 11 |
| §6.5 gate, Advanced screen, detected chats (rules, 30 s, removal), API manual add (plan 2) | 11, 12 |
| §9.2 work queue, callbacks answered at once, redelivery | 4, 10 |
| §9.4 notifications once per new problem; re-check on `my_chat_member` incl. private unblock | 4, 11, 13 |
| §9.4 row 5 / §6.4 migration service message | 3, 4, 10 |
| §10.3 flows, managers, detectedChats, setupFailures (caps, expiries) | 2 |
| §10.4 both resets clear the bot sections | 2 |
| §11.2 `GET Destinations` detected chats + manual flag, `POST`/`DELETE DetectedChats` | 11, 12 |
| §14.1 FlowEngine list; §14.2 fixtures | 4, 8, 9, 11, 12 |
| §15 Q1, Q3 (Q2, Q4, Q9 parse error settled while planning; Q2 → Task 7) | 7, 15 |

## Roadmap: plan 5 (unchanged, with what this plan hands over)

- **Plan 5 — Dashboard, templates, release:** as planned. It also gets:
  - the dashboard UI for `DetectedChats` and `ManualUnlocked` (§6.5's "Set up manually" link and block);
  - Q1's BotFather path for the README;
  - `BotLinks` for `GET Links`.
