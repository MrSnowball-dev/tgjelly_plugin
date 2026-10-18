# TgJelly Plan 3: Notification Pipeline — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Jellyfin events become Telegram messages. New media is batched into one message per movie, season batch or album, or into one digest per scan. Security events are coalesced, and server events go out as alerts. Everything flows through the plan-2 Outbox. CI proves it end to end inside real Jellyfin 10.11 and 12: a movie copied into a library reaches the stub Bot API, and a restart sends exactly one `server.started`.

**Architecture:** Intake turns Jellyfin callbacks into immutable records. Ten `IEventConsumer<T>`s and four watchers write them into two bounded in-memory `IntakeQueues`. One `PipelineService` loop drains those queues through `NotificationPipeline`:
- `MediaReadiness` decides when an item is ready to announce, using `queue.json`'s `pendingMedia`.
- The pure `Router` picks the destinations.
- `MediaAggregator` and `AlertCoalescer` batch and coalesce, keeping their state in `queue.json`.
- `NotificationRenderer` (layouts, `Formatter`, `MessageFitter`, `PosterPicker`, `LinkBuilder`) turns each result into an `OutboxEntry` that carries its `RenderModel`.

Everything that touches Jellyfin's library sits behind `ILibraryAccess`/`IServerInfo` (implemented by `JellyfinLibrary`/`JellyfinServerInfo`) and `Compat/JellyfinItems`. The pipeline is tested with fakes and a `FakeTimeProvider`. `UpdateChecker` polls GitHub only while some destination subscribes.

**Tech Stack:** C# / .NET 9 + .NET 10 (unchanged), Jellyfin.Controller 10.11.0 / 12.0.0, Telegram.Bot 22.10.3.3 (`HtmlText.Escape/PlainLength/Truncate/ToPlain`), xunit.v3, NSubstitute, `Microsoft.Extensions.TimeProvider.Testing`, Python 3 stdlib for the CI stub.

**Spec:** `docs/superpowers/specs/2026-10-16-tgjelly-v1-design.md` (rev. 2). This plan implements:
- §7 (all), §8.1–§8.4 and §8.6 (default layouts, posters, links, toggles, formatting);
- §9.3's render model and edit re-rendering, the `⏱` line;
- §10.3's pipeline sections: `pendingMedia`, `openGroups`, `releaseBuffers`, `sentMessages.model` and its expiry, `dedupe`, `removedIdentities`, `newSeries`, `recentPluginEvents`, `updateCheck`;
- §11.2 `GET Libraries` and the PublicUrl suggestion;
- §12's intake queues and 5 s graceful drain;
- §14.1's Router, Aggregator, security, MediaReadiness, upgrade, Renderer, LinkBuilder and UpdateChecker tests;
- §14.3's end-to-end media and `server.started` steps.

It also settles §15 Q8 (owner-gated Task 19) and closes plan 2's deferred review minors M1–M8. Plans 1 and 2 (`docs/superpowers/plans/2026-10-17-…`, `2026-10-18-…plan2…`) are merged code. Their Global Constraints still apply.

**Deliberate deviations and deferrals (so reviewers don't flag them):**
- **Rendering happens when an entry is queued, not when it is sent.** Each `OutboxEntry` stores its `RenderModel` next to the rendered `OutboxMessage`. An update to a group or a security window renders the whole message again from the merged model:
  - if the target `Send` is still queued, its message and model are replaced (§7.5 step 3, "re-rendered instead");
  - otherwise an `Edit` is queued, which replaces any edit already queued for that target (§9.3).

  The `⏱` line is the only part added at send time.
- **Custom Scriban templates are plan 5.** `NotificationRenderer` is the seam where plan 5 adds them. Digests never use templates anyway.
- **Security windows are capped at 500** (oldest evicted, counted in `evictions`). §10.3 lists no cap, but every other section has one.
- **Pending lockouts (the 5 s hold, §7.5) live in memory.** A crash loses at most 5 s of them, which is inside §12's crash allowance. A graceful stop sends them (§12 step 2).
- **`media.digest` is an internal event key** for digest entries, so they count as media for Outbox priority and caps. It is not in `EventCatalog`, and destinations never store it.
- **The BaseUrl smoke variant stays in plan 5** (roadmap), as do Status notices with `[Dismiss]`. This plan exposes the data the notices need: `PendingMedia`, `UpdateCheckFailing`, and the removed-library flags.

## Global Constraints

Plans 1 and 2's Global Constraints apply unchanged. That covers identity, two builds, analyzers with zero warnings and one type per file, host safety, no static mutable state, the zip whitelist, Telegram.Bot used only through `ITelegramSender`/`BotSupervisor`, redaction, strings from `en.json`, PascalCase API JSON, the `export PATH="$HOME/.dotnet:$PATH" &&` prefix, and one commit per task made with `git -c user.name=Daniil -c user.email=igatorr19@gmail.com commit` plus the harness's trailer lines. In addition, from the spec:

- **Nothing writes to the library or to items** (D13). `ConvertImageToLocal` is never called. Only `JellyfinLibrary`, `JellyfinServerInfo`, `Compat/JellyfinItems` and the watchers touch Jellyfin library or host APIs (§5.4 rule 1).
- **Event callbacks are O(1), never throw and never do I/O** (§5.4 rule 3). `IEventConsumer<T>` constructors take only `IntakeQueues` and `ILogger<T>` (F6). Watchers may also take `ILibraryManager`, `ITaskManager`, `IServerApplicationHost` and `PendingIdSet`.
- **Intake queues:**
  - media: 10 000 events; when it is full, the oldest queued `Updated` is evicted first;
  - other: 1 000 events; when it is full, the newest is dropped.

  Every drop counts toward `status.counters.droppedIntake` (§12).
- **Batching defaults (§7.4/§10.1):** `QuietWindowSeconds 120`, `MaxHoldMinutes 30`, `ReleaseQuietSeconds 20`, `DigestThreshold 10`, `LateEditHours 6` (0 = never edit), `ReadyTimeoutMinutes 15`. A scan holds a release buffer for at most **120 min**.
- **Security (§7.5):**
  - the `login_failed` window slides and ends **10 min** after the last attempt;
  - a lockout is held **5 s** before it is sent;
  - a `user_created` swallows a `password_changed` that follows within **10 s**;
  - a `login_succeeded` is deduped per (user, device) for **12 h**;
  - restart reasons are the plugin events of the previous **10 min**.
- **Upgrades (§7.4 step 8):** suppressed unless `AnnounceUpgrades`, using a provider-ID lookup, the episode SxxEyy lookup, or `removedIdentities` (48 h). An empty identity set is never an upgrade.
- **Posters (§8.2):** used only when the file is local, exists, is ≤ 10 MB (10 485 760 bytes) and ends in `.jpg/.jpeg/.png/.webp`. Uploaded as-is. The file_id cache key is `"{itemId:N}:{DateModified.Ticks}"`.
- **Length (§8.6):** caption ≤ 1024, text ≤ 4096, measured with `HtmlText.PlainLength`, keeping 40 code points free for the `⏱` line. Text is shortened in this order: the overview, then lists ("+N more"), then `HtmlText.Truncate`.
- **Times** are `<tg-time unix="…" format="t">HH:mm UTC</tg-time>`, or `format="wDT"` with `ddd d MMM HH:mm UTC` when the time isn't today (UTC).
- **`⏱` line:** added when a send leaves the Outbox more than **5 min** after `QueuedAt` (§8.1).
- **Update checker (§7.6):**
  - calls `GET https://api.github.com/repos/jellyfin/jellyfin/releases?per_page=20` with `User-Agent: TgJelly/<plugin version>`, a 30 s timeout and the bot's proxy settings;
  - checks first 5 min after startup or after a subscription appears, then every 12 h;
  - after 3 failures in a row, Status shows "Update check failing".
- **Graceful stop (§12):** within 5 s, the intake queues are drained into MediaReadiness and the Aggregator, and held lockouts go to the Outbox. Persistence flushes afterwards, because it stops later.

## Review Focus

These are the five inputs the spec implies but its happy paths don't exercise, most likely first. Each has a test in the task that owns the code:

1. **An initial import or a big scan** (thousands of items in one library refresh). Expected: one digest per destination, not thousands of messages. Nothing gets dropped by the Outbox's 500-entry media cap or the intake cap for a normal library. Tests:
   - `MediaAggregatorTests.BigScan_3000Movies_GivesOneDigestPerDestination` (Task 13);
   - `IntakeQueuesTests.Full_EvictsUpdatedBeforeAdded` (Task 5).
2. **Jellyfin restarts while items are pending or groups are open or buffered** (an in-process restart during a scan). Expected: after the restart the items are still announced, exactly once, with no duplicate for anything already sent. Test: `NotificationPipelineTests.RestartMidBatch_AnnouncesEachGroupOnce` (Task 15).
3. **Titles and overviews with `& < >`, emoji, non-BMP characters or huge length.** Expected: everything is escaped, captions stay ≤ 1024 and texts ≤ 4096 code points, the `⏱` line still fits, and a surrogate pair is never split. Tests:
   - `MessageFitterTests.NonBmpOverview_IsCutOnCodePoints_AndFits` (Task 9);
   - `MediaLayoutsTests.HostileTitle_IsEscaped_AndFits` (Task 11).
4. **Metadata never arrives** (a provider fails, so `DateLastRefreshed` stays `MinValue`), or the item is deleted before it is ready. Expected: after `ReadyTimeoutMinutes` the item is announced with what it has, and a deleted item is dropped quietly without affecting its group. Tests:
   - `MediaReadinessTests.NoMetadata_AnnouncedAfterTimeout`;
   - `MediaReadinessTests.DeletedBeforeReady_DroppedQuietly` (Task 7).
5. **Odd or partial Jellyfin payloads:** a null `SessionInfo`, a null `Exception` in `InstallationFailedEventArgs`, a null item or an album lookup that throws, a `TaskResult` with null texts. Expected: Jellyfin is never broken, and the event is either published with empty fields or skipped and logged. Tests:
   - `ConsumerTests.PartialPayloads_PublishWithEmptyFields_AndNeverThrow` (Task 5);
   - `LibraryWatcherTests.ThrowingItem_IsLoggedAndSkipped` (Task 6).

---

## File Structure

```
src/Jellyfin.Plugin.TgJelly/
  Library/          MediaKind, ItemFacts, FilterVerdict, MediaFilter, Identities, ImageRef, LibraryRef, MediaItem,
                    ILibraryAccess, JellyfinLibrary, IServerInfo, JellyfinServerInfo
  Compat/           JellyfinItems (BaseItem → ItemFacts; the only #if JF12 for items)
  Intake/           EventFields, NotificationEvent, MediaIntakeKind, MediaIntake, IntakeQueues, PendingIdSet, IScanState,
                    LibraryWatcher, TaskWatcher, RestartWatcher, StartupWatcher, ScanFinishedTask
  Intake/Consumers/ LoginFailedConsumer, LoginSucceededConsumer, LockoutConsumer, UserCreatedConsumer,
                    UserDeletedConsumer, PasswordChangedConsumer, PluginInstalledConsumer, PluginUpdatedConsumer,
                    PluginUninstalledConsumer, PluginInstallFailedConsumer
  Pipeline/         MediaReady, MediaReadiness, Router, LibraryFilter, PresetUpgrader, GroupKeys, MediaOutputKind,
                    MediaOutput, MediaAggregator, AlertOutput, AlertCoalescer, NotificationPipeline, PipelineService
  Rendering/        Formatter, LinkBuilder, PosterRef, PosterPicker, MessageParts, MessageFitter, MediaLayouts,
                    AlertLayouts, NotificationRenderer
  Updates/          GitHubRelease, IReleaseFeed, GitHubReleaseFeed, ReleaseVersion, UpdateChecker
  State/Documents/  RenderKind, RenderModel, MediaGroupState, ReleaseBuffer, SecurityWindow, DedupeSection,
                    RecentPluginEvent, UpdateCheckState (+ QueueDocument/StateDocument/OutboxEntry/SentMessage fields)
  Api/              LibrariesController, LibraryDto (+ SettingsDto, StatusDto, DestinationDto fields)
tests/Jellyfin.Plugin.TgJelly.Tests/
  Fakes/            FakeLibrary, FakeServerInfo, FakeScanState, FakeReleaseFeed
  Library/ Intake/ Pipeline/ Rendering/ Rendering/Golden/*.txt Updates/   tests per unit
tests/integration/botapi_stub.py (multipart), smoke.py (media + restart steps), test_botapi_stub.py
```

---

### Task 1: Plan-2 minors in state and settings (M1, M6, M7)

**Files:**
- Modify: `src/Jellyfin.Plugin.TgJelly/State/StateStore.cs`, `State/SecretsStore.cs`, `Configuration/PluginConfiguration.cs`, `Plugin.cs`, `Bot/BotTokenService.cs`, `Api/BotController.cs`
- Test: `tests/…/State/StateStoreTests.cs`, `State/SecretsStoreTests.cs`, `PluginConfigurationHostTests.cs`, `Bot/BotTokenServiceTests.cs`, `Api/BotControllerTests.cs`

**Interfaces:**
- Consumes: plan 2's `StateStore`, `SecretsStore`, `Plugin.UpdateConfiguration`, `BotTokenService`.
- Produces:
  - `StateStore.BlockedRetryInterval` (`static readonly TimeSpan` = 30 s);
  - `PluginConfiguration.MaxOutsideRevision` (`public const long` = `long.MaxValue / 2`);
  - `BotTokenService.DisconnectAsync(CancellationToken ct) : Task`, which replaces `Disconnect()`.

- [ ] **Step 1: Write the failing tests**

- `StateStoreTests.UnreadableQueueFile_IsNeverOverwritten_AndLoadsOnceReadable`:
  - Setup: `[UnsupportedOSPlatform("windows")]`, skip on Windows or root. Write a valid `queue.json` whose `outbox` holds one entry `"e1"`, then `File.SetUnixFileMode(path, UnixFileMode.None)`.
  - `Load()` warns that `queue.json` "can't be read".
  - After `UpdateQueue(q => q.Outbox.Clear())`, `FlushAll()` and `time.Advance(1 s); FlushDue()`, the file's bytes are unchanged (compare after restoring the mode).
  - Restore the mode, `time.Advance(StateStore.BlockedRetryInterval)`, `FlushDue()`: now `ReadQueue(q => q.Outbox.Single().EntryId) == "e1"`, and `LoadWarnings` gains "queue.json is readable again; changes made while it couldn't be read were not kept."
- `SecretsStoreTests.UnreadableFile_IsReadAgain_OnceReadable`: same setup, with `secrets.json` holding a token. `Load()` returns `BotSecrets.Empty`. After restoring the mode, the next `Load()` returns the token and `LoadWarning` is `null`.
- `PluginConfigurationHostTests.OutsideSave_NearMaxRevision_NeverWraps`:
  - an outside save with `Revision = long.MaxValue - 1` gives `Revision == PluginConfiguration.MaxOutsideRevision + 1`;
  - then 3 `store.Update` calls each raise `SettingsChanged`, and the revision keeps increasing and stays positive.
- `BotTokenServiceTests.Disconnect_WaitsForASaveInFlight_AndWins`:
  - `api.OnAsync("getMe", …)` blocks on a `TaskCompletionSource`; start `SaveAsync`, then start `DisconnectAsync`;
  - assert `DisconnectAsync` hasn't completed; release `getMe`; await both;
  - `secrets.Load().BotToken == ""` and `supervisor.Restarts == 2`.
- `BotControllerTests.DeleteToken_CallsDisconnectAsync`: the existing disconnect test, rewritten to `await`.

- [ ] **Step 2: Run them, expect failure**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet test --project tests/Jellyfin.Plugin.TgJelly.Tests -c Release -f net10.0`
Expected: FAIL. The build lacks `BlockedRetryInterval`, `MaxOutsideRevision` and `DisconnectAsync`.

- [ ] **Step 3: Implement**

- `StateStore`:
  - When `ReadOrDefault` hits `IOException` or `UnauthorizedAccessException`, mark that file **blocked** and use the warning "`<file>` can't be read: <msg>. TgJelly won't overwrite it until it can."
  - `Flush` never writes a blocked file and leaves its dirty flag set.
  - `FlushDue` retries the read of each blocked file at most once per `BlockedRetryInterval`. On success, the read document replaces the in-memory one under `_gate`, the block clears, and the "readable again" warning is added.

  If `state.json` itself was the blocked file, the warning goes into the newly read document.
- `SecretsStore.ReadFromDisk`: on `IOException` or `UnauthorizedAccessException`, don't cache the result, so the next `Load()` reads again. Set `_loadWarning` each time. Clear it on a successful read.
- `Plugin.UpdateConfiguration` (outside branch): `incoming.Revision = Math.Min(Math.Max(current, incoming.Revision), PluginConfiguration.MaxOutsideRevision) + 1;`. This replaces the plan-2 `long.MaxValue` special case.
- `BotTokenService.DisconnectAsync`: `await _saving.WaitAsync(ct)`, then the old body, then `Release` in `finally`. `BotController`'s `DELETE Token` awaits it with the request's `CancellationToken`.

- [ ] **Step 4: Run all tests**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet build Jellyfin.Plugin.TgJelly.slnx -c Release && dotnet test --solution Jellyfin.Plugin.TgJelly.slnx -c Release --no-build`
Expected: PASS, with `0 Warning(s)` and `failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "fix: never overwrite unreadable state files, clamp outside revisions, serialise disconnect with token saves"
```

---

### Task 2: Plan-2 minors in delivery and the API (M2, M3, M4, M5, M8)

**Files:**
- Modify: `Bot/DeliveryErrorKind.cs`, `Bot/TelegramErrorClassifier.cs`, `Delivery/OutboxDelivery.cs`, `Api/DestinationsController.cs`, `Strings/en.json`
- Test: `Bot/TelegramErrorClassifierTests.cs`, `Delivery/OutboxDeliveryTests.cs`, `Api/DestinationsControllerTests.cs`

**Interfaces:**
- Produces:
  - `DeliveryErrorKind.Unauthorized`: a new member placed just before `Other`. Its doc reads "401: the token was revoked; retried with backoff while the poll loop reports TokenRejected".
  - `DeliveryError.Description` and `PollError.Description` are always redacted.
  - The string `destination.notForum`.

- [ ] **Step 1: Write the failing tests**

- `TelegramErrorClassifierTests`:
  - `[Theory]` row `(401, "Unauthorized", DeliveryErrorKind.Unauthorized)`;
  - `Descriptions_AreRedacted`: an `ApiRequestException("Bad Request: 123456789:AAEhBP0av28KrBd9xL4Zq3Wm7Tn5Ys1Cv6QpZ", 400)` gives a `Description` containing `<token>` and not the token, for both `ClassifyDelivery` and `ClassifyPoll`.
- `OutboxDeliveryTests.Unauthorized_KeepsTheEntry_ForARetry`: `sendMessage` answers `Error(401, "Unauthorized")`. The entry is still queued with `NotBefore > now` and `Attempts == 1`, and `droppedOutbox == 0`.
- `OutboxDeliveryTests.UnreadablePoster_SendsTheTextLayout` (Unix, skipped as root): the poster file has mode `None`. Exactly one `sendMessage` goes out with the entry's `Text`, there is no `sendPhoto`, and the entry is completed.
- `DestinationsControllerTests`:
  - `Add_CheckerApiError_ShowsTelegramsRedactedText`: `getChat` answers `Error(400, "Bad Request: something odd")`. The `ChatId` field error is `"Telegram answered with an error: Bad Request: something odd"`.
  - `Add_CheckerTransportError_SaysUnreachable`: `getChat` throws `HttpRequestException`. The error is `strings.Get("token.unreachable")`.
  - `Add_AlreadyExistsRecheckFails_StillAnswersAlreadyExists`: an existing destination, and the second `getChat` (the re-check) fails with a transport error. The outcome is 200 `AlreadyExists`, never a 500.
  - `Add_MessageLinkInANonForumChat_IsRejected`: `TopicLink = "https://t.me/c/1234567890/55"`, and the chat is a supergroup with `is_forum = false`. The response is 400 with field `TopicLink` and `strings.Get("destination.notForum")`, and no destination is created.

- [ ] **Step 2: Run them, expect failure** (same command as Task 1; `Unauthorized` doesn't exist yet).

- [ ] **Step 3: Implement**

- Classifier:
  - `401 => DeliveryErrorKind.Unauthorized`;
  - wrap every `Description` in `SecretRedactor.Redact(...)` (both methods).
- `OutboxDelivery.OnFailure`:
  - `Unauthorized` gets the `ServerError` treatment (replace, then `Retry` with `Backoff`, counted).
  - A new first case: `exception is IOException or UnauthorizedAccessException && attempt.UploadPath is not null` → `message.ForceTextOnly = true; return (entry, destination);`. Transport `IOException`s arrive wrapped in `RequestException`, so this matches only file errors.
- `DestinationsController.Add`:
  - Map checker exceptions: `BotNotConnectedException` → `destination.notConnected`; `ApiRequestException` → `token.failed` with `("error", description)`; anything else → `token.unreachable`.
  - Guard the `AlreadyExists` re-check with a `try/catch (Exception) when not OperationCanceledException`, which logs a warning and returns the stored health.
  - After a passing check: if `threadId > 0 && !check.IsForum`, return `Error("TopicLink", _strings.Get("destination.notForum"))`.
- `en.json`: `"destination.notForum": "This chat has no topics, so that link points to a message. Use the chat ID instead."`

- [ ] **Step 4: Run all tests** (Task 1's command). Expected: PASS, 0 warnings.

- [ ] **Step 5: Commit**: `fix: hold messages on 401, redact Telegram texts, clearer add errors, reject message links, text fallback for unreadable posters`

---

### Task 3: Item facts and the media filter

**Files:**
- Create: `Library/MediaKind.cs`, `Library/ItemFacts.cs`, `Library/FilterVerdict.cs`, `Library/MediaFilter.cs`, `Library/Identities.cs`, `Compat/JellyfinItems.cs`
- Test: `tests/…/Library/MediaFilterTests.cs`, `Library/IdentitiesTests.cs`, `Compat/JellyfinItemsTests.cs`

**Interfaces:**
- Produces:
  - `enum MediaKind { Other, Movie, Episode, Series, MusicAlbum, Audio, MusicVideo, Book, AudioBook }`
  - `sealed record ItemFacts(Guid Id, MediaKind Kind, Guid PendingId, bool IsVirtual, bool HasOwner, bool IsExtra, bool IsAlternateVersion, bool IsItemByName, DateTime DateLastRefreshed, IReadOnlyList<string> Identities)`:
    - `PendingId` is the album ID for `Audio` (`Guid.Empty` when it has none) and `Id` otherwise.
  - `enum FilterVerdict { Drop, Keep, SeriesContext }`
  - `static class MediaFilter`:
    - `FilterVerdict Classify(ItemFacts facts)`;
    - `bool IsAnnounced(MediaKind kind)` (true for the seven kept kinds).
  - `static class Identities`: `IReadOnlyList<string> Of(MediaKind kind, IReadOnlyDictionary<string, string>? providerIds, Guid seriesId, int? seasonNumber, int? episodeNumber)`.
  - `static class JellyfinItems`:
    - `ItemFacts Describe(BaseItem item, Func<Audio, Guid> albumOf)`;
    - `Guid AlbumOf(Audio audio)` (= `audio.AlbumEntity?.Id ?? Guid.Empty`; the production delegate);
    - `internal static bool IsAlternateVersion(Video video)`.

- [ ] **Step 1: Write the failing tests**

`MediaFilterTests`, a table-driven `[Theory]` over `ItemFacts` built by a helper `Facts(MediaKind kind, …flags)`:
- `Movie`, `Episode`, `MusicAlbum`, `MusicVideo`, `Book`, `AudioBook` → `Keep`;
- `Audio` with a non-empty `PendingId` → `Keep`; with `Guid.Empty` → `Drop` ("Audio without an album is not announced");
- `Series` → `SeriesContext`; `Other` → `Drop`;
- for a Movie, each of `IsVirtual`, `HasOwner`, `IsExtra`, `IsAlternateVersion` and `IsItemByName` set → `Drop`.

`IdentitiesTests`:
- a movie with `{"Imdb":"tt0111161","Tmdb":"278"}` → `["imdb:tt0111161","tmdb:278"]`;
- an album with `{"MusicBrainzAlbum":"abc"}` → `["musicbrainz:abc"]`;
- an episode with `{"Tvdb":"9"}`, series `S`, season 2, episode 5 → `["tvdb:9", "series:{S:N}:S02E05"]`;
- an episode without numbers → only provider IDs;
- `null`/empty → `[]`;
- keys are matched case-insensitively, empty values are skipped, and output keeps the order imdb, tmdb, tvdb, musicbrainz, series.

`JellyfinItemsTests`. These use real Jellyfin entities and touch only stored properties, never `LibraryManager`:
- `new AudioBook { Id = g }` → `Kind == AudioBook`. Most-derived wins: an AudioBook isn't `Audio`, and `albumOf` is never called. Pass a delegate that throws.
- `new Audio { Id = g }` with `albumOf = _ => album` → `Kind == Audio` and `PendingId == album`.
- `new Movie { Id = g, ExtraType = ExtraType.Trailer }` → `IsExtra`.
- `new Movie { OwnerId = Guid.NewGuid() }` → `HasOwner`.
- `new Episode { IsVirtualItem = true }` → `IsVirtual`.
- `new MusicArtist()` → `IsItemByName` (an `IItemByName`).
- `new Series()` → `Series`.
- `new Folder()` → `Other`.
- A movie with a primary version set → `IsAlternateVersion`. Use `#if JF12` in the test too: `SetPrimaryVersionId(Guid.NewGuid())` on 12, `SetPrimaryVersionId(Guid.NewGuid().ToString("N"))` on 10.11.

- [ ] **Step 2: Run, expect failure** (types missing).

- [ ] **Step 3: Implement**

- `Describe`:
  - Type tests use `is`, in the order `AudioBook`, `Audio`, `MusicAlbum`, `MusicVideo`, `Movie`, `Episode`, `Book`, `Series`, then `Other`. `MusicVideo` and `Movie` don't derive from each other; the order just fixes the precedence.
  - Identities come from `Identities.Of(kind, item.ProviderIds, (item as Episode)?.SeriesId ?? Guid.Empty, item.ParentIndexNumber, item.IndexNumber)`.
- `IsAlternateVersion`:
  - JF12: `video.PrimaryVersionId is { } id && id != Guid.Empty`;
  - 10.11: `!string.IsNullOrEmpty(video.PrimaryVersionId)`.

  This is the only `#if JF12` for items (§5.4 rule 6).

- [ ] **Step 4: Run all tests.** PASS on both TFMs.
- [ ] **Step 5: Commit**: `feat: item facts, the media filter and identity sets`

---

### Task 4: Library access and server info

**Files:**
- Create: `Library/ImageRef.cs`, `Library/LibraryRef.cs`, `Library/MediaItem.cs`, `Library/ILibraryAccess.cs`, `Library/JellyfinLibrary.cs`, `Library/IServerInfo.cs`, `Library/JellyfinServerInfo.cs`; `tests/…/Fakes/FakeLibrary.cs`, `Fakes/FakeServerInfo.cs`
- Modify: `PluginServiceRegistrator.cs`, `tests/…/PluginServiceRegistratorTests.cs`
- Test: `tests/…/Library/JellyfinLibraryTests.cs`, `Library/JellyfinServerInfoTests.cs`

**Interfaces:**
- Produces:
  - `sealed record ImageRef(string Path, bool IsLocal, DateTime DateModified)`
  - `sealed record LibraryRef(Guid Id, string Name, string? CollectionType)`
  - `sealed class MediaItem` with `init` properties:
    - identity: `Guid Id`, `MediaKind Kind`, `string Name`, `int? Year`;
    - details: `string? Overview`, `float? CommunityRating`, `TimeSpan? RunTime`, `IReadOnlyList<string> Genres`, `IReadOnlyDictionary<string,string> ProviderIds`;
    - episodes: `Guid SeriesId`, `string? SeriesName`, `Guid SeasonId`, `int? SeasonNumber`, `int? EpisodeNumber`;
    - music: `string? AlbumArtist`;
    - parent and children: `Guid ParentId`, `string? ParentName`, `int ChildCount` (album tracks; 0 otherwise);
    - `ImageRef? Primary`, `IReadOnlyList<string> Identities`.
  - `interface ILibraryAccess`:
    - `MediaItem? GetItem(Guid id)`;
    - `IReadOnlyList<LibraryRef> GetLibraries(Guid itemId)`;
    - `bool HasOtherCopy(MediaItem item)`;
    - `IReadOnlyList<LibraryRef> GetVirtualFolders()`.
  - `interface IServerInfo`: `string SystemId`, `string ServerName`, `string VersionString`, `Version Version`, `string BaseUrl`, `string? SuggestPublicUrl()`.
  - `FakeLibrary : ILibraryAccess`:
    - `Dictionary<Guid, MediaItem> Items`, `Dictionary<Guid, List<LibraryRef>> Libraries`, `HashSet<Guid> OtherCopies`, `List<LibraryRef> VirtualFolders`;
    - `MediaItem Add(MediaItem item, params LibraryRef[] libraries)`.
  - `FakeServerInfo : IServerInfo` with settable properties: defaults `SystemId = "a1b2c3"`, `ServerName = "Home"`, `VersionString = "12.2.0"`, `BaseUrl = ""`, `Suggestion = null`.

- [ ] **Step 1: Write the failing tests**

Use NSubstitute for `ILibraryManager` with real entities.

`JellyfinLibraryTests`:
- `GetItem_Movie_MapsStoredFields`. The source is `new Movie { Id, Name = "Arrival", ProductionYear = 2016, Overview, CommunityRating = 7.9f, RunTimeTicks = TimeSpan.FromMinutes(116).Ticks, Genres = ["Drama","Sci-Fi"], ProviderIds = {Imdb, Tmdb} }` plus `ImageInfos = [new ItemImageInfo { Type = ImageType.Primary, Path = "/p.jpg", DateModified = d }]`. Assert:
  - every field maps;
  - `Primary == new ImageRef("/p.jpg", true, d)`;
  - `Identities` are those from `Identities.Of`.
- `GetItem_Episode_MapsSeriesAndSeason`: `SeriesId`, `SeriesName`, `SeasonId`, `ParentIndexNumber → SeasonNumber`, `IndexNumber → EpisodeNumber`.
- `GetItem_Album_CountsTracks`: a substituted `GetItemById` returns a `MusicAlbum`, and `ChildCount` comes from `ILibraryManager.GetCount(InternalItemsQuery { AncestorIds = [album.Id], IncludeItemTypes = [BaseItemKind.Audio], IsVirtualItem = false, Recursive = true })`. That covers multi-disc folders too.
- `GetItem_Missing_ReturnsNull`; `GetItem_Throws_ReturnsNull_AndLogs`.
- `HasOtherCopy`:
  - `_QueriesProviderIds_SameType_ExcludingSelf`: capture the `InternalItemsQuery` passed to `GetCount` and assert `IncludeItemTypes == [BaseItemKind.Movie]`, `HasAnyProviderId` = `{Imdb: tt…, Tmdb: …}`, `IsVirtualItem == false` and `ExcludeItemIds == [self]`.
  - `_Episode_AlsoQueriesSeasonAndNumber`: `AncestorIds == [seriesId]`, `ParentIndexNumber == 2`, `IndexNumber == 5`.
  - `_EmptyIdentities_NeverQueries`.
- `GetVirtualFolders_MapsItemIdNameAndType`.

`JellyfinServerInfoTests.SuggestPublicUrl`:
- returns the `external=` entry of `PublishedServerUriBySubnet`, then an `all=` entry;
- otherwise `GetSmartApiUrl(IPAddress.Parse("8.8.8.8"))`;
- `BaseUrl` comes from `GetNetworkConfiguration().BaseUrl`. Substitute `IServerConfigurationManager.GetConfiguration("network")` to return a `NetworkConfiguration`.

- [ ] **Step 2: Run, expect failure.**

- [ ] **Step 3: Implement**

`JellyfinLibrary(ILibraryManager library, ILogger<JellyfinLibrary> logger)`:
- `GetItem`:
  - `GetItemById(id)` → map the stored properties only;
  - `ParentName` from `GetItemById(item.ParentId)?.Name`;
  - wrap the whole call in `try/catch`, logging and returning `null`.
- `GetLibraries`: `GetCollectionFolders(item)` → `(f.Id, f.Name, null)`.
- `HasOtherCopy`:
  - provider keys `Imdb`, `Tmdb`, `Tvdb`, `MusicBrainzAlbum` (`MetadataProvider` names) present on the item → one `GetCount` with `Recursive = true`;
  - for episodes with series, season and number → a second query;
  - true if either count is `> 0`;
  - the `MediaKind` → `BaseItemKind` mapping is Movie, Episode, MusicAlbum, MusicVideo, Book, AudioBook.
- `GetVirtualFolders`: `VirtualFolderInfo.ItemId` parsed as a Guid, `Name`, and `CollectionType?.ToString()`.

`JellyfinServerInfo(IServerApplicationHost host, IServerConfigurationManager config)`:
- `SystemId`, `FriendlyName` → `ServerName`, `ApplicationVersionString`, `ApplicationVersion`;
- `BaseUrl` from `config.GetNetworkConfiguration().BaseUrl ?? ""`.

Registration: `services.AddSingleton<ILibraryAccess, JellyfinLibrary>()`, `services.AddSingleton<IServerInfo, JellyfinServerInfo>()`, and a redacted logger for `JellyfinLibrary`. `PluginServiceRegistratorTests` registers substitutes for `ILibraryManager`, `IServerConfigurationManager` and `ITaskManager`; later tasks need the last one.

- [ ] **Step 4: Run all tests.** PASS.
- [ ] **Step 5: Commit**: `feat: read-only library access and server info`

---

### Task 5: Intake queues and event consumers

**Files:**
- Create: `Intake/EventFields.cs`, `Intake/NotificationEvent.cs`, `Intake/MediaIntakeKind.cs`, `Intake/MediaIntake.cs`, `Intake/IntakeQueues.cs`, `Intake/Consumers/*.cs` (10 files)
- Modify: `PluginServiceRegistrator.cs`
- Test: `tests/…/Intake/IntakeQueuesTests.cs`, `Intake/ConsumerTests.cs`

**Interfaces:**
- Produces:
  - `static class EventFields`: string constants `User = "user"`, `UserKey = "user_key"`, `UserId = "user_id"`, `Ip = "ip"`, `Client = "client"`, `Device = "device"`, `DeviceId = "device_id"`, `Attempts = "attempts"`, `Plugin = "plugin"`, `Version = "version"`, `Changelog = "changelog"`, `Error = "error"`, `Task = "task"`, `Server = "server"`, `Current = "current"`, `Url = "url"`, `IpCount = "ip_count"`, `Locked = "locked"`.
  - `sealed record NotificationEvent(string EventKey, DateTimeOffset At, IReadOnlyDictionary<string, string> Values)`.
  - `enum MediaIntakeKind { Added, Updated, Removed, SeriesAdded, ScanFinished }`.
  - `sealed record MediaIntake(MediaIntakeKind Kind, ItemFacts? Item, DateTimeOffset At)`.
  - `sealed class IntakeQueues(CompatResult compat, TimeProvider time)`:
    - `const int MediaCapacity = 10_000`, `OtherCapacity = 1_000`;
    - `bool PublishMedia(MediaIntakeKind kind, ItemFacts? item)`;
    - `bool Publish(string eventKey, IReadOnlyDictionary<string, string> values)`;
    - `IReadOnlyList<MediaIntake> TakeMedia(int max)`, `IReadOnlyList<NotificationEvent> TakeOther(int max)`;
    - `int MediaCount`, `int OtherCount`;
    - `long TakeDropped()` (returns and resets the drop counter);
    - `Task WaitAsync(TimeSpan timeout, CancellationToken ct)` (wakes on any publish; uses `AsyncSignal`).
  - Consumers, each `public sealed class X : IEventConsumer<T>` with constructor `(IntakeQueues queues, ILogger<X> logger)`:

| Consumer | `T` | Event key | Values |
|---|---|---|---|
| `LoginFailedConsumer` | `AuthenticationRequestEventArgs` | `security.login_failed` | user = `Username`, user_key = `Username.ToUpperInvariant()`, ip = `RemoteEndPoint`, client = `App`, device = `DeviceName`, device_id = `DeviceId` |
| `LoginSucceededConsumer` | `AuthenticationResultEventArgs` | `security.login_succeeded` | user = `User?.Name`, user_id = `User?.Id:N`, ip/client/device/device_id from `SessionInfo` |
| `LockoutConsumer` | `UserLockedOutEventArgs` | `security.lockout` | user = `Argument.Username`, user_key, user_id, attempts = `InvalidLoginAttemptCount` |
| `UserCreatedConsumer` / `UserDeletedConsumer` / `PasswordChangedConsumer` | `UserCreated/UserDeleted/UserPasswordChangedEventArgs` | `security.user_created` / `user_deleted` / `password_changed` | user, user_id |
| `PluginInstalledConsumer` / `PluginUpdatedConsumer` | `PluginInstalledEventArgs` / `PluginUpdatedEventArgs` | `server.plugin_installed` / `plugin_updated` | plugin = `Argument.Name`, version = `Argument.Version?.ToString()`, changelog (updated only) |
| `PluginUninstalledConsumer` | `PluginUninstalledEventArgs` | `server.plugin_uninstalled` | plugin, version |
| `PluginInstallFailedConsumer` | `InstallationFailedEventArgs` | `server.plugin_install_failed` | plugin = `InstallationInfo?.Name`, version, error = `Exception?.Message` |

A null value becomes `""`.

- [ ] **Step 1: Write the failing tests**

`IntakeQueuesTests`:
- `Full_EvictsUpdatedBeforeAdded`: fill with 9 999 `Added` + 1 `Updated`, then publish one `Added`. `MediaCount == 10 000`, the `Updated` is gone, and `TakeDropped() == 1`.
- `Full_WithoutUpdated_DropsTheIncomingUpdated`: fill with `Added`; publishing an `Updated` returns `false` and the queue is unchanged.
- `Full_OnlyAdded_DropsTheOldestAdded_NeverAScanFinished`: a queue full of `Added` with a `ScanFinished` first. Publishing one more `Added` keeps the `ScanFinished`, removes the oldest `Added`, and adds the new one.
- `Other_Full_DropsTheNewest`.
- `Incompatible_AcceptsNothing`: `CompatResult` with `IsCompatible == false`; both publishes return `false`, and nothing is counted.
- `Publish_StampsTheClock`: `At == time.GetUtcNow()`.
- `WaitAsync_WakesOnPublish`.

`ConsumerTests` (one `[Fact]` per consumer, plus):
- `LoginFailed_MapsEveryField`: `new AuthenticationRequestEventArgs(new AuthenticationRequest { Username = "Alice", App = "Infuse", DeviceName = "iPhone", DeviceId = "d1", RemoteEndPoint = "203.0.113.5" })` gives `user_key == "ALICE"` and all values.
- `PartialPayloads_PublishWithEmptyFields_AndNeverThrow`:
  - an `AuthenticationResult` with null `SessionInfo` and null `User`;
  - an `InstallationFailedEventArgs` with null `InstallationInfo` and `Exception`;
  - a `PluginUpdatedEventArgs` whose `Version` is null.

  Each publishes with `""` values, and `OnEvent` completes without throwing.
- `QueuesThrow_IsLogged_NotRethrown`: use an `IntakeQueues` built with a `TimeProvider` subclass declared in the test whose `GetUtcNow` throws. `OnEvent` returns a completed task, and the logger saw an error.

- [ ] **Step 2: Run, expect failure.**

- [ ] **Step 3: Implement**

`IntakeQueues` keeps two `LinkedList<…>`s under one `Lock` and an `Interlocked` drop counter, with the eviction rules in the tests. Each consumer's `OnEvent` builds a `Dictionary<string,string>(StringComparer.Ordinal)` inside `try/catch (Exception ex)` → `logger.LogError(ex, …)`, and always returns `Task.CompletedTask`.

Registration:
- `services.AddSingleton<IntakeQueues>()`;
- every consumer `services.AddScoped<IEventConsumer<T>, X>()`, like Jellyfin and the Webhook plugin;
- redacted loggers for all ten;
- the registrator test resolves `IEnumerable<IEventConsumer<AuthenticationRequestEventArgs>>` inside a scope.

- [ ] **Step 4: Run all tests.** PASS.
- [ ] **Step 5: Commit**: `feat: intake queues and the security, user and plugin event consumers`

---

### Task 6: Watchers, the post-scan task and the pending-ID set

**Files:**
- Create: `Intake/PendingIdSet.cs`, `Intake/IScanState.cs`, `Intake/LibraryWatcher.cs`, `Intake/TaskWatcher.cs`, `Intake/RestartWatcher.cs`, `Intake/StartupWatcher.cs`, `Intake/ScanFinishedTask.cs`
- Modify: `PluginServiceRegistrator.cs` (singletons only; Task 15 fixes the hosted-service order)
- Test: `tests/…/Intake/LibraryWatcherTests.cs`, `Intake/TaskWatcherTests.cs`, `Intake/RestartWatcherTests.cs`, `Intake/StartupWatcherTests.cs`, `Intake/PendingIdSetTests.cs`

**Interfaces:**
- Consumes: `IntakeQueues`, `JellyfinItems.Describe`/`AlbumOf`, `MediaFilter`, `IServerStartup` (plan 2), `IServerInfo`, `CompatResult`.
- Produces:
  - `sealed class PendingIdSet`: `void Add(Guid id)`, `bool Contains(Guid id)`, `void Remove(Guid id)`, `void Reset(IEnumerable<Guid> ids)`, `int Count` (thread-safe; `ConcurrentDictionary<Guid, byte>`).
  - `interface IScanState { bool IsScanRunning { get; } }`.
  - `sealed class LibraryWatcher(ILibraryManager, IntakeQueues, PendingIdSet, CompatResult, ILogger<LibraryWatcher>) : BackgroundService, IScanState`, with `internal void OnItemAdded/OnItemUpdated/OnItemRemoved(object? sender, ItemChangeEventArgs e)` and `internal Func<Audio, Guid> AlbumOf` (defaults to `JellyfinItems.AlbumOf`).
  - `sealed class TaskWatcher(ITaskManager, IntakeQueues, CompatResult, ILogger<TaskWatcher>) : BackgroundService`.
  - `sealed class RestartWatcher(IServerApplicationHost, IntakeQueues, CompatResult, ILogger<RestartWatcher>) : BackgroundService`.
  - `sealed class StartupWatcher(IServerStartup, IServerInfo, IntakeQueues, CompatResult, TimeProvider, ILogger<StartupWatcher>) : BackgroundService`.
  - `public sealed class ScanFinishedTask(IntakeQueues queues) : ILibraryPostScanTask`.

- [ ] **Step 1: Write the failing tests**

`LibraryWatcherTests` call the `internal` handlers directly with real entities:
- `Added_KeptMovie_IsQueued_AndMarkedPending`: one `MediaIntake(Added, facts)`, and `pending.Contains(movie.Id)`.
- `Added_DroppedKind_QueuesNothing`: a trailer extra, a `MusicArtist`, and an `Audio` whose `AlbumOf` returns `Guid.Empty`.
- `Added_Series_QueuesSeriesAdded`.
- `Added_TwoTracksOfOneAlbum_PendAlbumOnce`: two `Audio` with the same album. Both queue `Added` with `PendingId == album`; `MediaReadiness` dedupes later.
- `Updated_ForwardedOnlyWhenPendingAndRefreshed`:
  - pending and `DateLastRefreshed = now` → queued `Updated`;
  - pending with `MinValue` → nothing;
  - not pending → nothing;
  - an `Audio` whose album is pending → `Updated` with `PendingId == album`.
- `Removed_AnnouncedKind_QueuesRemovedWithIdentities`; `Removed_Virtual_QueuesNothing`.
- `ThrowingItem_IsLoggedAndSkipped`: an `ItemChangeEventArgs` with `Item = null`, and an `Audio` whose `AlbumOf` delegate throws. Each handler returns normally, the logger saw an error, and nothing was queued.
- `Incompatible_NeverSubscribes`: start the service with an incompatible `CompatResult`; `libraryManager.DidNotReceive().ItemAdded += Arg.Any<…>()`.
- `Stop_Unsubscribes`.
- `IsScanRunning_ReadsTheLibraryManager`.

`TaskWatcherTests`:
- a `Failed` result for "Scan Media Library" → `server.task_failed` with `task = Name` and `error = ErrorMessage` (null → `""`);
- `Completed` → nothing;
- `Key == "RefreshLibrary"` with any status → `ScanFinished` on the media queue.

`RestartWatcherTests`: `HasPendingRestartChanged` with `HasPendingRestart == true` → one `server.restart_pending` with empty values; with `false` → nothing.

`StartupWatcherTests`:
- `EmitsServerStartedOnce_AfterStartupCompletes`: `FakeStartup.HasCompleted = false`, advance 3 s, nothing; set it true and advance 1 s → exactly one `server.started` with `version = info.VersionString` and `server = info.ServerName`; advance 10 s more, still one.
- `Incompatible_NeverEmits`.

`PendingIdSetTests`: `Reset` replaces the contents, and a concurrent `Add`/`Contains` from two tasks doesn't throw.

- [ ] **Step 2: Run, expect failure.**

- [ ] **Step 3: Implement**

- Every handler body sits inside `try/catch (Exception ex) { _logger.LogError(ex, …); }` and does no I/O (§5.4 rule 3).
- `ExecuteAsync`: subscribe at once when `compat.IsCompatible`, then `await Task.Delay(Timeout.Infinite, stoppingToken)` inside the plan-1 `Supervisor.RunAsync`.
- `StopAsync` unsubscribes first, then calls `base.StopAsync`. It never throws.
- `StartupWatcher` polls `HasCompleted` every 1 s with `Task.Delay(…, time, ct)` and publishes once per instance (F4: a restart means a new instance).
- `ScanFinishedTask.Run` publishes `ScanFinished`, sets progress to 100 and returns a completed task.

Registration: `PendingIdSet`, `LibraryWatcher` (also as `IScanState` via a factory), `TaskWatcher`, `RestartWatcher`, `StartupWatcher` as singletons, plus their redacted loggers. Jellyfin finds `ScanFinishedTask` by scanning (F14) and creates it with `ActivatorUtilities`, so it isn't registered.

- [ ] **Step 4: Run all tests.** PASS.
- [ ] **Step 5: Commit**: `feat: library, task, restart and startup watchers, and the post-scan task`

---

### Task 7: MediaReadiness

**Files:**
- Create: `Pipeline/MediaReady.cs`, `Pipeline/MediaReadiness.cs`
- Modify: `State/Documents/QueueDocument.cs`
- Test: `tests/…/Pipeline/MediaReadinessTests.cs`

**Interfaces:**
- Consumes: `ILibraryAccess`, `PendingIdSet`, `MediaIntake`, `SettingsStore` (`Batching.ReadyTimeoutMinutes`, `Message.AnnounceUpgrades`), `StateStore`.
- Produces:
  - `QueueDocument` fields, each a `Dictionary<string, DateTimeOffset>` keyed by Guid `N` or identity strings: `PendingMedia` (`firstSeen`), `RemovedIdentities` (`removedAt`), `NewSeries` (`firstSeen`).
  - `sealed record MediaReady(MediaItem Item, IReadOnlyList<LibraryRef> Libraries, bool Upgraded, bool NewSeries, DateTimeOffset At)`.
  - `sealed class MediaReadiness(StateStore state, SettingsStore settings, ILibraryAccess library, PendingIdSet pending, TimeProvider time, ILogger<MediaReadiness> logger)`:
    - `const int PendingCap = 20_000, RemovedCap = 5_000, NewSeriesCap = 1_000`;
    - `static readonly TimeSpan RemovedTtl = 48 h, NewSeriesTtl = 24 h`;
    - `void Load()` (seeds `PendingIdSet` from `PendingMedia`);
    - `IReadOnlyList<MediaReady> Process(MediaIntake intake)`;
    - `IReadOnlyList<MediaReady> Tick()`;
    - `int PendingCount`.

- [ ] **Step 1: Write the failing tests** (`FakeLibrary`, `FakeTimeProvider`, a real `StateStore` in a `TempDir`)

- `Added_ThenUpdated_IsReadyOnce`: `Process(Added)` returns `[]`, then `Process(Updated)` returns one `MediaReady` with the library from `FakeLibrary`. A second `Updated` returns `[]`, and `PendingCount == 0`.
- `HundredTracksOfOneAlbum_OnePendingAlbum`: 100 `Added` with the same `PendingId` → `PendingCount == 1`.
- `NoMetadata_AnnouncedAfterTimeout`: `Added`, then advance 14 min 59 s → `Tick()` returns `[]`; advance 1 s more → one item.
- `DeletedBeforeReady_DroppedQuietly`: `FakeLibrary` lacks the item. `Updated` returns `[]`, the item is no longer pending, and nothing is logged above `Information`.
- `Upgrade`:
  - `_LibraryHit_IsSuppressed`: the item's ID is in `OtherCopies` → `[]`.
  - `_RemovedWithin48h_IsSuppressed`: `Process(Removed)` with facts whose `Identities = ["imdb:tt1"]`, then an item with that identity becomes ready 47 h later → `[]`. At 49 h it is announced.
  - `_EmptyIdentitySet_IsNeverAnUpgrade`: the item is in `OtherCopies` but has no identities → announced.
  - `_AnnounceUpgradesOn_MarksUpgraded`.
- `NewSeries_FlagsEpisodesOfThatSeriesFor24h`.
- `Restart_KeepsPendingItems`: after `Added`, call `FlushAll()` and build a new `StateStore` and `MediaReadiness` over the same folder. `Load()` puts the ID back in the new `PendingIdSet`, and `Updated` gives the item.
- `PendingCap_TreatsTheOldestAsReady`: with the cap lowered through an `internal int PendingCapOverride`, the next `Added` returns the oldest item, and `status.counters.evictions` increments.
- `TtlAndCaps_PruneRemovedAndNewSeries`: entries older than their TTL are gone after `Tick()`; over-cap entries evict oldest first, counted.

- [ ] **Step 2: Run, expect failure.**

- [ ] **Step 3: Implement**

`Ready(Guid id)`:
1. Remove the ID from the set and the doc.
2. `GetItem` (null → `[]`), then `GetLibraries`.
3. Upgrade test (only when `Identities.Count > 0`): `library.HasOtherCopy(item)` or any identity in `RemovedIdentities` with `removedAt + 48 h > now`.
4. Suppressed → `[]` unless `AnnounceUpgrades`, in which case `Upgraded = true`.
5. `NewSeries = item.Kind == Episode && NewSeries[item.SeriesId:N] + 24 h > now`.

`Process(SeriesAdded)` records the series. `Process(ScanFinished)` returns `[]`; the pipeline routes it to the Aggregator. All writes go through `StateStore.UpdateQueue` (not urgent).

- [ ] **Step 4: Run all tests.** PASS.
- [ ] **Step 5: Commit**: `feat: MediaReadiness with timeouts, upgrade detection and new-series context`

---

### Task 8: Router, library filter and preset upgrades

**Files:**
- Create: `Pipeline/Router.cs`, `Pipeline/LibraryFilter.cs`, `Pipeline/PresetUpgrader.cs`
- Modify: `Hosting/PersistenceService.cs`
- Test: `tests/…/Pipeline/RouterTests.cs`, `Pipeline/PresetUpgraderTests.cs`, extend `Hosting/PersistenceServiceTests.cs`

**Interfaces:**
- Produces:
  - `static class Router`: `IReadOnlyList<Destination> Route(string eventKey, IReadOnlyCollection<Guid>? libraryIds, IReadOnlyList<Destination> destinations)`.
  - `static class LibraryFilter`:
    - `bool Matches(Destination d, IReadOnlyCollection<Guid> libraryIds)`;
    - `IReadOnlyList<string> Removed(Destination d, IReadOnlyCollection<Guid> existing)`;
    - `bool RefersOnlyToRemoved(Destination d, IReadOnlyCollection<Guid> existing)`.
  - `static class PresetUpgrader`: `bool Apply(PluginConfiguration configuration)`, which returns whether anything changed.

- [ ] **Step 1: Write the failing tests**

`RouterTests`:
- `ExactKeyMatch_Only`: `Events = ["media.movie"]` doesn't match `media.movies` or `MEDIA.MOVIE`.
- `Disabled_GetsNothing`.
- `LibraryFilter_AppliesToMediaOnly`:
  - `LibraryIds = [L1:N]`; `media.movie` from `{L2}` → none, from `{L1, L2}` → match;
  - `security.lockout` (libraries `null`) → match;
  - empty `LibraryIds` → all.
- `RemovedLibraries_MatchNothing_ButAreKept`.
- `UnparseableLibraryId_MatchesNothing`.

`LibraryFilter`: `Removed` lists IDs missing from `existing`; `RefersOnlyToRemoved` is true only when the list is non-empty and every ID is missing.

`PresetUpgraderTests`:
- `NewMedia_MissingAKey_GetsItAdded` (simulate a key added in a later version by starting from `Events` minus one key) → `Apply` returns `true` and the events are in catalog order;
- `Custom_IsNeverTouched`;
- `UpToDate_ReturnsFalse`.

`PersistenceServiceTests.Start_UpgradesPresets_Once`: a stored `NewMedia` destination missing `media.book`. After startup the destination has it, and `Revision` went up by exactly 1.

- [ ] **Step 2: Run, expect failure.**

- [ ] **Step 3: Implement**

`Route` filters `d.Enabled && d.Events.Contains(eventKey, StringComparer.Ordinal)`, plus `LibraryFilter.Matches` when `EventCatalog.GroupOf(eventKey) == "media"` and `libraryIds` is non-null. `Matches` treats empty `LibraryIds` as all; otherwise it parses each with `Guid.TryParseExact(id, "N", …)` and checks for an intersection.

`PersistenceService.RunAsync`, right after the restore check: `_settings.UpdateIf(null, PresetUpgrader.Apply)`, once per instance and guarded like `_restoreChecked`.

- [ ] **Step 4: Run all tests.** PASS.
- [ ] **Step 5: Commit**: `feat: Router with library filter, and preset upgrades on load`

---

### Task 9: Rendering basics: Formatter, LinkBuilder, PosterPicker, MessageFitter

**Files:**
- Create: `Rendering/Formatter.cs`, `Rendering/LinkBuilder.cs`, `Rendering/PosterRef.cs`, `Rendering/PosterPicker.cs`, `Rendering/MessageParts.cs`, `Rendering/MessageFitter.cs`
- Modify: `Strings/en.json`, `PluginServiceRegistrator.cs`
- Test: `tests/…/Rendering/FormatterTests.cs`, `LinkBuilderTests.cs`, `PosterPickerTests.cs`, `MessageFitterTests.cs`

**Interfaces:**
- Consumes: `StringTable`, `IServerInfo`, `PublicUrlRules`, `ImageRef`, `HtmlText`.
- Produces:
  - `sealed class Formatter(StringTable strings)`:
    - `StringTable Strings`;
    - `string Escape(string? text)`;
    - `string Time(DateTimeOffset at, DateTimeOffset now)`;
    - `string Number(long n)`;
    - `string Rating(float rating)`;
    - `string Runtime(TimeSpan runtime)`;
    - `string Version(Version v)`;
    - `string DelayedLine(DateTimeOffset queuedAt, DateTimeOffset now)`.
  - `sealed class LinkBuilder(IServerInfo server)`:
    - `string? OpenInJellyfin(string publicUrl, Guid itemId)`;
    - `string? Suggest()`;
    - `static string? Normalize(string? url)`;
    - `static string Imdb(string id)`;
    - `static string Tmdb(bool tv, string id)`.
  - `sealed record PosterRef(string Path, string CacheKey)`.
  - `static class PosterPicker`: `const long MaxBytes = 10_485_760`; `PosterRef? Pick(IEnumerable<(Guid ItemId, ImageRef? Image)> candidates)` (the first eligible one wins).
  - `sealed record MessageParts(string Head, IReadOnlyList<string> List, string? Overview, string Tail)`. `List` items are HTML. `Overview` is plain text, which the fitter escapes.
  - `static class MessageFitter`:
    - `const int CaptionLimit = 1024, TextLimit = 4096, DelayedReserve = 40`;
    - `string Fit(MessageParts parts, int limit, Func<int, string> more)`, where `more(n)` renders "+N more".
- `en.json` additions:

```json
"time.utc": "{time} UTC",
"delivery.delayed": "⏱ {time}",
"media.runtime.hours": "{hours}h {minutes}m",
"media.runtime.minutes": "{minutes}m",
"media.more": "+{count} more"
```

- [ ] **Step 1: Write the failing tests**

`FormatterTests`:
- `Time_Today`: `at = 2026-10-18 14:05:09Z`, `now = 2026-10-18 20:00Z` → `<tg-time unix="1792332309" format="t">14:05 UTC</tg-time>`. Compute the unix value in the test with `at.ToUnixTimeSeconds()`.
- `Time_AnotherDay` → `format="wDT"` with text `Sat 17 Oct 14:05 UTC` (`ddd d MMM HH:mm`, en-US).
- `Escape`: `&<>` → `&amp;&lt;&gt;`; null → `""`.
- `Runtime`: 116 min → `1h 56m`, 45 min → `45m`, 60 min → `1h 0m`.
- `Rating`: `7.85f` → `7.9`, `10f` → `10.0`.
- `Number`: `12345` → `12,345`.
- `Version`: `12.2.0.0` → `12.2`, `10.11.11.0` → `10.11.11`, `12.0.0.0` → `12.0`.
- `DelayedLine` → `⏱ <tg-time …>`.

`LinkBuilderTests`:
- `NoDotHost_IsRejected`: `http://jellyfin:8096` → `null`.
- `Ipv6_IsAccepted`: `http://[2001:db8::1]:8096` → a link.
- `BaseUrl_AppendedExactlyOnce`, with BaseUrl `/jf`:
  - `https://x.example.com` → `https://x.example.com/jf/web/#/details?id={id:N}&serverId=a1b2c3`;
  - `https://x.example.com/jf/` and `https://x.example.com/JF` → also `…/jf/web/…`, where the path keeps the URL's own case and nothing is appended twice.
- `EmptyBaseUrl`: `https://x.example.com/` → `https://x.example.com/web/#/details?…`.
- `Suggest_NormalizesAndValidates`: `"http://192.168.1.5:8096/"` → `"http://192.168.1.5:8096"`; `"http://localhost:8096"` → `null`.
- `Imdb`/`Tmdb` URLs follow §8.3.

`PosterPickerTests` use temp files:
- picks the first local, existing, ≤ 10 MB `.jpg/.jpeg/.png/.webp`;
- skips each of: non-local, missing file, 10 485 761 bytes, `.gif`, null image;
- `CacheKey == $"{id:N}:{dateModified.Ticks}"`.

`MessageFitterTests`:
- `Fits_Unchanged`: segments are joined by `"\n\n"` in the order Head, List (items joined by `"\n"`), escaped Overview, Tail, with empty segments skipped.
- `LongOverview_IsShortenedFirst_WithEllipsis`: the result's `HtmlText.PlainLength` ≤ `1024 - 40`, the head and tail are intact, and the overview ends with `…`.
- `ThenTheList_WithMore`: with no overview, 200 episode lines → the last line is `+N more` and N is right.
- `ThenTruncate`: a 2000-character head → `HtmlText.Truncate` result ≤ budget.
- `NonBmpOverview_IsCutOnCodePoints_AndFits`: an overview made of `"𝕏"` × 2000 never ends with a lone surrogate (`char.IsHighSurrogate(result[^2])` check), and it fits.
- `ExactlyAtTheLimit_IsUntouched`: a head of 984 code points (1024 − 40) made of non-BMP characters is unchanged.

- [ ] **Step 2: Run, expect failure.**

- [ ] **Step 3: Implement**

- `Formatter`:
  - formats with `CultureInfo.GetCultureInfo("en-US")`;
  - "today" means `at.UtcDateTime.Date == now.UtcDateTime.Date`;
  - `Version` keeps `Major.Minor` and adds `Build`/`Revision` only when non-zero, building from the right.
- `LinkBuilder.OpenInJellyfin`:
  1. `Normalize(publicUrl)`, which is null when invalid;
  2. `uri.GetLeftPart(UriPartial.Path).TrimEnd('/')`;
  3. if `server.BaseUrl` (normalized to a leading `/`, no trailing one) is non-empty and the path doesn't end with it (OrdinalIgnoreCase), append it;
  4. append `"/web/#/details?id=" + itemId.ToString("N") + "&serverId=" + server.SystemId`.
- `MessageFitter`:
  - `budget = limit - DelayedReserve`;
  - shorten the overview by binary search on its rune count, using `StringInfo`/`Rune`-safe slicing, with a suffix `…`; drop it when fewer than 20 runes remain;
  - then remove list items from the end, adding `more(removed)`, keeping at least one item;
  - then `HtmlText.Truncate(text, budget, "…")`.

Registration: `Formatter` and `LinkBuilder` as singletons.

- [ ] **Step 4: Run all tests.** PASS.
- [ ] **Step 5: Commit**: `feat: formatter, link builder, poster picker and message fitter`

---
### Task 10: Render model on Outbox entries, re-rendering queued sends, sentMessages expiry, the ⏱ line

**Files:**
- Create: `State/Documents/RenderKind.cs`, `State/Documents/RenderModel.cs`
- Modify: `State/Documents/OutboxEntry.cs`, `State/Documents/SentMessage.cs`, `Delivery/Outbox.cs`, `Delivery/OutboxDelivery.cs`, `Delivery/OutboxService.cs`
- Test: `Delivery/OutboxTests.cs`, `Delivery/OutboxDeliveryTests.cs`, `Delivery/OutboxServiceTests.cs`

**Interfaces:**
- Consumes: `Formatter.DelayedLine` (Task 9), `BatchingOptions.LateEditHours`.
- Produces:
  - `enum RenderKind { Media, Digest, Alert }`
  - `sealed class RenderModel` (JSON, in `queue.json`):
    - every kind: `RenderKind Kind`, `string EventKey`, `DateTimeOffset At`;
    - media: `List<string> ItemIds` (Guid `N`), `string ContainerId`, `string LibraryName`, `bool NewSeries`, `bool Upgraded`;
    - digest: `List<RenderModel> Groups`;
    - alerts: `Dictionary<string,string> Values`, `List<string> Lines`.

    Collections are initialized empty and strings to `""`.
  - `OutboxEntry.Model` and `SentMessage.Model` (`RenderModel?`).
  - `sealed record SentRecord(RenderModel Model, DateTimeOffset SentAt, bool Queued)`, in `Delivery/`.
  - `Outbox.FindModel(string destinationId, string key) : SentRecord?`. It takes, in this order:
    1. the model of a queued `Edit` with `TargetKey == key`;
    2. else a queued `Send` with `SentKey == key` (`Queued = true`, `SentAt = QueuedAt`);
    3. else `sentMessages[dest|key]` when it has a model.
  - `Outbox.PruneSent(DateTimeOffset now, int lateEditHours) : int`.
  - `OutboxDelivery.DelayedAfter` (`static readonly TimeSpan` = 5 min). `OutboxDelivery` gains a `Formatter` constructor parameter.

- [ ] **Step 1: Write the failing tests**

`OutboxTests`:
- `EditForAQueuedSend_ReRendersTheSend_InsteadOfQueuingAnEdit`: a queued `Send` with `SentKey = "login_failed:ALICE"` and text "×1". Enqueue an `Edit` with `TargetKey` equal to that key and text "×5". The result is one entry, the original `Send` with the same `EntryId` and `QueuedAt`, text "×5", and the new `Model`. Its fallback flags (`ForceTextOnly`, `NoButtons`, `PlainText`) carry over from the old message.
- `Complete_RecordsTheModel`: a sent message keeps `entry.Model`. Completing an `Edit` replaces the target's `Model` and sets `LastEditAt`.
- `FindModel_PrefersQueuedEdit_ThenQueuedSend_ThenSent`.
- `PruneSent`:
  - `lateEditHours = 6`: a message sent 6 h 1 min ago with no edit is removed; one edited 5 min ago is kept;
  - `lateEditHours = 0`: a `login_failed:*` message sent 9 min ago is kept, and at 11 min it is removed.

  Removals are not counted as evictions.

`OutboxDeliveryTests`:
- `HeldForMoreThan5Minutes_AddsTheDelayedLine`: `QueuedAt = now - 6 min`. The sent text ends with `"\n\n" + formatter.DelayedLine(QueuedAt, now)`. The same applies to a photo caption.
- `Under5Minutes_NoLine`.
- `Edit_NeverGetsTheLine`.
- `PlainTextFallback_KeepsTheLineAsText`.

`OutboxServiceTests.Loop_PrunesSentMessages`.

- [ ] **Step 2: Run, expect failure.**

- [ ] **Step 3: Implement**

- `Outbox.Enqueue`, `Edit` branch: before looking for a queued edit, look for a queued `Send` in the same destination whose `SentKey == entry.TargetKey`. If there is one, replace its `Message` (copying the three fallback flags from the old message) and its `Model`, and return.
- `Outbox.Complete`:
  - for a send, set `SentMessage.Model = entry.Model`;
  - for an edit, set `target.Model = entry.Model ?? target.Model`.

  `OutboxDelivery.OnSuccess` builds the `SentMessage` with `Model = entry.Model`.
- `OutboxDelivery.SendAsync`: when `attempt.EditTarget is null` and `now - entry.QueuedAt > DelayedAfter`, append the delayed line to the HTML body before `Body()` applies the plain-text fallback.
- `OutboxService` loop: call `_outbox.PruneSent(now, _settings.Current.Batching.LateEditHours)` next to `PurgeExpired`.

- [ ] **Step 4: Run all tests.** PASS (plan 2's Outbox tests unchanged except for the new constructor argument).
- [ ] **Step 5: Commit**: `feat: render models on Outbox entries, re-render queued sends, sentMessages expiry and the delayed line`

---

### Task 11: Media layouts and the digest

**Files:**
- Create: `Rendering/MediaLayouts.cs`, `tests/…/Rendering/Golden/movie.caption.txt`, `movie.text.txt`, `episodes.caption.txt`, `album.caption.txt`, `audiobook.caption.txt`, `digest.text.txt`
- Modify: `Strings/en.json`, `tests/…/Jellyfin.Plugin.TgJelly.Tests.csproj` (`<None Update="Rendering/Golden/**" CopyToOutputDirectory="PreserveNewest" />`)
- Test: `tests/…/Rendering/MediaLayoutsTests.cs`

**Interfaces:**
- Consumes: `Formatter`, `LinkBuilder`, `PosterPicker`, `MessageFitter`, `ILibraryAccess`, `SettingsStore` (`Message`, `PublicUrl`), `RenderModel`, `OutboxMessage`/`OutboxButton`.
- Produces: `sealed class MediaLayouts(Formatter formatter, LinkBuilder links, ILibraryAccess library, SettingsStore settings)`, with:
  - `OutboxMessage? Render(RenderModel model)`, which returns null when no item of the group exists any more;
  - `OutboxMessage RenderDigest(RenderModel digest)`.
- The layouts (§8.1). Each segment goes into `MessageParts`, and the fitter runs twice: `Caption` at 1024, `Text` at 4096.

  | Event | Head | List | Overview | Tail |
  |---|---|---|---|---|
  | movie / music video / book | heading (`media.headingYear` or `media.heading`), then `\n` + meta line | — | the item's overview, when `ShowOverview` | optional `media.upgraded` line + `\n` + `media.library` |
  | episodes | optional `media.newSeries` line + `\n` + `media.episodes.heading` | one `media.episode` line per item, sorted by number (`E{00}`; no number → `media.episodeNoNumber`) | — | library |
  | album | `media.album.headingYear`/`heading` + `\n` + `media.album.tracks` plural (`ChildCount`) | — | — | library |
  | audiobook | `media.heading` with the folder's `ParentName` + `\n` + `media.audiobook.parts` plural (the group's item count) | — | — | library |

  - **Meta line:** the parts that exist, joined by `" · "`:
    - `media.rating`, when `ShowRatingGenres` and a rating exists;
    - runtime (`Formatter.Runtime`);
    - genres joined by `", "`, when `ShowRatingGenres`.
  - **Episodes heading:** the season part is `media.season` (`Season {number}`) or `media.specials` for season 0. The count is the plural `media.episodes.count`.
  - **Book:** like a movie, without runtime. An `AudioBook` group (`ContainerId` = the parent folder) uses the audiobook row.
  - **Poster** (`ShowPoster`), via `PosterPicker`:
    - movie, music video, book: the item's own image;
    - episodes: season → series → first episode;
    - album: the album's image;
    - audiobook: the first part, then the folder item.
  - **Buttons:**
    - row 1: `[Open in Jellyfin]`, when `ShowOpenInJellyfin` and a link exists. It points at the item, or the series for episodes, or the album, or the folder.
    - row 2: `[IMDb] [TMDb]`, when `ShowProviderLinks`. Movies use their own IDs (`tmdb` → movie). Episodes use the series' IDs (`tmdb` → tv). Nothing for others.
  - **Digest** (text only, no buttons, no poster): head `digest.heading` (plural over the group count), then one line per library in first-seen order: `digest.library` = `<b>{library}</b>: {entries}`. A group without a library goes under `digest.noLibrary`. Entries are joined by `", "`:
    - movie, music video, book: `digest.titleYear` or `{title}`;
    - episodes: `digest.season` (`{series} S{00}`) plus `digest.episodes` plural (` ({count} episodes)`) when there is more than one, or `digest.episode` (`{series} S{00}E{00}`) for one;
    - album: `digest.album`;
    - audiobook: the folder name.

    When the result is over budget, drop entries from the end and add a last line `digest.more` (plural, `…and {count} more`).
- `en.json` additions (exact copy):

```json
"media.heading": "<b>{title}</b>",
"media.headingYear": "<b>{title}</b> ({year})",
"media.rating": "★ {rating}",
"media.library": "📁 {library}",
"media.upgraded": "♻️ Upgraded",
"media.newSeries": "🆕 New series",
"media.season": "Season {number}",
"media.specials": "Specials",
"media.episodes.heading": "<b>{series}</b> · {season} · {count}",
"media.episodes.count.one": "{count} new episode",
"media.episodes.count.other": "{count} new episodes",
"media.episode": "E{number} {title}",
"media.episodeNoNumber": "{title}",
"media.album.heading": "<b>{artist} — {album}</b>",
"media.album.headingYear": "<b>{artist} — {album}</b> ({year})",
"media.album.tracks.one": "{count} track",
"media.album.tracks.other": "{count} tracks",
"media.audiobook.parts.one": "{count} part",
"media.audiobook.parts.other": "{count} parts",
"digest.heading.one": "🆕 <b>{count} new item in Jellyfin</b>",
"digest.heading.other": "🆕 <b>{count} new items in Jellyfin</b>",
"digest.library": "<b>{library}</b>: {entries}",
"digest.noLibrary": "Other",
"digest.titleYear": "{title} ({year})",
"digest.season": "{series} S{season}",
"digest.episodes.one": "{entry} ({count} episode)",
"digest.episodes.other": "{entry} ({count} episodes)",
"digest.episode": "{series} S{season}E{episode}",
"digest.album": "{artist} — {album}",
"digest.more.one": "…and {count} more",
"digest.more.other": "…and {count} more",
"button.open": "Open in Jellyfin",
"button.imdb": "IMDb",
"button.tmdb": "TMDb"
```

Every value placed into these strings is passed through `Formatter.Escape` first.

- [ ] **Step 1: Write the golden files and the failing tests**

`Golden/movie.caption.txt` (exact; `ShowOverview` and `ShowRatingGenres` on, library "Movies"):

```
<b>Arrival</b> (2016)
★ 7.9 · 1h 56m · Drama, Sci-Fi

A linguist works with the military to talk to alien visitors.

📁 Movies
```

`Golden/episodes.caption.txt` (new series, "Severance" season 2, three episodes given out of order):

```
🆕 New series
<b>Severance</b> · Season 2 · 3 new episodes

E01 Hello, Ms. Cobel
E02 Goodbye, Mrs. Selvig
E03 Who Is Alive?

📁 Shows
```

Write the album, audiobook and digest golden files from the rules above. They pin the layout, so review them by eye. Golden files have no trailing newline; the test compares with `File.ReadAllText(...).TrimEnd('\n')`.

`MediaLayoutsTests`, using `FakeLibrary` and `FakeServerInfo` with `PublicUrl = "https://jf.example.com"`:
- `Movie_MatchesGolden_CaptionAndText`.
- `Movie_Buttons`: row 1 is `Open in Jellyfin` → `https://jf.example.com/web/#/details?id={id:N}&serverId=a1b2c3`. Row 2 is `IMDb` → `https://www.imdb.com/title/tt2543164/` and `TMDb` → `https://www.themoviedb.org/movie/329865`.
- `Toggles_Off_RemoveTheirParts`, one `[Theory]` row per toggle:
  - `ShowPoster` → `PosterPath` is null;
  - `ShowOverview` → no overview;
  - `ShowRatingGenres` → the meta line is the runtime only;
  - `ShowOpenInJellyfin` → no row 1;
  - `ShowProviderLinks` → no row 2.
- `InvalidPublicUrl_NoOpenButton`.
- `Episodes_MatchesGolden_PosterFallsBackSeasonSeriesEpisode`.
- `Episodes_SeasonZero_IsSpecials`.
- `Album_TrackCount_FromChildCount`; `Audiobook_CountsParts_AndUsesFolderName`.
- `Upgraded_AddsTheLine`.
- `AllItemsGone_ReturnsNull`; `SomeItemsGone_RendersTheRest`.
- `HostileTitle_IsEscaped_AndFits`: the title is `"<b>&</b>"` + 3000 × `"😀"`. The caption contains `&lt;b&gt;&amp;&lt;/b&gt;`, `PlainLength(Caption) ≤ 984`, and `PlainLength(Text) ≤ 4056`.
- `NonBmpCaption_AtExactly1024`: a non-BMP title made so the plain caption is exactly 984 code points is not truncated. One code point more, and it is.
- `Digest_MatchesGolden`: 12 groups across "Movies" and "Shows", including a season with 8 episodes and a single episode.
- `Digest_Overflow_EndsWithAndNMore`: 600 movie groups → text ≤ 4056 and the last line is `…and N more`, with N correct.

- [ ] **Step 2: Run, expect failure.**
- [ ] **Step 3: Implement `MediaLayouts`** as specified. Items come from `library.GetItem(Guid.ParseExact(id, "N"))`. The digest reads each group's items the same way and skips missing ones.
- [ ] **Step 4: Run all tests.** PASS on both TFMs.
- [ ] **Step 5: Commit**: `feat: default media layouts, posters, buttons and the digest`

---

### Task 12: Security and server layouts, and the renderer facade

**Files:**
- Create: `Rendering/AlertLayouts.cs`, `Rendering/NotificationRenderer.cs`, `tests/…/Rendering/Golden/login_failed.many.txt`, `server.started.txt`, `plugin_updated.txt`
- Modify: `Strings/en.json`, `PluginServiceRegistrator.cs`
- Test: `tests/…/Rendering/AlertLayoutsTests.cs`, `Rendering/NotificationRendererTests.cs`

**Interfaces:**
- Consumes: `Formatter`, `MessageFitter`, `TimeProvider`, `MediaLayouts`, `RenderModel`, `EventFields`.
- Produces:
  - `sealed class AlertLayouts(Formatter formatter, TimeProvider time)`: `OutboxMessage Render(RenderModel model)`. These are text-only: `Text` is fitted at 4096, `Caption` is the same text fitted at 1024, and there is no poster.
  - `sealed class NotificationRenderer(MediaLayouts media, AlertLayouts alerts)`: `OutboxMessage? Render(RenderModel model, Destination destination)`. It dispatches on `Kind` and sets `Silent = destination.SilentGroups.Contains(EventCatalog.GroupOf(model.EventKey))`; `media.digest` counts as `media`. Plan 5 adds templates here.
- The layouts. Values come from `model.Values`, keyed by `EventFields`; times use `model.At` and `time.GetUtcNow()`. A `{details}` line is the parts that exist joined by `" · "`, and empty parts are skipped.

  | Event key | Head | Following lines |
  |---|---|---|
  | `security.login_failed` | `security.login_failed.heading` | attempts = 1: `{ip} · {client} · {device} · {time}`. More: `security.login_failed.many`. Then `security.locked` when `locked == "true"`. |
  | `security.lockout` | `security.lockout` plural over `attempts` | — |
  | `security.login_succeeded` | `security.login_succeeded.heading` | `{client} · {device}`, `security.ip`, `security.at` |
  | `security.user_created` / `user_deleted` / `password_changed` | one line each | — |
  | `server.started` | `server.started` | — |
  | `server.update_available` | `server.update_available` | Button row `[Release notes]` → `url` |
  | `server.plugin_installed` / `plugin_updated` / `plugin_uninstalled` / `plugin_install_failed` | their heading | `plugin_updated` with a changelog, and `plugin_install_failed` with an error: `<blockquote expandable>{escaped text}</blockquote>` |
  | `server.task_failed` | `server.task_failed` | `<blockquote expandable>{error}</blockquote>` when the error isn't empty |
  | `server.restart_pending` | `server.restart_pending` | One line per `model.Lines` entry (already rendered by the coalescer from `server.reason.*`) |

  Fitting: the blockquote text is the `Overview` part, so it is shortened first.
- `en.json` additions (exact copy):

```json
"security.login_failed.heading": "🔐 <b>Wrong password for {user}</b>",
"security.login_failed.many": "×{attempts} from {ips} · last {ip} · {client} · {time}",
"security.ips.one": "{count} IP",
"security.ips.other": "{count} IPs",
"security.locked": "🔒 locked",
"security.lockout.one": "🔒 <b>{user}</b> is locked out after {count} failed attempt",
"security.lockout.other": "🔒 <b>{user}</b> is locked out after {count} failed attempts",
"security.login_succeeded.heading": "🔐 <b>{user} signed in</b>",
"security.ip": "🌐 {ip}",
"security.at": "🕒 {time}",
"security.user_created": "🔐 <b>New user: {user}</b>",
"security.user_deleted": "🔐 <b>User deleted: {user}</b>",
"security.password_changed": "🔐 <b>Password changed for {user}</b>",
"server.started": "✅ Jellyfin is up · {version} · <i>{server}</i>",
"server.update_available": "⬆️ Jellyfin <b>{version}</b> is available (running {current})",
"server.plugin_installed": "⚙️ <b>Plugin installed: {plugin} {version}</b>",
"server.plugin_updated": "⚙️ <b>Plugin updated: {plugin} {version}</b>",
"server.plugin_uninstalled": "⚙️ <b>Plugin removed: {plugin} {version}</b>",
"server.plugin_install_failed": "⚙️ <b>Plugin install failed: {plugin} {version}</b>",
"server.task_failed": "⚙️ <b>Task failed: {task}</b>",
"server.restart_pending": "⚙️ <b>Jellyfin needs a restart</b>",
"server.reason.server.plugin_installed": "Installed {plugin} {version}",
"server.reason.server.plugin_updated": "Updated {plugin} to {version}",
"server.reason.server.plugin_uninstalled": "Removed {plugin} {version}",
"server.reason.server.plugin_install_failed": "Failed to install {plugin} {version}",
"button.releaseNotes": "Release notes"
```

- [ ] **Step 1: Write the golden files and the failing tests**

`Golden/server.started.txt` (version `12.2.0`, server `Home`): `✅ Jellyfin is up · 12.2.0 · <i>Home</i>`

`Golden/login_failed.many.txt` (user `alice`, 37 attempts, 12 IPs, last `203.0.113.5`, client `Infuse`, at today 14:05:09Z, `locked = "true"`):

```
🔐 <b>Wrong password for alice</b>
×37 from 12 IPs · last 203.0.113.5 · Infuse · <tg-time unix="1792332309" format="t">14:05 UTC</tg-time>
🔒 locked
```

`AlertLayoutsTests`:
- each golden file matches;
- `LoginFailed_SingleAttempt` gives line 2 = `203.0.113.5 · Infuse · iPhone · <tg-time …>`, and an empty device is skipped;
- `Lockout_Plural`;
- `UpdateAvailable_HasReleaseNotesButton`;
- `TaskFailed_EmptyError_NoBlockquote`;
- `HugeChangelog_IsShortened_FitsIn4096`;
- `UserControlledValues_AreEscaped` (`user = "<script>"`);
- `RestartPending_ListsReasons`.

`NotificationRendererTests`: `SilentGroups = ["security"]` makes `security.*` silent, `media.digest` follows `media`, and dispatch by `Kind` works.

- [ ] **Step 2: Run, expect failure.**  - [ ] **Step 3: Implement.**  - [ ] **Step 4: Run all tests.** PASS.

Registration: `MediaLayouts`, `AlertLayouts`, `NotificationRenderer` as singletons.

- [ ] **Step 5: Commit**: `feat: security and server layouts, and the renderer`

---

### Task 13: MediaAggregator

**Files:**
- Create: `State/Documents/MediaGroupState.cs`, `State/Documents/ReleaseBuffer.cs`, `Pipeline/GroupKeys.cs`, `Pipeline/MediaOutputKind.cs`, `Pipeline/MediaOutput.cs`, `Pipeline/MediaAggregator.cs`, `tests/…/Fakes/FakeScanState.cs`
- Modify: `State/Documents/QueueDocument.cs`
- Test: `tests/…/Pipeline/MediaAggregatorTests.cs`, `Pipeline/GroupKeysTests.cs`

**Interfaces:**
- Consumes: `MediaReady`, `Destination`, `SettingsStore` (`Batching`), `Outbox.FindModel`, `IScanState`, `StateStore`, `TimeProvider`.
- Produces:
  - `sealed class MediaGroupState { string DestinationId; string Key; DateTimeOffset FirstAt; DateTimeOffset LastAt; RenderModel Model; }`
  - `sealed class ReleaseBuffer { DateTimeOffset FirstAt; DateTimeOffset LastAt; List<MediaGroupState> Groups; }`
  - `QueueDocument.OpenGroups` (`List<MediaGroupState>`) and `QueueDocument.ReleaseBuffers` (`Dictionary<string, ReleaseBuffer>` keyed by destination ID)
  - `static class GroupKeys`: `(string Key, string EventKey, string ContainerId) For(MediaItem item)`:

    | Kind | Key | Event key | ContainerId |
    |---|---|---|---|
    | Movie | `movie:{Id:N}` | `media.movie` | the item |
    | MusicVideo | `musicvideo:{Id:N}` | `media.musicvideo` | the item |
    | Book | `book:{Id:N}` | `media.book` | the item |
    | AudioBook | `audiobook:{ParentId:N}` | `media.book` | the parent folder |
    | Episode | `episodes:{SeriesId:N}:{SeasonId:N}` (`…:s{SeasonNumber}` when `SeasonId` is empty) | `media.episodes` | the season (the series when there is no season ID) |
    | MusicAlbum | `album:{Id:N}` | `media.album` | the album |

  - `enum MediaOutputKind { Group, Digest, LateEdit }`
  - `sealed record MediaOutput(string DestinationId, MediaOutputKind Kind, IReadOnlyList<MediaGroupState> Groups)`
  - `sealed class MediaAggregator(StateStore state, SettingsStore settings, Outbox outbox, IScanState scan, TimeProvider time)`:
    - `const int OpenGroupsCap = 5_000, BufferCap = 2_000`;
    - `static readonly TimeSpan ScanHoldLimit = 120 min`;
    - `IReadOnlyList<MediaOutput> Add(MediaReady ready, IReadOnlyList<Destination> destinations)`;
    - `IReadOnlyList<MediaOutput> Tick()`;
    - `IReadOnlyList<MediaOutput> ScanFinished()`.

**Rules (§7.4 steps 4–7), for each destination in `Add`:**
1. **An open group with the same key:** add the item ID (no duplicates), set `LastAt = now`, `NewSeries |= ready.NewSeries`, `Upgraded &= ready.Upgraded`.
2. **Else a group with that key in this destination's release buffer:** add the item there. `buffer.LastAt` doesn't change, because no new group entered.
3. **Else, when `LateEditHours > 0`, `outbox.FindModel(d.Id, key)` exists and (`Queued` or `now - SentAt < LateEditHours`):** emit a `LateEdit` whose single group holds the merged model (the old `ItemIds` plus the new ID, with the flags merged as in rule 1). It is not stored.
4. **Else a new open group** `{FirstAt = LastAt = now, Model = { Kind = Media, EventKey, ItemIds = [id], ContainerId, LibraryName = ready.Libraries.FirstOrDefault()?.Name ?? "", NewSeries, Upgraded, At = now }}`. When the destination already has `OpenGroupsCap` open groups, its oldest one is closed into its buffer first.

**`Tick()`** (now = `time.GetUtcNow()`, scan = `scan.IsScanRunning`):
1. **Closing:** a group closes when `now - LastAt ≥ QuietWindowSeconds` or `now - FirstAt ≥ MaxHoldMinutes`. A closed group moves into its destination's buffer: the buffer is created with `FirstAt = now` if needed, and `LastAt = now`.
2. **Releasing:** a buffer is released when `Groups.Count ≥ BufferCap`, or `!scan && now - LastAt ≥ ReleaseQuietSeconds`, or `!scan && now - FirstAt ≥ MaxHoldMinutes`, or `scan && now - FirstAt ≥ ScanHoldLimit`.
3. **What a release emits:** more than `DigestThreshold` groups → one `Digest` output with all of them; otherwise one `Group` output per group, in `FirstAt` order. Then the buffer is removed.

**`ScanFinished()`** closes every open group and releases every buffer, ignoring the timers.

- [ ] **Step 1: Write the failing tests** (`FakeScanState { bool IsScanRunning { get; set; } }`, `FakeTimeProvider`, a real `StateStore` and `Outbox`)

- `GroupKeysTests`: one row per kind, plus a season without an ID.
- `QuietWindow_ClosesTheGroup`: 3 episodes of one season, 10 s apart. At `LastAt + 119 s` the group is still open; at `+120 s` it is in the buffer. After `ReleaseQuietSeconds` with no scan: one `Group` output with 3 items.
- `MaxHold_ClosesABusyGroup`: an item every 60 s keeps the group from going quiet; it closes at 30 min.
- `ScanFinished_ClosesAndReleasesEverything`.
- `ExactlyThreshold_SendsGroups_ThresholdPlusOne_SendsADigest`: 10 movies give 10 `Group` outputs; 11 give 1 `Digest`.
- `HeldWhileScanRunning_UntilScanFinished`: groups in the buffer, `scan = true`, advance 25 min → nothing. `ScanFinished()` → released.
- `LongScan_ReleasesEvery2Hours`: `scan = true` throughout and a new movie every 10 min for 5 h → releases at about 2 h and about 4 h (more than 10 groups each → digests), never earlier.
- `BufferMaxHold_WithoutScan`.
- `LateItem_WithinLateEditHours_EmitsLateEdit`, `LateItem_After6h_StartsANewGroup`, `LateEditHoursZero_NeverEdits`:
  - set up a group through Tick and release;
  - enqueue its `OutboxEntry` with `SentKey = key` and complete it with a `SentMessage` that carries the model;
  - add a new episode of the same season at 5 h and at 7 h.
- `LateItem_WhileTheSendIsStillQueued_EmitsLateEdit`.
- `DigestedGroups_AreNeverEdited`: release a digest. Its outbox entry has `SentKey = null`, so a late item for one of its keys starts a new group.
- `TwoDestinations_KeepSeparateGroups`.
- `OpenGroupsCap_ClosesTheOldestIntoTheBuffer`, using an `internal int OpenGroupsCapOverride`.
- `BufferCap_ReleasesAtOnce`: with an `internal int BufferCapOverride = 3`, the 3rd closed group triggers a release on the next `Tick()`, even while a scan runs.
- `BigScan_3000Movies_GivesOneDigestPerDestination`: 3000 `Add`s, each routed to both of 2 destinations, during a scan, with no `Tick()` in between. Then `ScanFinished()` → exactly 2 `Digest` outputs, one per destination, with 3000 groups each, 0 `Group` outputs, finishing in under 2 s.

- [ ] **Step 2: Run, expect failure.**  - [ ] **Step 3: Implement** with all state in `QueueDocument` through `StateStore.UpdateQueue`.  - [ ] **Step 4: Run all tests.** PASS.
- [ ] **Step 5: Commit**: `feat: MediaAggregator with group windows, release buffers, digests and late-arrival edits`

---

### Task 14: AlertCoalescer

**Files:**
- Create: `State/Documents/SecurityWindow.cs`, `State/Documents/DedupeSection.cs`, `State/Documents/RecentPluginEvent.cs`, `Pipeline/AlertOutput.cs`, `Pipeline/AlertCoalescer.cs`
- Modify: `State/Documents/QueueDocument.cs`
- Test: `tests/…/Pipeline/AlertCoalescerTests.cs`

**Interfaces:**
- Consumes: `NotificationEvent`, `EventFields`, `StringTable` (`server.reason.*`), `StateStore`, `TimeProvider`.
- Produces:
  - `sealed class SecurityWindow { string User; DateTimeOffset FirstAt; DateTimeOffset LastAt; int Attempts; List<string> Ips; string LastIp; string Client; string Device; bool Locked; }`
  - `sealed class DedupeSection { Dictionary<string, DateTimeOffset> Logins; Dictionary<string, DateTimeOffset> UserCreated; }`
  - `sealed class RecentPluginEvent { DateTimeOffset At; string EventKey; string Plugin; string Version; }`
  - `QueueDocument.SecurityWindows` (`Dictionary<string, SecurityWindow>` keyed by user key), `QueueDocument.Dedupe`, `QueueDocument.RecentPluginEvents` (`List<RecentPluginEvent>`)
  - `sealed record AlertOutput(RenderModel Model, OutboxOp Op, string? SentKey, string? TargetKey, string? ReplyToKey)`
  - `sealed class AlertCoalescer(StateStore state, StringTable strings, TimeProvider time)`:
    - `static readonly TimeSpan Window = 10 min, LockoutHold = 5 s, UserCreatedMerge = 10 s, LoginDedupe = 12 h, RestartReasons = 10 min`;
    - `const int WindowsCap = 500, LoginDedupeCap = 2_000, UserCreatedCap = 100, PluginEventsCap = 50`;
    - `IReadOnlyList<AlertOutput> Process(NotificationEvent e)`;
    - `IReadOnlyList<AlertOutput> Tick()`;
    - `IReadOnlyList<AlertOutput> Drain()`;
    - `static string LoginFailedKey(string userKey)` (= `"login_failed:" + userKey`).

**Rules (§7.5):**
- **`login_failed`:** update the window for `user_key`. Once `now ≥ LastAt + Window`, the old window is ended and a new one starts. The window counts attempts, keeps a distinct IP list, and stores the last ip, client and device. The model is `Kind = Alert`, `At = LastAt`, `Values = { user, attempts, ip_count, ip, client, device, locked }`.
  - first attempt of a window → `Send` with `SentKey = LoginFailedKey(key)`;
  - later attempts → `Edit` with `TargetKey` = that key and `SentKey` = that key.
- **`lockout`:** held in memory with `due = At + LockoutHold`. When `Tick()` reaches `due`, or on `Drain()`:
  - `Send` the lockout model (`attempts` = the window's `Attempts` if a window exists, else the event's `attempts`), with `ReplyToKey = LoginFailedKey(key)` when a window exists;
  - if a window exists: set `Locked = true` and also emit an `Edit` of the login_failed message.
- **`user_created`:** `Send` it, and store `UserCreated[user_id] = At`. A **`password_changed`** within `UserCreatedMerge` of that marker for the same `user_id` gives nothing; otherwise it gives a `Send`.
- **`login_succeeded`:** nothing if `Logins[user_id|device_id]` is younger than 12 h. Otherwise store it and `Send`.
- **`server.plugin_*`:** append a `RecentPluginEvent`, then `Send`. **`server.restart_pending`:** `Lines` = the reasons from the last 10 min, each rendered with `strings.Get("server.reason." + EventKey, plugin, version)` and escaped by the layout. Then `Send`.
- **Every other key:** `Send`.
- **`Tick()`** also removes ended windows and expired dedupe and plugin entries, and applies the caps (oldest first, counted in `evictions`).

- [ ] **Step 1: Write the failing tests**

- `LockoutBeforeLoginFailed_IsHeld_ThenRepliesAndMarksLocked`: `lockout` at t0, then `login_failed` for the same user at t0 + 0.1 s → one `Send` (login_failed). At t0 + 5 s, `Tick()` → a lockout `Send` with `ReplyToKey = "login_failed:ALICE"`, plus an `Edit` with `locked = "true"`.
- `LockoutAfterLoginFailed_SameResult`.
- `LockoutWithoutWindow_IsItsOwnMessage_NoReply`.
- `CredentialStuffing_50Ips_OneSendThenEdits`: 50 attempts from 50 IPs within 10 min → 1 `Send` and 49 `Edit`s on the same key. The last model has `attempts = "50"` and `ip_count = "50"`.
- `Window_Slides`: attempts 9 min apart stay in one window; an 11-min gap starts a new `Send`.
- `UsernameCase_IsOneWindow` (`Alice` and `ALICE`).
- `UserCreatedThenPasswordChanged_Within10s_OnlyCreation`; `PasswordChanged_After10s_IsSent`.
- `LoginSucceeded_DedupedPerUserAndDevice_For12h`: another device is sent; the same device after 12 h is sent.
- `RestartPending_ListsPluginEventsFromTheLast10Minutes`.
- `Drain_ReleasesHeldLockoutsAtOnce`.
- `Restart_KeepsWindows`: persist, then build a new coalescer over the same state. A later attempt is an `Edit`.
- `WindowsCap_EvictsOldest`.

- [ ] **Step 2: Run, expect failure.**  - [ ] **Step 3: Implement.**  - [ ] **Step 4: Run all tests.** PASS.
- [ ] **Step 5: Commit**: `feat: AlertCoalescer for failed logins, lockouts, user and plugin events`

---

### Task 15: NotificationPipeline, PipelineService, the graceful drain and service order

**Files:**
- Create: `Pipeline/NotificationPipeline.cs`, `Pipeline/PipelineService.cs`
- Modify: `PluginServiceRegistrator.cs`, `tests/…/PluginServiceRegistratorTests.cs`
- Test: `tests/…/Pipeline/NotificationPipelineTests.cs`, `Pipeline/PipelineServiceTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 5–14, plus `Outbox.Enqueue` and `Router.Route`.
- Produces:
  - `sealed class NotificationPipeline(IntakeQueues queues, MediaReadiness readiness, MediaAggregator media, AlertCoalescer alerts, NotificationRenderer renderer, Outbox outbox, SettingsStore settings, StateStore state, TimeProvider time, ILogger<NotificationPipeline> logger)`:
    - `const int BatchSize = 500`;
    - `void Start()` (calls `readiness.Load()`);
    - `int RunOnce()`: takes up to `BatchSize` from each queue, processes them, then ticks readiness, media and alerts, and folds `queues.TakeDropped()` into `status.counters.droppedIntake`. Returns the number of intake events processed.
    - `void Drain()`: `RunOnce()` until both queues are empty, then `alerts.Drain()`.
  - `sealed class PipelineService(InstanceGate gate, CompatResult compat, StateStore state, IntakeQueues queues, NotificationPipeline pipeline, TimeProvider time, ILogger<PipelineService> logger) : BackgroundService`, with `static readonly TimeSpan DrainBudget = 5 s`.

**Composition rules:**
- **Media:** for each `MediaReady`:
  1. `GroupKeys.For(item)` gives the event key;
  2. `Router.Route(eventKey, libraries.Select(l => l.Id).ToList(), destinations)`;
  3. `media.Add(ready, routed)`.

  `ScanFinished` intake → `media.ScanFinished()`.
- **Each `MediaOutput`** becomes an `OutboxEntry` with `EntryId = Guid.NewGuid():N`, `QueuedAt = now`, and `Message = renderer.Render(model, destination)`. A null message is skipped.

  | Output | Op | EventKey | GroupKey | SentKey | TargetKey | Model |
  |---|---|---|---|---|---|---|
  | `Group` | `Send` | the group's | the group's key | the group's key | — | the group's |
  | `LateEdit` | `Edit` | the group's | the group's key | the key | the key | the merged model |
  | `Digest` | `Send` | `media.digest` | `digest` | null | — | `{Kind = Digest, EventKey = "media.digest", Groups = …}` |

- **Each `AlertOutput`:** route by `Model.EventKey` with `libraryIds = null`. For each destination, an entry with the output's `Op`, `SentKey`, `TargetKey` and `ReplyToKey`, and `GroupKey = Model.EventKey`.
- **Destinations** come from `settings.Current.Destinations`, read once per `RunOnce`.
- **Failures:** one output that throws (in rendering or enqueueing) is logged, recorded with `state.RecordError("pipeline", …)` and skipped. The loop goes on.

**`PipelineService`:**
- Runs inside `Supervisor.RunAsync("pipeline", …)`. It waits for ownership; on an incompatible build it returns. Once `state.IsLoaded`, it calls `pipeline.Start()`.
- Loop: `await queues.WaitAsync(1 s, ct)`, then `pipeline.RunOnce()`.
- `StopAsync`: `base.StopAsync`, then, only if `Start()` ran, `pipeline.Drain()` inside `try/catch`. The drain stops early once `DrainBudget` has passed (checked between batches). It never throws.

**Singletons registered here** (Tasks 7, 13 and 14 leave registration to this task): `MediaReadiness`, `MediaAggregator`, `AlertCoalescer`, `NotificationPipeline`, `PipelineService`, plus their redacted loggers.

**Hosted-service registration order (final):** `InstanceGate`, `PersistenceService`, `PipelineService`, `LibraryWatcher`, `TaskWatcher`, `RestartWatcher`, `StartupWatcher`, `UpdateChecker` (Task 16 adds it in this position), `BotSupervisor`, `HealthService`, `OutboxService`.

They stop in reverse order. The Outbox stops first. The watchers unsubscribe before the pipeline drains, and persistence flushes after the drain (§12). Write that as a comment above the block.

- [ ] **Step 1: Write the failing tests**

These use a harness over `BotTestHarness` + `FakeLibrary` + `FakeServerInfo` + `FakeScanState`, built with real units.

`NotificationPipelineTests`:
- `MovieAdded_Ready_Released_IsOneOutboxEntryWithPoster`:
  - a `NewMedia` destination, `Added` + `Updated` intake for a movie with a poster temp file;
  - advance past the quiet and release windows, calling `RunOnce()` each second;
  - one `Send` entry: `EventKey = "media.movie"`, `SentKey = "movie:{id:N}"`, `Message.PosterPath` set, `Model.ItemIds = [id]`.
- `LibraryFilter_SkipsOtherLibraries`.
- `LoginFailedTwice_IsSendThenEditMergedIntoTheQueuedSend`: an `AdminAlerts` destination, with the Outbox not delivering. Two `login_failed` events give one queued entry whose text says `×2`.
- `ServerStarted_GoesOnlyToSubscribers`.
- `RenderFailure_IsRecorded_AndOtherOutputsStillGo`: a `FakeLibrary.GetItem` that throws for one item.
- `IntakeDrops_AreCounted`.
- `RestartMidBatch_AnnouncesEachGroupOnce`:
  1. 3 episodes are `Added`; one is `Updated`; another season is in an open group; a third group is in a release buffer.
  2. `state.FlushAll()`.
  3. Build a fresh pipeline (new instances) over the same folder and `Start()`.
  4. `Updated` for the remaining items, then advance through all windows.
  5. Exactly one `Send` per group key ends up in the Outbox, with no duplicates and every item ID present once.

`PipelineServiceTests`:
- `Stop_DrainsIntake_AndSendsHeldLockouts_Within5s`: publish 1000 media events and a held lockout, then `StopAsync`. The intake is empty, the lockout entry is queued, and the stop takes less than `DrainBudget + 1 s`.
- `Stop_DoesNotThrow_WhenPipelineThrows`.
- `Incompatible_NeverStarts`.

`PluginServiceRegistratorTests`: the hosted-service order above is asserted by type, and every new `ILogger<T>` user is in the redacted list (the existing reflection test).

- [ ] **Step 2: Run, expect failure.**  - [ ] **Step 3: Implement.**  - [ ] **Step 4: Run all tests.** PASS, repeated 3× (`for i in 1 2 3; do …; done`) to catch timing flakiness.
- [ ] **Step 5: Commit**: `feat: the notification pipeline, its hosted loop and the graceful drain`

---

### Task 16: UpdateChecker

**Files:**
- Create: `Updates/GitHubRelease.cs`, `Updates/IReleaseFeed.cs`, `Updates/GitHubReleaseFeed.cs`, `Updates/ReleaseVersion.cs`, `Updates/UpdateChecker.cs`, `State/Documents/UpdateCheckState.cs`, `tests/…/Fakes/FakeReleaseFeed.cs`
- Modify: `State/Documents/StateDocument.cs`, `Strings/en.json`, `PluginServiceRegistrator.cs`
- Test: `tests/…/Updates/ReleaseVersionTests.cs`, `Updates/UpdateCheckerTests.cs`, `Updates/GitHubReleaseFeedTests.cs`

**Interfaces:**
- Consumes: `TelegramClientFactory.CreateHandler(ProxySettings, BotSecrets)` (plan 2, `internal static`), `SettingsStore`, `SecretsStore`, `IServerInfo.Version`, `IntakeQueues.Publish`, `IServerStartup`.
- Produces:
  - `sealed record GitHubRelease(string TagName, bool Draft, bool Prerelease, string HtmlUrl)`
  - `interface IReleaseFeed`: `Task<IReadOnlyList<GitHubRelease>> GetReleasesAsync(CancellationToken ct)`
  - `sealed class GitHubReleaseFeed(SettingsStore settings, SecretsStore secrets) : IReleaseFeed`:
    - `const string Url = "https://api.github.com/repos/jellyfin/jellyfin/releases?per_page=20"`;
    - `internal Func<HttpMessageHandler>? HandlerOverride`.
  - `static class ReleaseVersion`:
    - `bool TryParse(string? tag, out Version version)`. It is lenient: strip a leading `v`, then take the leading run of digits and dots, so `v12.2` → 12.2.0.0, `v10.11.11` → 10.11.11.0, and `12.3-rc1` → 12.3.0.0.
    - `Version Normalize(Version v)` (four parts).
  - `sealed class UpdateCheckState { DateTimeOffset? LastCheckedAt; string LastNotifiedVersion = ""; int ConsecutiveFailures; string? LastError; }`, stored as `StateDocument.UpdateCheck`.
  - `sealed class UpdateChecker(InstanceGate gate, CompatResult compat, IServerStartup startup, SettingsStore settings, StateStore state, IServerInfo server, IReleaseFeed feed, IntakeQueues queues, Formatter formatter, TimeProvider time, ILogger<UpdateChecker> logger) : BackgroundService`:
    - `static readonly TimeSpan FirstDelay = 5 min, Interval = 12 h`;
    - `internal Task<bool> CheckOnceAsync(CancellationToken ct)`.
  - `en.json`: `"update.failing": "Update check failing: {reason}"`.

**Rules:**
- **Subscribed** = `settings.Current.UpdateCheck.Enabled` and some destination that is `Enabled` and has `server.update_available`. The loop re-evaluates this every minute. While unsubscribed it makes no network calls and clears its schedule.
- **Schedule:** when a subscription starts (or at startup while subscribed), the next check is `max(now + FirstDelay, LastCheckedAt + Interval)`. After each check, the next is `+ Interval`.
- **A check:**
  1. Skip drafts and prereleases.
  2. Parse each tag and compare normalized versions with `server.Version`.
  3. `SameMajor` keeps only the same `Major`; `AnyNewer` keeps any newer one.
  4. Take the highest. If its tag is newer than `LastNotifiedVersion` (also parsed), publish `server.update_available` with `version = tag without "v"`, `current = formatter.Version(server.Version)` and `url = HtmlUrl`, then set `LastNotifiedVersion`.
- **Failures:** a 403, 429, network error or timeout increments `ConsecutiveFailures` and sets a redacted `LastError`, retrying at the next interval. Success resets both. No Telegram message is ever sent about a failure.
- **`GitHubReleaseFeed`:**
  - builds a new `HttpClient` for each call from `HandlerOverride?.Invoke() ?? TelegramClientFactory.CreateHandler(settings.Current.Proxy, secrets.Load())`, disposed after the call;
  - `Timeout = 30 s`; `User-Agent: TgJelly/{typeof(Plugin).Assembly.GetName().Version}`; `Accept: application/vnd.github+json`;
  - deserializes `tag_name`, `draft`, `prerelease` and `html_url` with `System.Text.Json`;
  - throws `HttpRequestException` on a non-success status.

- [ ] **Step 1: Write the failing tests**

- `ReleaseVersionTests`: `v12.2`, `v10.11.11`, `12.3-rc1`, `garbage` (false) and `""` (false).
- `UpdateCheckerTests`, using `FakeReleaseFeed` (a settable list or exception, and a call counter):
  - `NotSubscribed_NeverCalls`: advance 13 h → 0 calls.
  - `FirstCheck_5MinAfterSubscription_ThenEvery12h`.
  - `SameMajor_IgnoresOtherMajors_AnyNewer_DoesNot`: running 10.11.11; releases `v12.2` and `v10.11.12`. `SameMajor` gives `10.11.12`; `AnyNewer` gives `12.2`.
  - `DraftsAndPrereleases_Skipped`.
  - `EachVersion_AnnouncedOnce`.
  - `ThreeFailuresInARow_ShowFailing`: `ConsecutiveFailures == 3` and `LastError` is set; a success resets them.
  - `Incompatible_NeverRuns`.
- `GitHubReleaseFeedTests`:
  - `SendsUserAgent_AndParses`: a handler override records the request and returns JSON.
  - `UsesTheBotsProxySettings`: with `ProxyMode.Http` and `proxy.example.com:3128`, the handler from `TelegramClientFactory.CreateHandler` has `Proxy` pointing there. Add `internal Func<ProxySettings, BotSecrets, HttpMessageHandler> HandlerBuilder`, default `TelegramClientFactory.CreateHandler`, so the test can capture its arguments.
  - `Non200_Throws`.

- [ ] **Step 2: Run, expect failure.**  - [ ] **Step 3: Implement.** Register `IReleaseFeed` → `GitHubReleaseFeed` and `UpdateChecker` (hosted, in the position Task 15 fixed), with redacted loggers.  - [ ] **Step 4: Run all tests.** PASS.
- [ ] **Step 5: Commit**: `feat: Jellyfin update checker over GitHub releases`

---

### Task 17: API: Libraries, PublicUrl suggestion, Status counts, removed-library flags

**Files:**
- Create: `Api/LibrariesController.cs`, `Api/LibraryDto.cs`
- Modify: `Api/SettingsController.cs`, `Api/SettingsDto.cs`, `Api/TgJellyController.cs`, `Api/StatusDto.cs`, `Api/DestinationsController.cs`, `Api/DestinationDto.cs`
- Test: `tests/…/Api/LibrariesControllerTests.cs`, extend `SettingsControllerTests.cs`, `TgJellyControllerTests.cs`, `DestinationsControllerTests.cs`

**Interfaces:**
- Produces:
  - `GET TgJelly/Libraries` → `LibraryDto[] { string Id (N), string Name, string? CollectionType }` (§11.2);
  - `SettingsDto.PublicUrlSuggestion` (`string?`, from `LinkBuilder.Suggest()`; the page labels it "verify");
  - `StatusDto` fields:
    - `PendingMedia` (`int`, from `MediaReadiness.PendingCount`, 0 before load);
    - `UpdateCheckFailing` (`string?`, `strings.Get("update.failing", ("reason", LastError))` when `ConsecutiveFailures ≥ 3`);
    - `RemovedLibraryFilters` (`int`, the number of destinations where `LibraryFilter.RefersOnlyToRemoved`);
  - `DestinationDto.RemovedLibraryIds` (`string[]`) and `DestinationDto.LibraryFilterBroken` (`bool`).

- [ ] **Step 1: Write the failing tests**

- `LibrariesControllerTests.ListsVirtualFolders`: `FakeLibrary.VirtualFolders` has two entries → two DTOs with `N`-format IDs.
- `SettingsControllerTests`:
  - `Get_IncludesTheNormalizedSuggestion`: `FakeServerInfo.Suggestion = "http://192.168.1.5:8096/"` → `"http://192.168.1.5:8096"`.
  - `Get_InvalidSuggestion_IsNull`: `http://jellyfin:8096` → `null`.
- `TgJellyControllerTests`: `Status_PendingMedia`, `Status_UpdateCheckFailing_After3`, `Status_RemovedLibraryFilters`.
- `DestinationsControllerTests.Get_FlagsRemovedLibraries`: one destination with `[L1, Lgone]` → `RemovedLibraryIds = [Lgone]` and `LibraryFilterBroken == false`; another with `[Lgone]` → broken.

- [ ] **Step 2–4: fail, implement, pass** (Task 1's command).
- [ ] **Step 5: Commit**: `feat: libraries endpoint, public URL suggestion, pending-media and update-check status`

---

### Task 18: CI end to end: a movie and a restart through real Jellyfin

**Files:**
- Modify: `tests/integration/botapi_stub.py`, `tests/integration/test_botapi_stub.py`, `tests/integration/smoke.py`, `tests/integration/test_smoke.py`
- No workflow change is needed; the four `--stub` jobs run the new steps.

**Interfaces:**
- Produces:
  - `StubBotApi` parses `multipart/form-data` bodies (stdlib `email.parser.BytesParser` with a synthetic header), so `sendPhoto` calls are recorded with `chat_id` (as an int) and `caption`;
  - `stub.messages(chat_id) -> list[dict]` gives that chat's `sendMessage`/`sendPhoto` bodies;
  - in `smoke.py`: `create_library(base, token, container)`, `add_sample_movie(container)` and `media_and_restart_checks(base, token, stub, container)`.

- [ ] **Step 1: Write the failing script tests**

- `test_botapi_stub.py`:
  - `test_records_multipart_send_photo`: POST a multipart body with `chat_id=-100…`, `caption="<b>Smoke Movie</b>"` and a small file part to `/bot…/sendPhoto`. The call is recorded with an int `chat_id` and the caption, and the reply is `ok` with a `photo` array (`[{"file_id":"f1","file_unique_id":"u1","width":1,"height":1}]`).
  - `test_messages_filters_by_chat`.
- `test_smoke.py`: `test_sample_movie_command` checks that the ffmpeg command line uses `/usr/lib/jellyfin-ffmpeg/ffmpeg`, `-f lavfi`, and the target path `/media/movies/Smoke Movie (2024)/Smoke Movie (2024).mkv`.

- [ ] **Step 2: Run them, expect failure**

Run: `python3 -m unittest discover -s tests/integration -p 'test_*.py' -v`
Expected: FAIL (`messages` and the multipart handling don't exist yet).

- [ ] **Step 3: Implement**

- **Stub:**
  - multipart parsing;
  - `sendPhoto` replies with a `photo` array;
  - `sendMessage`/`sendPhoto` replies echo `chat` and `caption`/`text`.
- **`smoke.py --stub`**, after the existing token check, with the order changed:
  1. `PUT Settings`: copy the current settings and set `Batching {QuietWindowSeconds: 5, ReleaseQuietSeconds: 5, MaxHoldMinutes: 1, ReadyTimeoutMinutes: 1, DigestThreshold: 10, LateEditHours: 6}`.
  2. `POST Destinations {ChatId: SMOKE_CHAT, Preset: "NewMedia"}`. It still asserts the automatic test message.
  3. `docker exec <c> mkdir -p "/media/movies/Smoke Movie (2024)"`, then `docker exec <c> /usr/lib/jellyfin-ffmpeg/ffmpeg -loglevel error -f lavfi -i testsrc=duration=2:size=320x240:rate=5 -c:v mpeg4 "/media/movies/Smoke Movie (2024)/Smoke Movie (2024).mkv"`.
  4. `POST /Library/VirtualFolders?name=Movies&collectionType=movies&paths=%2Fmedia%2Fmovies&refreshLibrary=true`, with body `{"LibraryOptions": {"EnableRealtimeMonitor": false}}`. Then `POST /Library/Refresh`.
  5. `wait_until(…, 180)`: a `sendMessage` text or `sendPhoto` caption for `SMOKE_CHAT` contains `Smoke Movie`.
  6. `POST Destinations {ChatId: SMOKE_CHAT_2 = -1009876543210, Preset: "AdminAlerts"}`, then `POST /System/Restart`.
  7. `wait_until` healthy (240 s) and `ConnectionState == "Connected"`. Then wait until a `sendMessage` to `SMOKE_CHAT_2` contains `Jellyfin is up` (120 s from the restart).
  8. Sleep 15 s and assert **exactly one** such message to `SMOKE_CHAT_2`, and none to `SMOKE_CHAT` (NewMedia doesn't subscribe).
  9. The existing token-not-in-log check runs last, over the logs from both runs.

- [ ] **Step 4: Run the script tests, then push and watch CI**

Run: `python3 -m unittest discover -s tests/integration -p 'test_*.py' -v` (PASS), then `git push` (pushing a feature branch is pre-approved) and `gh run watch --exit-status <id>`.
Expected: all six jobs pass. On failure, read the job's container log tail, which `smoke.py` prints. Fix the cause, then push again.

- [ ] **Step 5: Commit**: `ci: end-to-end movie notification and server.started across a restart`

---

### Task 19 (owner-gated, optional before plan 4): Settle §15 Q8 (and Q9 if plan 2's Task 16 is still open)

Needs the owner's throwaway bot and a test chat. **Ask the owner first**; skip this task if they decline.

- [ ] **Step 1:** With the owner present, send a message from the bot, then edit it 25 h later (or as late as the owner allows). Record the result in spec §15 Q8: whether the edit succeeded, or the exact error text. If a limit shows up below 24 h, lower the `LateEditHours` maximum in `SettingsValidator` to match, and say so in the commit.
- [ ] **Step 2:** If plan 2's Task 16 is still open, record the Q9 texts the same way (an HTML parse error, and "can't initiate conversation"), and add them to Appendix A and the classifier fixtures.
- [ ] **Step 3: Commit**: `docs: answer spec 15 Q8 from a real bot`

---

## Self-check against the spec (done while writing this plan)

- **§7.1 catalog:** every key has a source:
  - Task 5: consumers for the auth, user and plugin events;
  - Task 6: `server.task_failed`, `server.restart_pending`, `server.started`, plus `ScanFinished`;
  - Task 16: `server.update_available`;
  - Tasks 3, 6 and 7: `media.*`.
- **§7.2 preset upgrades on load:** Task 8. **§7.3:** Tasks 7 (library resolution), 8 (filter) and 17 (removed-library flags, `GET Libraries`).
- **§7.4 pipeline:**
  - filter: Tasks 3 and 6;
  - pending, ready and timeouts: Task 7;
  - group keys, windows, buffer, digest, late edits: Task 13;
  - upgrades: Task 7;
  - `newSeries`: Tasks 6 and 7.
- **§7.5:** Task 14. **§7.6:** Task 16.
- **§8.1:** Tasks 11 and 12. **§8.2:** Tasks 9 and 11 (PhotosBlocked is plan 2's delivery). **§8.3:** Tasks 9, 11 and 17. **§8.4:** Tasks 11 and 12. **§8.6:** Tasks 9, 11 and 12. **§8.5:** plan 5, with the seam in Task 12.
- **§9.3:** render model and re-rendering in Task 10; `⏱` in Task 10. **§10.3 sections:** Tasks 7, 10, 13, 14 and 16.
- **§12:** intake queues in Task 5; graceful drain in Task 15. **§14.1:** each listed test group maps to Tasks 7–16. **§14.3** end to end: Task 18.
- **§15 Q8:** Task 19.
- **Plan 2's deferred minors:** M1, M6 and M7 in Task 1; M2, M3, M4, M5 and M8 in Task 2.

## Roadmap: plans 4–5 (unchanged, with what this plan hands over)

- **Plan 4 — Bot UX:** as planned. It also gets:
  - `NotificationRenderer`/`Formatter` for bot screens;
  - the `Events · Libraries` screen data from `GET Libraries`' source (`ILibraryAccess.GetVirtualFolders`);
  - the service-message supergroup migration in `UpdateRouter`.
- **Plan 5 — Dashboard, templates, release:** as planned. It also gets:
  - Scriban templates plugged into `NotificationRenderer`, using `RenderModel` to build the flat template model (`docs/templates.md` lists `RenderModel.Values` keys and media fields);
  - Status notices for `UpdateCheckFailing`, `RemovedLibraryFilters` and `publicUrlFlag`;
  - the BaseUrl smoke variant.
