# TgJelly v1: design spec

- **Status:** draft rev. 3 (2026-10-21, inline-only bot UX after v0.1 testing), waiting for owner review
- **Date:** 2026-10-16 (rev. 3: 2026-10-21)
- **Repo:** `github.com/MrSnowball-dev/tgjelly_plugin` (GPL-3.0)

TgJelly is a Jellyfin server plugin with a built-in Telegram bot. Version 1 is a **notifier**. It sends Jellyfin events (new media, security/account events, server/admin events) to Telegram chats, groups and channels that the server's admin chooses. Setup is easy to follow and can be done both from the Jellyfin dashboard and from inside the bot.

---

## 1. Intent

### 1.1 What the owner asked for
- The repo description reads: "A (better) Jellyfin plugin containing telegram bot interface to be notified about Jellyfin events."
- First and foremost a **notifier** with **simple, understandable setup**, configurable through **a GUI in the Jellyfin plugin settings AND in the bot itself** (inline buttons, persistent flows). Later features build on it.
- Only the **server admin** sets up destinations (chats/groups/channels). Regular Jellyfin users never deal with the bot.
- v1 event groups: **new media**, **security/accounts**, **server/admin**. The owner explicitly added **"server started"**.
- Must support **Jellyfin 10.11 and 12.x**. Released **publicly** through a plugin repository.
- **English only, but i18n-ready.**
- Messages: **polished default layouts with toggles, plus an optional hidden custom template for each event**. Templates use **Scriban**.
- Posters are sent **as-is**, without resizing.
- Setup corrections the owner gave during review:
  1. Start by asking whether to create a new bot or use an existing one, and accept a pasted token.
  2. No "connect your Telegram account" step. One personal bot is the normal case. When a new bot is created, point the owner to **restrict the bot to themselves (or whoever they choose) in @BotFather**. The owner has used this restriction on regular bots.
  3. Add destinations through **Telegram's own chat chooser, opened from the bot's inline buttons**. Manual setup appears only in Advanced settings, and only when the main flow fails.
- Access model: **trust the BotFather restriction**. Anyone who can open the bot's private chat can manage it. The owner rejected an invisible "first /start claims the bot" lock.
- The owner explicitly kept three behaviors that the review suggested changing:
  - late episodes are added to the earlier message by a silent edit,
  - preset order `[Everything] [New media] [Admin alerts] [Custom…]` for every chat type,
  - every new destination gets an automatic test message.
- Roadmap after v1: **interactive bot features** (search or browse the library, stats, trigger tasks) and **playback notifications**.
- **Not planned in any version:** per-user subscriptions and non-Telegram transports.

### 1.2 Assumptions (not stated by the owner)
- "(better)" means better than notifiers built on the Webhook plugin: no hand-written templates, no chat IDs, no extra service to run.
- Most installs sit behind NAT or in Docker without a public HTTPS endpoint. That is why the bot uses long polling and uploads posters instead of linking to them.

### 1.3 Success criteria
1. **From a fresh install to the first notification takes only a token paste plus a few taps:** choose new or existing bot → paste the token → Open bot → pick a chat in Telegram's own chat chooser, opened from the bot's inline buttons → pick a preset. No chat IDs, templates or config files.
2. The **same zip format and the same code** run on Jellyfin 10.11.x and 12.x. Each server automatically gets the build that matches it.
3. **The plugin never harms Jellyfin and never modifies the library.** No unhandled exception, slow handler or Telegram outage may break, block or stop the server.
4. **A scan that produces more than `DigestThreshold` (default 10) media groups for a destination results in one digest for that destination** (scans running longer than 2 h: one digest per 2 h). Smaller scans produce one message per group (a movie, a season batch, an album).
5. Telegram outages, chats the bot was removed from, missing permissions and rate limits **degrade gracefully**. Messages are held rather than lost while a destination recovers, and the plugin explains the problem in the dashboard and in the bot.

---

## 2. Scope

**In v1:** the event catalog in §7, the message layouts and templates in §8, the setup UX in §6, both builds with the release pipeline in §13.

**Planned later (the architecture leaves room for these, v1 does not implement them):** playback notifications (including a "now playing" message edited in place) and interactive bot features.

**Non-goals for v1:**
- a webhook receive mode,
- support for a self-hosted Bot API server (the base URL can only be overridden by an environment variable, for tests: §9.1),
- image resizing,
- languages other than English (only the structure for them),
- settings export and import,
- notifying failed logins for unknown usernames (Jellyfin publishes no event for them),
- user policy or permission changes (that event exists only on Jellyfin 12 and fires differently on 10.11).

**Not planned in any version (the architecture does not leave room for them):** per-user subscriptions, linking Jellyfin users to Telegram, transports other than Telegram.

---

## 3. Decision log

| # | Decision | Why |
|---|---|---|
| D1 | One C# plugin hosts everything: the bot (long polling), the event pipeline and the dashboard page | Owner's choice. A single install, no ports to open, one shared config |
| D2 | Telegram.Bot **22.10.3.3**, pinned exactly. Its API methods are called only from `ITelegramSender` and `BotSupervisor` | Well maintained (checked by the owner). This version or later counts length in Unicode code points |
| D3 | Two builds: net9.0 / ABI 10.11.0.0 / version 1.x, and net10.0 / ABI 12.0.0.0 / version 2.x | Jellyfin 12 moved to .NET 10, and its compatibility check only enforces an upper bound (F7) |
| D4 | Only the admin configures; the bot sends to chats, groups and channels | Owner's choice |
| D5 | **Trust the BotFather restriction**; no pairing and no claim lock | Owner's decision. The owner has used the restriction on regular bots. §6.4 |
| D6 | Destinations are added through inline `startchannel` / `startgroup` deep-link buttons, which open Telegram's chat chooser. The bot matches the resulting `my_chat_member` to the admin's flow. Manual methods are under Advanced and appear only after a failure | Owner's setup correction; rev. 3: inline buttons only, no reply keyboards (owner, 2026-10-21). `request_chat` exists only on reply keyboards |
| D7 | Default layouts with toggles, plus an optional Scriban template per event that replaces only the text or caption | Templates per event: owner's choice. Limiting them to the text: presented in design section 3 and approved |
| D8 | Posters uploaded as-is; text-only fallback | Owner's decision |
| D9 | The token and proxy credentials live in `secrets.json`, not in the Jellyfin config XML | The generic config GET returns the whole XML to admins, and a failed load overwrites it with defaults (F9) |
| D10 | The dashboard uses only TgJelly's own API endpoints (fine-grained updates with a revision check) | The generic POST replaces the whole config (F9) |
| D11 | Server updates are detected by polling GitHub releases, only while some destination subscribes to them | Jellyfin has no update check (F19). No outbound calls nobody needs |
| D12 | Our own `GetUpdates` loop instead of `ReceiveAsync`; the update offset is kept in memory only | `ReceiveAsync` reports nothing when a poll succeeds and can exit silently (F39). A saved offset goes stale after a bot switch or a week without updates (§9.2) |
| D13 | The plugin only reads from the library; `ConvertImageToLocal` is never called | That call writes files, raises ItemUpdated and by default deletes images on failure (F20) |
| D14 | `server.started` event | Owner's request |

---

## 4. Platform facts this design relies on

These facts were checked against Jellyfin **v10.11.11** and **v12.2** server sources, jellyfin-web at the same tags, jellyfin-plugin-webhook (main), Telegram.Bot (main, matching 22.10.3.3), Scriban (main, which carries 7.5.1's API), and the Telegram Bot API docs (Bot API 10.3, 2026-08-24). A separate verifier re-checked the load-bearing claims, and a second review pass checked this spec against them. Paths are relative to the jellyfin repo.

**Plugin hosting (identical in both versions unless noted)**
- F1. Plugins register `IHostedService`s from `IPluginServiceRegistrator.RegisterServices`, which runs before the host is built. `_jellyfinHost.StartAsync()` starts them.
- F2. Discovered types (registrator, plugin class, controllers, `ILibraryPostScanTask`) must be **public**, non-generic and concrete. The registrator needs a public parameterless constructor.
- F3. **Jellyfin sets no HostOptions,** so .NET's default `BackgroundServiceExceptionBehavior.StopHost` applies. An unhandled exception in a background service **stops the whole server**. A throw from `StartAsync` keeps Jellyfin from starting.
- F4. Restarts happen **in-process**. A new host, a new plugin instance and a new collectible `PluginLoadContext` are created, and the old context is only unloaded cooperatively. On **10.11**, an exception from any `StopAsync` cancels a pending restart; 12 guards against this.
- F5. Each plugin folder gets its own `PluginLoadContext`. If `meta.json` has a non-empty `assemblies` list, **only those DLLs** are loaded. If its `imagePath` is non-empty, it overrides the image downloaded from the repository's `imageUrl`.
- F6. `IEventConsumer<T>` implementations are **not** found by scanning; they must be registered in DI. Events are published **inline**, inside the login or install code path. The DI services are resolved **outside** EventManager's try/catch, so a consumer constructor that throws breaks the publisher.
- F7. Compatibility checks only `targetAbi <= server version`; there is no lower bound. A 12.x server picks a 10.11 build if its version number is higher. The official repo uses version ranges that don't overlap.
- F8. On a 10.11→12 upgrade the server **still loads the old 10.11 build** until the plugin update task replaces it.
- F9. Plugin config: `BasePlugin<T>` uses `XmlSerializer` (no `Dictionary<,>`, no interface-typed members; pre-filled lists get deserialized items appended). The file is `plugins/configurations/{AssemblyName}.xml`. `LoadConfiguration` **swallows load errors and saves a fresh default config over the file.** `POST Plugins/{id}/Configuration` **replaces the whole object**. `UpdateConfiguration` is virtual.
- F10. `BasePlugin.DataFolderPath` points at `plugins/{AssemblyName}`, so it is not used.
- F11. Plugin ASP.NET controllers are discovered automatically. The admin policy is `Policies.RequiresElevation` (`MediaBrowser.Common/Api/Policies.cs`) in both versions.

**Events**
- F12. `ILibraryManager.ItemAdded` fires **before any metadata provider runs or any image is downloaded**. It also fires for:
  - containers: Series, Season, MusicAlbum, folders,
  - `IItemByName` entities. Persons are upserted on every refresh, so they fire again and again,
  - on 12 also TMDB **virtual** episodes and seasons, and stacked parts.
- F13. Metadata is ready when `ItemUpdated` arrives with `item.DateLastRefreshed > DateTime.MinValue`. That date stays `MinValue` if a provider failed. A first refresh raises `ItemUpdated` at least twice.
- F14. `ILibraryManager.IsScanRunning` is true during **any** full library validation: the scheduled "Scan Media Library" task, `POST /Library/Refresh`, and adding a library on 10.11. A refresh of a single library doesn't set it. Every public `ILibraryPostScanTask` is run at the end of each full validation, whoever started it. `ITaskManager.TaskCompleted` with key `RefreshLibrary` fires only for the scheduled-task path.
- F15. Failed logins (`AuthenticationRequestEventArgs`) are published **only** when an existing user enters a wrong password. Unknown usernames and disabled or locked accounts throw before the event is published. The payload has Username (as typed), App, DeviceName, DeviceId and RemoteEndPoint. Jellyfin matches usernames case-insensitively (`NormalizedUsername = ToUpperInvariant()`).
- F16. `UserLockedOutEventArgs` (a `GenericEventArgs<User>` without IP) is published **inside** `AuthenticateUser`, **before** the `AuthenticationRequestEventArgs` of the same attempt. `User.LoginAttemptsBeforeLockout` is an `int?`, null by default (never lock), so many accounts are never locked out at all.
- F17. Successful logins (`AuthenticationResultEventArgs`) carry the user and the session (client, device, IP). Created, deleted and password-changed users fire their own events. Creating a user with a password fires Created **and then** PasswordChanged.
- F18. Plugin events: `PluginInstalled`, `PluginUpdated` (with a changelog), `PluginUninstalled`, `InstallationFailed`. `TaskCompletionEventArgs` and `PendingRestartEventArgs` are **never published** through IEventManager. Use the C# events `ITaskManager.TaskCompleted` and `IServerApplicationHost.HasPendingRestartChanged`.
- F19. Jellyfin has **no server update check**. Release tags look like `v10.11.11` and `v12.2`. `IServerApplicationHost.CoreStartupHasCompleted` becomes true once startup has finished.
- F20. Images: `BaseItem.GetImageInfo(ImageType.Primary, 0)` → `ItemImageInfo.Path` / `IsLocalFile`. After a refresh, the kinds we announce normally have local images. `ILibraryManager.ConvertImageToLocal` saves into the library, raises ItemUpdated, has no timeout and by default deletes the image on failure, so it is **not used** (D13).
- F21. `AudioBook` derives from `Audio`. Multi-file audiobooks resolve to **one AudioBook item per file**.
- F22. `ILibraryManager.ItemRemoved` and `InternalItemsQuery.HasAnyProviderId` exist in both versions.
- F23. There is no canonical public server URL, so the admin has to enter it. The web deep link is `{origin}{BaseUrl}/web/#/details?id={id:N}&serverId={SystemId}` in both versions.

**Web client (identical in both versions)**
- F24. 10.11 and 12 have **one** MUI dashboard, which hosts classic plugin pages through the legacy view container. That code path is byte-identical between the two versions, and nothing newer replaces it.
- F25. The page HTML must contain the literal `data-role="page"` (double quotes). `data-controller="__plugin/<name>.js"` loads an ES module whose default export is constructed with `(view, params)`.
- F26. `globalize.translateHtml` rewrites **every** `${...}` in the page HTML. JS loaded through `data-controller` is not touched.
- F27. Custom elements registered at startup: `emby-input`, `emby-checkbox`, `emby-button`, `emby-linkbutton`. `emby-select` is registered only indirectly, so treat it as best-effort. `emby-textarea`, `emby-collapse` and `emby-toggle` are not guaranteed.
- F28. Up to 3 cached views stay in the DOM; returning to a cached view fires only the show events (`viewshow`/`pageshow`, `viewbeforehide`, `viewhide`, `viewdestroy`).
- F29. `GET /web/ConfigurationPage` is **anonymous**, so pages must never embed secrets. 12 disables legacy auth headers, so pages must use `ApiClient.fetch`/`ajax` with `ApiClient.getUrl(...)`, which also handles BaseUrl.
- F30. `MenuIcon` is shown only on 12, and `MenuSection` is ignored. 10.11 needs the class `type-interior` to get the dashboard theme. The plugin catalog has no "Notifications" category; Webhook uses `Administration`.

**Telegram / Telegram.Bot**
- F31. `my_chat_member` is delivered by default. Always send an explicit `allowed_updates`. Updates are kept for **24 h**. In private chats, `my_chat_member` arrives only when the user blocks or unblocks the bot.
- F32. Three kinds of 409: a webhook is active, "terminated by other getUpdates request", and "terminated by setWebhook request".
- F33. (No longer used since rev. 3.) `KeyboardButtonRequestChat` works only in private chats with the bot. It can add the bot to the chosen chat with the requested admin rights (`bot_administrator_rights`, plus `user_administrator_rights` that must be a superset; `bot_is_member` for groups). The bot then receives a `chat_shared` message: request_id, chat_id, plus title if requested. **Chat type and `is_forum` need `getChat`.**
- F34. A message that has (or ever had) a non-inline reply_markup, including `ReplyKeyboardRemove`, **cannot be edited**. A message carries exactly one reply_markup.
- F35. The Bot API has no method to list forum topics. `createForumTopic` needs an admin with `can_manage_topics`. A topic's link (`t.me/c/<id>/<topic>` or `t.me/<username>/<topic>`) carries its thread ID.
- F36. Limits: text 4096 and caption 1024 **code points after entity parsing**, photo upload ≤ 10 MB, `sendMediaGroup` has **no** reply_markup. Rate guidance: about 1 msg/s per chat, 20 msg/min per group, about 30 msg/s overall. Groups may block members from sending media ("not enough rights to send photos to the chat").
- F37. A URL button whose host has no dot (`localhost`, a Docker service name) **makes the whole send fail**.
- F38. TDLib puts no edit time limit on a bot's own outgoing messages. The server side wasn't verified (§15).
- F39. Telegram.Bot:
  - `ReceiveAsync` reports nothing when a poll succeeds, has no backoff, and returns silently if an `OperationCanceledException` escapes.
  - It retries 429 inside every call unless `RetryThreshold = 0`.
  - `TelegramBotClientOptions` switches to local-server mode for **any non-null** base URL (`""` throws). Its constructor throws `ArgumentException` with `ParamName` `"token"` or `"baseUrl"`.
  - The token is part of every request URL, so it must never be logged.
- F40. Access settings: Bot API 10.0 (2026-05-08) added `BotAccessSettings` (only selected users plus the owner can access the bot; up to 10 users). The API docs describe it only for managed bots. **The owner confirms that BotFather offers it for ordinary bots.** A bot cannot read its own access settings or find out who its owner is.
- F41. Scriban ≥ 7.2.6 fixes the 2026 advisories. It is not a security boundary for reflected objects, so templates only get plain data. Relevant `TemplateContext` members: `LoopLimit`, `RecursiveLimit`, `ObjectRecursionLimit`, `LimitToString`, `OutputLimit` (int?, UTF-16 chars), `OnOutputLimit`/`OnStringLimit` (`ScriptLimitBehavior.Truncate|Throw`), `RegexTimeOut`, `StrictVariables`, `EnableRelaxedMemberAccess`, `CancellationToken`, and `virtual Write(SourceSpan, object?)`.
- F42. `https://t.me/<bot>?startgroup=<payload>` and `?startchannel&admin=<rights>` open the client's chat chooser and add the bot, with the rights for a channel. The bot receives `my_chat_member` with `from` = the user who added it. `admin=` on `startgroup` asks for those rights for the bot (`manage_topics` for topic promotion).
- F43 (§15 Q10, confirmed by the owner 2026-10-21). After a `startgroup` add, the client sends `/start@<bot> <payload>` into the group from that user, also when the bot is already a member.

---

## 5. Architecture

### 5.1 Identity (fixed forever)
| Item | Value |
|---|---|
| Plugin name | `TgJelly` |
| GUID | `e547b901-0dc4-42ae-8999-3871dd141270` |
| Assembly / DLL | `Jellyfin.Plugin.TgJelly` / `Jellyfin.Plugin.TgJelly.dll` |
| Config XML | `plugins/configurations/Jellyfin.Plugin.TgJelly.xml` (name comes from the DLL name, F9) |
| Data folder | `plugins/configurations/TgJelly/` (`secrets.json`, `state.json`, `queue.json`, `settings.bak.json`, `state.lock`) |
| Dashboard pages | `tgjelly` (HTML), `tgjelly.js` (controller) |
| API route prefix | `/TgJelly` |

Changing the name, GUID or DLL name after the first release would lose every user's configuration.

### 5.2 Data flow
```
Jellyfin ─► Intake ─► [media queue | other queue] ─► MediaReadiness ─► Router ─► Aggregator ─► Renderer ─► Outbox ─► Telegram
                                                                                                   ▲
Dashboard page ─► TgJelly API ─┐                                                                   │
                               ├─► SettingsStore ◄── Bot (GetUpdates loop, flows, screens) ◄───────┘
Telegram (private chats) ──────┘        │
                              StateStore (state.json, queue.json) · SecretsStore (secrets.json)
```

### 5.3 Units
| Unit | Responsibility | Uses Jellyfin APIs? |
|---|---|---|
| `Plugin` | `BasePlugin<PluginConfiguration>`, `IHasWebPages`; overrides `UpdateConfiguration` (§10.4) | yes |
| `PluginServiceRegistrator` | Registers every service, consumer and hosted service explicitly (F6) | — |
| `CompatGuard` | Compares the major version this build targets (10 or 12) with `ApplicationVersion.Major`. On a mismatch, intake, the bot and the outbox stay off and Status reports it (F8) | read-only |
| **Intake consumers** | `IEventConsumer<T>` for auth, user and plugin events. Each maps its event to an immutable `NotificationEvent` and writes it to the **other** queue | — |
| **Intake watchers** | Hosted services that subscribe to C# events in `ExecuteAsync` and unsubscribe in `StopAsync`: `LibraryWatcher` (ItemAdded, ItemUpdated, ItemRemoved), `TaskWatcher` (TaskCompleted), `RestartWatcher` (HasPendingRestartChanged), `StartupWatcher` (polls `CoreStartupHasCompleted` every 1 s, since that property has no change event, and emits `server.started` once). LibraryWatcher also provides `IScanState` (backed by `ILibraryManager.IsScanRunning`) | read-only |
| `ScanFinishedTask` | A public `ILibraryPostScanTask`. It only writes a `ScanFinished` event to the media queue (F14) | — |
| `UpdateChecker` | A BackgroundService that polls GitHub (§7.6). Not an event callback | read-only (`ApplicationVersion`) |
| **MediaReadiness** | Item filter, pending set, readiness, timeouts, upgrade detection, and library resolution (puts the library IDs and names on each `MediaReady` event). Owns the **pending-ID set** that LibraryWatcher reads | read-only |
| **Router** | For each event, finds the destinations whose rules match: event key, library filter, `Enabled` | — (pure) |
| **Aggregator** | Group batching, the per-destination release buffer with digests, late-arrival edits, security coalescing (§7.4, §7.5). Reads scan state only through the injected `IScanState` | — (pure, uses `TimeProvider` and `IScanState`, both faked in tests) |
| **Renderer** | Default layouts, toggles, Scriban templates, Telegram-HTML escaping, validation and truncation, `PosterPicker`, `LinkBuilder` | read-only (items, images) |
| **Outbox** | Send queues per destination, priority, pacing, retries, the error classifier, fallbacks, destination health and re-checks, edits, file_id cache | — |
| `ITelegramSender` | A thin wrapper over Telegram.Bot that calls methods with **named arguments**. Apart from BotSupervisor's GetUpdates loop, the only place that calls Telegram.Bot API methods (send, edit, get*) | — |
| **Bot** | `BotSupervisor` (client lifecycle and the GetUpdates loop, §9.2), `UpdateRouter`, `FlowEngine`, screens (§6.3) | read-only (`CoreStartupHasCompleted`, virtual folders) |
| **SettingsStore** | The only way to change settings (§10.4). Raises `SettingsChanged` | yes (`UpdateConfiguration`) |
| **SecretsStore** | Atomic read and write of `secrets.json` (mode 600 on Unix) | — |
| **StateStore** | `state.json` + `queue.json` (§10.3) | — |
| **TgJellyController** | Admin-only REST API (§11.2) | read-only (virtual folders, network config) |
| **Web** | `tgjelly.html` + `tgjelly.js` ES module (§11.1) | — |
| **Strings** | `Strings/en.json` plus the `Formatter` (§8.6) | — |

### 5.4 Rules that apply everywhere
1. Jellyfin APIs are used only by the units marked "yes" or "read-only" above. **Nothing writes to the library or items** (D13). Router and Aggregator are pure and are tested with a fake clock. Only `ITelegramSender` (send, edit and get* calls) and `BotSupervisor` (the GetUpdates loop) call Telegram.Bot API methods. Telegram.Bot model types (`Update`, `ApiRequestException`) and the `HtmlText` helpers may be used by UpdateRouter, FlowEngine, the classifier and the Renderer.
2. Every background service returns from `StartAsync` at once, wraps all work in a top-level catch-all with a supervised restart and backoff (1 s → 60 s), honors its stopping token, and **never throws from `StopAsync`** (F3, F4).
3. **`IEventConsumer<T>` constructors** take only the queue writer and `ILogger<T>` (F6). **Watchers** may also take Jellyfin singletons that always resolve (`ILibraryManager`, `ITaskManager`, `IServerApplicationHost`) and the pending-ID set. **Event callbacks** are O(1), never throw, and never do I/O.
4. No static mutable state; a restart creates fresh instances (F4).
5. Nothing is sent before `CoreStartupHasCompleted` is true.
6. The places where 10.11 and 12 differ stay behind `#if JF12` in one `Compat` file (e.g. `Video.PrimaryVersionId`: `string` on 10.11, `Guid?` on 12).

### 5.5 How the roadmap fits
- **Playback:** a new watcher (`ISessionManager` playback events, identical in both versions) plus layouts. The Outbox already edits sent messages.
- **Interactive features:** new commands and screens on `UpdateRouter`/`FlowEngine`. Long-running actions go to the bot work queue so the GetUpdates loop is never blocked.

---

## 6. Setup and configuration UX

### 6.1 Dashboard, first run
A single card: **"Connect a Telegram bot"** with `[Create a new bot]` `[I already have a bot]`.

- **Create a new bot:** numbered steps, each with a link:
  1. Open **@BotFather** (`https://t.me/BotFather`) and send `/newbot`. Choose a display name and a username ending in `bot`.
  2. In BotFather's settings for the new bot, **restrict access to yourself** and to anyone else who should manage Jellyfin (up to 10 people) (F40). *The exact menu labels are recorded during implementation (§15).*
  3. Copy the token BotFather gives you and paste it below.
- **I already have a bot:** a token field, with two notes:
  - "Restrict access in BotFather if you haven't, because anyone who can open the bot can manage notifications."
  - "Don't use this bot in another program at the same time."

  The page also shows a checkbox, **on** by default: "Let TgJelly set up this bot's profile: description, command menu and default admin rights (replaces the current ones)". When it is off, TgJelly changes nothing about the bot's profile. The commands still work when typed; they just don't appear in Telegram's command menu.
- **Save** → `POST /TgJelly/Token`:
  1. **Format check.** Build the client. An `ArgumentException` with `ParamName == "token"` means "This doesn't look like a bot token" (F39).
  2. **`getMe`.** A 401 means "Telegram rejected this token".
  3. **Same bot or a different one?** If a bot ID is stored and `getMe.id` differs from it, the §10.4 different-bot reset **always** runs. If destinations exist, the API first replies `needsConfirm` with this text: "@new is a different bot. It isn't in your N destinations, so they will need to be added again." `[Switch bot] [Cancel]`. The reset then runs only after confirmation.
  4. **Webhook.** `getWebhookInfo`; if a URL is set, `deleteWebhook(drop_pending_updates=false)` and show a warning. This is the only automatic webhook deletion (§9.2).
  5. **Store** the token in `secrets.json` and start the supervisor.
  6. **Profile calls.** Only if the checkbox is on, which it always is for new bots. They run best-effort, in the background:
     - `setMyCommands` for the all-private-chats scope (`/menu`, `/status`, `/test`, `/help`), plus an empty default command scope,
     - `setMyDescription` ("Jellyfin notifications from *server name*") and `setMyShortDescription`,
     - `setMyDefaultAdministratorRights`: for channels post + edit messages.

     The choice and any calls still pending are stored in `state.telegram.profile` and retried when the supervisor next starts, until each one succeeds. Failures are shown in Status and never fail the save.
- Result: the connect card closes. The Bot section (§6.6) shows @MyJellyBot with **[Open in Telegram]** (`https://t.me/<username>?start=setup`).
- If `getMe` fails with a network error, a **"Can't reach Telegram?"** panel opens with the proxy settings (§9.1). Otherwise proxy settings stay under Advanced.

### 6.2 Bot onboarding: the main flow, inline buttons only
Triggered by `/start setup`, `/start add`, a plain `/start` when there are no destinations yet, or **Add destination** in `/menu`.

1. **Prompt.** The prompt is one inline wizard message: "Where should notifications go?" plus the hint. Its buttons:
   - `Add to a channel` (URL, `BotLinks.StartChannel`: `https://t.me/<bot>?startchannel&admin=post_messages+edit_messages`),
   - `Add to a group` (URL, `BotLinks.StartGroup`: `https://t.me/<bot>?startgroup=add`),
   - `Right here` (callback): the destination is this private chat,
   - up to 3 `[Set up <Title>]` rows for detected chats that are not set up yet (§6.5),
   - `Cancel` (callback).

   Both URL buttons open Telegram's own chat chooser (F42). The prompt is sent as a new message for `/start`. It edits the pressed message for Add destination, Add another place and Try again. The flow stores `wizardMessageId`, `promptSentAt` and `await=chat`.
2. **Check.**
   - **Trigger.** A `my_chat_member` whose `from.id` has a live prompt, and whose change counts as an add. A change counts as an add when the new status is member, administrator, or restricted with `is_member`, **and** either the bot was left or kicked before (a fresh add), or the new status is administrator (rights granted in a chat the bot was already in, e.g. `startchannel` on a channel it is in). A group `/start@bot` from that user (F43) counts the same. A matched add is not recorded as a detected chat.
   - The wizard message is edited to "Checking *Title*…", and the check runs:
     1. Call `getChat` (type, `is_forum`, title, `permissions`) and `getChatMember(chat, botId)` at t = 0, 1.5, 3 and 5 s, stopping at the first pass. `my_chat_member` may arrive late, so a few tries are allowed.

   Passing means:
   - **Channel:** `administrator` with `can_post_messages`.
   - **Group:** `member`, `administrator`, or `restricted` with `is_member = true` and `can_send_messages`.
     - If photos are not allowed for the bot (`restricted`: `can_send_photos=false`; `member`: `getChat.permissions.can_send_photos=false`), set the destination flag **PhotosBlocked**: posters are skipped and text is sent. The Done message says so.

   **Duplicate:** for a non-forum chat, if that chat is already a destination, the bot says "*Title* is already set up", runs a re-check, and opens that destination's screen. Nothing new is created. For forums, the same rule is applied to (chat, topic) in step 3, once the topic is known.

   **On failure:** the wizard message is edited to an explanation of what's missing, with `[Try again] [Advanced options]` (§6.5), and a setup failure is recorded (§6.5). `[Try again]` re-checks the same chat once (no retries), in place, and continues; a failed check keeps its chat in the spent flow for this. It offers a fresh prompt only when there is no chat.
3. **Forum supergroup** (`is_forum`). The wizard message is edited to offer `[General topic] [Create a "Jellyfin" topic] [Use an existing topic…]`. The duplicate rule is checked for the chosen (chat, topic) before anything is created.
   - **Create, when the bot already has `can_manage_topics`:** `createForumTopic("Jellyfin")`, and the destination uses the returned thread ID.
   - **Create, when it doesn't:** the wizard is edited to "To create a topic, I need permission to manage topics…" with these buttons:
     - `Allow topic creation` (URL `https://t.me/<bot>?startgroup=topics&admin=manage_topics`),
     - `[Check again]`,
     - `[General topic]`,
     - `[Advanced options]`.

     **Promotion detected:** a `my_chat_member` → administrator in that forum from the same user, a group `/start@bot` there, or `[Check again]` finding `can_manage_topics`. The bot then creates the topic. **Promotion in another group:** said on the wizard, and the bot keeps waiting. **Rights still missing on Check again:** said on the wizard, and the bot keeps waiting. This screen has no expiry. If the creation fails, the bot falls back to `[General topic]` plus `[Advanced options]`.
   - **Use an existing topic…**: Telegram has no topic picker (F35), so this is the one place where the main flow hands over to the manual path. The bot records a setup failure of kind `topic_not_pickable` (§6.5) and asks for the topic's link (§6.5, "Use an existing topic").
   - **General** is stored as `ThreadId = 0` (no `message_thread_id`). This is provisional until §15 Q4 is answered.
4. **Preset.** "What should I send there?" `[Everything] [New media] [Admin alerts] [Custom…]` (§7.2). *Custom…* opens the event switches with nothing selected.
5. **Done.** The bot sends a test message to the new destination ("✅ TgJelly test: this chat will receive *groups*"). It then edits the wizard message to "✅ Done: *Title* will receive *groups*. `[Add another place] [Menu]`".

The whole flow is **one inline wizard message** that gets edited. There are no reply keyboards and no throwaway prompts.

**Stale or unexpected input in a private chat:**
- A plain `/start` when destinations exist, an unknown `/start` payload, or unrecognised text or a command → the `/menu` screen headed "TgJelly is set up: N destinations".
- A v0.1 reply-keyboard text or a `chat_shared` → "That menu expired" with `ReplyKeyboardRemove` (the only non-inline markup the bot still sends, to clear a keyboard the client still shows), then the menu.
- A prompt unanswered for 10 min → edited to "That setup prompt expired." `[Add destination]`. It counts as a setup failure (§6.5).

### 6.3 Bot menus (`/menu`)
```
🏠 Menu
├─ 📬 Destinations (n) ─► <destination> ─► Events (Media · Security · Server, each its own screen) · Libraries · Silent groups · Send test · Check again ·
│                       │                  Pause/Resume · Remove (asks "Also make the bot leave the chat?")
│                       └─ Chats added outside setup (n)   ← shown only when n > 0 (§6.5)
├─ ➕ Add destination ─► §6.2
├─ ⚙️ Settings ─► Message style (§8.4) · Update checks (on/off, mode) ·
│                Public URL (shows current/suggested; [Use suggested] [Type another] (inline prompt with [Cancel], validated §8.3) [Clear])
└─ 🩺 Status ─► connection, compat, destination health, pending media, outbox sizes, last errors
   (Advanced options: reachable only from a failed setup step, §6.5)
```
- Long lists of related switches are grouped into submenus (Events); long lists of items page (destinations, libraries, detected chats).
- **Icons.** Action buttons and screen headings show custom emoji from the `TgAndroidIcons` pack: `icon_custom_emoji_id` on buttons, and `<tg-emoji>` with the old emoji as fallback in headings.
  - Their labels carry no plain emoji.
  - State marks stay plain emoji.
  - Custom emoji need Telegram Premium on the bot owner's account; without it they don't show.
  - If Telegram rejects a message that has them with any 400 error (Telegram's wording varies) other than "message is not modified" or a gone edit target, the bot resends it once without icons. If that resend goes through, the bot stops using icons until it reconnects; if it fails too, its error stands and the icons stay on.
  - Notification posts get none.
- **Every button is explained.** Every bot screen explains each of its buttons in its text, one line per button, `Label — what it does`. Lists of items or switches get one shared line saying what tapping an item does. Back, Prev and Next need no line.
- `/status` and `/test` (pick a destination) are shortcuts. `/help` gives a short description and the dashboard path.
- **Dashboard only:** entering and changing the token, proxy settings, batching values, templates, adding a chat by ID or topic link, diagnostics, disconnect, `[Take over]` for a webhook conflict.

### 6.4 Access model
- **Anyone who can open the bot's private chat can manage it** (D5). Telegram enforces the BotFather restriction (F40). The plugin can't check it, so:
  - both setup paths push the restriction (§6.1),
  - the dashboard's Bot section always shows: "Anyone who can open this bot can manage notifications. Keep it restricted in BotFather."
- **In groups and channels the bot ignores every command and message.** It only acts on `my_chat_member`, service messages it needs (e.g. supergroup migration), and its own sends. The one exception is `/start@<bot> …` from a user (F43), used for correlation only (§6.2 step 2). The bot never replies in the group.
- **"Managers"**: the private chats that used `/menu` or the onboarding in the last 90 days. They are used only to tell people about destination problems (§9.4).

### 6.5 Fallbacks (Advanced; shown only after the main flow fails)
**When they appear.** The manual options appear only when one of these holds (`state.setupFailures` keeps the last 7 days):
- a chat check failed (§6.2 step 2),
- a prompt expired unanswered,
- a topic promotion or creation failed, or the admin chose `Use an existing topic…` (§6.2 step 3),
- some destination is unhealthy,
- a detected chat exists.

At that point the dashboard's Destinations section shows "Telegram setup didn't work? **Set up manually**", which opens the manual block under Advanced. The bot shows `[Advanced options]` on its failure messages. The gate only controls what the two UIs show; the API accepts manual adds from admins at any time (§11.2).

**Bot "Advanced options" screen:**
- `Use an existing topic`: asks with an inline prompt with `[Cancel]`, in the private chat, for the topic's link (long-press the topic → Copy link). It checks that the link's chat is the picked chat, then makes a test send to that `message_thread_id`,
- `Open dashboard`: the dashboard path, for add by ID.

**Dashboard manual block:** "Add chat by ID": a chat ID, or a topic link, plus a preset. It is checked with `getChat`/`getChatMember` before saving, and the same duplicate rule as §6.2 applies.

**Detected chats.**
- **When one is recorded:** a `my_chat_member` update (left/kicked → member/administrator) for a **group, supergroup or channel** that isn't a destination, except when:
  - the add matches a setup flow of `my_chat_member.from.id` (§6.2 step 2), or
  - the chat becomes a destination within 30 s.
- **Where it shows:** under "Chats added outside setup (n)" in the bot and in the dashboard's manual block, labelled "Detected: *Title*, set it up?" `[Set up] [Dismiss]`. *Set up* runs the §6.2 step 2 check (without retries), then continues at step 3/4.
- **When it is removed:** when the chat becomes a destination, is dismissed, or `my_chat_member` reports left or kicked.
- A detected chat receives nothing.

### 6.6 Dashboard page after setup
Sections, top to bottom:
1. **Status:** connection state (Connected / Token rejected / Another app is polling this bot / Another app set a webhook `[Take over]` / Telegram unreachable, check proxy / Wrong build for this server / Waiting for the previous instance to stop), last errors, counts (pending media, outbox, dropped), notices (backup restored, template flagged, public URL flagged, update check failing, removed libraries in a filter), each with `[Dismiss]`.
2. **Bot:** @username (shown only here), the access reminder (§6.4), `[Open in Telegram]`, `[Change token]`, `[Disconnect bot]` (confirm → §10.4).
3. **Destinations:** a list with health badges (OK / Photos blocked / Needs permissions / Topic missing / Lost) and a **Pause/Resume** switch (`Enabled`). The editor covers event switches with the preset, a library multi-select, and silent groups, plus `[Send test]` `[Check again]` `[Remove]` (with a "Bot leaves the chat" checkbox → `leaveChat`). `[Add destination]` opens `https://t.me/<bot>?start=add`. The "Set up manually" link appears per §6.5.
4. **Messages:** public URL (pre-filled with a suggestion marked "verify", §8.3) and the toggles in §8.4.
5. **Message templates:** its own section after Messages, no longer in Advanced (amended by the [templates design](2026-10-21-tgjelly-templates-design.md)). One editor per template key, starting from the built-in template, with a live Telegram preview, `[Send test]`, `[Reset]` and the guide link under the template (§8.5).
6. **Advanced** (`<details>`): proxy (§9.1), batching values (§7.4, with the ranges in §10.1), update checks (on/off, mode), the manual block (§6.5, only when it is unlocked), `[Copy diagnostics]`.

Everything in §6.3 not marked dashboard-only can be done in both the dashboard and the bot.

---

## 7. Events

### 7.1 Catalog (event keys are stable strings; config stores concrete keys only)
| Key | Source | Notes |
|---|---|---|
| `media.movie` | ItemAdded → readiness | `Movie` |
| `media.episodes` | ItemAdded → readiness | `Episode`, batched by (series, season) |
| `media.album` | ItemAdded → readiness | Tracked by **album** (§7.4). Audio without an album is not announced |
| `media.musicvideo` | ItemAdded → readiness | `MusicVideo` |
| `media.book` | ItemAdded → readiness | `Book` (one per item); `AudioBook` grouped by parent folder (F21) |
| `security.login_failed` | `IEventConsumer<AuthenticationRequestEventArgs>` | Wrong password for an existing user (F15). The label says exactly that |
| `security.lockout` | `IEventConsumer<UserLockedOutEventArgs>` | §7.5 |
| `security.login_succeeded` | `IEventConsumer<AuthenticationResultEventArgs>` | Deduplicated per (user, device) for 12 h |
| `security.user_created` | `IEventConsumer<UserCreatedEventArgs>` | Merged with a PasswordChanged that follows within 10 s |
| `security.user_deleted` | `IEventConsumer<UserDeletedEventArgs>` | |
| `security.password_changed` | `IEventConsumer<UserPasswordChangedEventArgs>` | Also fires for resets |
| `server.started` | `StartupWatcher` | Once per plugin start, after `CoreStartupHasCompleted`. That includes in-process restarts and first install. Not sent while CompatGuard reports a mismatch |
| `server.update_available` | `UpdateChecker` | §7.6 |
| `server.plugin_installed` | `IEventConsumer<PluginInstalledEventArgs>` | |
| `server.plugin_updated` | `IEventConsumer<PluginUpdatedEventArgs>` | With changelog |
| `server.plugin_uninstalled` | `IEventConsumer<PluginUninstalledEventArgs>` | |
| `server.plugin_install_failed` | `IEventConsumer<InstallationFailedEventArgs>` | With the error message |
| `server.task_failed` | `ITaskManager.TaskCompleted` | Only `Result.Status == Failed` (F18) |
| `server.restart_pending` | `HasPendingRestartChanged` → true | Reasons: the plugin events from the previous 10 min |

Groups: `media.*` = **media**, `security.*` = **security**, `server.*` = **server**.

### 7.2 Presets
| Preset | Keys |
|---|---|
| Everything | all keys |
| New media | all `media.*` keys |
| Admin alerts | all `security.*` keys except `security.login_succeeded`, plus all `server.*` keys |
| Custom | individual switches |

- `Destination.Preset` stores the choice, and `Destination.Events` stores the expanded concrete keys. The Router matches by exact string equality.
- When `Preset ≠ Custom`, keys that a later version adds to that preset are added to `Events` when the config loads.
- Switching any event on or off makes the destination `Custom`.
- Destinations created from Advanced use the preset picked in that form (default `Everything`).

### 7.3 Library filter
- Each destination has `LibraryIds` (Guid strings, `N` format; empty means all).
- When an item becomes ready, MediaReadiness resolves `ILibraryManager.GetCollectionFolders(item)` into a set of Guids plus library names and puts them on the `MediaReady` event. The Router compares that set with the parsed `LibraryIds`.
- The choices offered come from `ILibraryManager.GetVirtualFolders()` (`VirtualFolderInfo.ItemId`).
- **Removed libraries:** IDs that `GetVirtualFolders()` no longer returns are kept but match nothing. The list is never pruned to empty, because empty means all. If **none** of a destination's libraries exist any more, Status, the dashboard editor and the bot show "Library filter refers to removed libraries", and the editor lists them as "(removed library)" with a remove action.
- The filter applies to `media.*` only.

### 7.4 New-media pipeline
1. **Filter** (in LibraryWatcher's ItemAdded callback, O(1)).
   - Type checks are exact, most-derived first: `AudioBook` is tested before `Audio`.
   - Keep `Movie`, `Episode`, `MusicAlbum`, `Audio`, `MusicVideo`, `Book`, `AudioBook`.
   - Drop the item if it:
     - has `IsVirtualItem`,
     - has a non-empty `OwnerId` or a non-null `ExtraType`,
     - is a Video with a primary version set (an alternate version; via `Compat`),
     - is an `IItemByName`, or
     - is anything else (F12).
   - **Music is tracked per album.** An `Audio` adds its album ID (`AlbumEntity`'s ID) to pending, unless that album is already pending. A `MusicAlbum` adds itself. The track count is read from the album's children when the message is rendered.
   - **Context only:** a `Series` add records `newSeries[seriesId] = now` (kept 24 h). A batch whose series is in that list is labelled "🆕 New series".
   - **`ItemUpdated`** is forwarded only if the item ID (or, for Audio, its album ID) is in the pending-ID set **and** `DateLastRefreshed > MinValue`.
   - **`ItemRemoved`** for a kept type records the item's identity set (step 8) in `removedIdentities` (48 h).
2. **Pending.** IDs go into `pendingMedia` with `firstSeen` (`queue.json`).
3. **Ready.**
   - An item is ready on a forwarded `ItemUpdated` (F13), or after `ReadyTimeoutMinutes` (default **15**) since `firstSeen`. A timed-out item is rendered with whatever metadata it has.
   - Items that disappeared (`GetItemById` returns null) are dropped quietly, without affecting the others.
   - At readiness, MediaReadiness resolves the libraries (§7.3) and runs the upgrade check (step 8).
4. **Group keys** (per destination):
   - movie, music video and `Book`: the item itself,
   - episodes: (series, season),
   - albums: the album,
   - `AudioBook`: its parent folder, labelled with the folder name, so one book counts as one group.
5. **Group windows.**
   - A group **closes** when no new item has joined it for `QuietWindowSeconds` (default **120**), or `MaxHoldMinutes` (default **30**) after its first item.
   - A `ScanFinished` event (or the fallback `TaskCompleted("RefreshLibrary")`) closes every open group immediately.
6. **Release buffer and digest.**
   - A closed group moves into its destination's **release buffer** instead of being sent.
   - The buffer is released when:
     - no group has entered it for `ReleaseQuietSeconds` (default **20**) **and** `ILibraryManager.IsScanRunning` is false, or
     - a `ScanFinished` event arrives, or
     - `MaxHoldMinutes` have passed since the first group entered it while no scan is running. While a scan runs, the buffer waits for `ScanFinished`, up to 120 min after its first group.
   - **On release:**
     - more than `DigestThreshold` (default **10**) groups → **one text digest**, grouped by library ("*Movies*: A (2024), B (2023) …; *Shows*: X S02 (8 episodes) …"), truncated to fit 4096 with "…and N more";
     - otherwise each group is sent as its own message.
7. **Late arrivals.**
   - If a group with the same key was **sent as its own message** to the same destination less than `LateEditHours` ago (default **6**, range 0–24, 0 = off), new items for that key are **added to that message by editing it** (F38). The edit makes no sound (owner's choice).
   - Groups that went out inside a digest are never edited; late items for them are sent as a normal new message.
   - If an edit fails, a new message is sent (§9.4).
8. **Upgrades.**
   - An item's **identity set** holds every provider ID it has (`imdb:tt…`, `tmdb:…`, `tvdb:…`, `musicbrainz:<album id>`), plus `series:<seriesId>:S{ss}E{ee}` for episodes with index numbers.
   - Unless `AnnounceUpgrades` is on (default **off**), a ready item is **suppressed** when:
     - another non-virtual library item of the same type shares a provider ID: `InternalItemsQuery { IncludeItemTypes = [type], HasAnyProviderId = {Imdb/Tmdb/Tvdb/MusicBrainzAlbum → value}, IsVirtualItem = false, ExcludeItemIds = [self] }`, or
     - for episodes, another non-virtual episode has the same series, season and episode number: `InternalItemsQuery { IncludeItemTypes = [Episode], AncestorIds = [seriesId], ParentIndexNumber = ss, IndexNumber = ee, IsVirtualItem = false, ExcludeItemIds = [self] }`, or
     - an element of its identity set is in `removedIdentities` (removed in the last 48 h; F22).
   - Items with an empty identity set are never treated as upgrades.
   - With `AnnounceUpgrades` on, upgrades carry a "♻️ Upgraded" line.

### 7.5 Coalescing security events
- **`login_failed`.**
  - Key: the **normalized username** (`ToUpperInvariant`, F15) across all IPs, so credential stuffing from many IPs makes one message.
  - The window **slides**: it ends 10 min after the last attempt.
  - The first attempt sends a message. Later attempts in the window **edit** it: "×37 from 12 IPs · last 203.0.113.5 · Infuse · `<tg-time>`".
- **`lockout`** (published before its triggering `login_failed`, F16).
  1. Hold it for up to **5 s**, so the triggering `login_failed` can be recorded first.
  2. Then send it as **its own message, which notifies** (unless the security group is silent for that destination): "🔒 *user* is locked out after N failed attempts". If a failed-login message for that user exists in the window, the lockout message **replies to it** (`reply_parameters`), and that message is also edited to add "🔒 locked".
  3. If the message to edit is still queued in the Outbox, the queued entry is re-rendered instead.
- **`user_created`** followed by **`password_changed`** for the same user within 10 s → only the creation message.
- **`login_succeeded`:** suppressed if the same (user ID, device ID) was announced in the last 12 h.

### 7.6 Update checker
- Runs **only while at least one enabled destination has `server.update_available`** (D11); otherwise it makes no network calls. The README says it contacts api.github.com.
- First check 5 min after startup (or after a subscription appears), then every **12 h**.
- It calls `GET https://api.github.com/repos/jellyfin/jellyfin/releases?per_page=20` with `User-Agent: TgJelly/<version>`, on its own `HttpClient` that uses the **same proxy settings as the bot** (§9.1), with a 30 s timeout.
- Draft and prerelease entries are skipped. Tags are parsed leniently (`v12.2` → 12.2.0.0) and compared with `ApplicationVersion` after both are normalized to four parts.
- `Mode`: **SameMajor** (default) or **AnyNewer**. Each version is announced once (`state.updateCheck.lastNotifiedVersion`).
- On 403, 429 or a network error: retry at the next interval. After 3 failures in a row, Status shows "Update check failing: *reason*". No Telegram message is sent about it.
- Message: "⬆️ Jellyfin **12.3** is available (running 12.2)" with a `[Release notes]` button (the GitHub release page).

---

## 8. Messages

### 8.1 Default layouts (Telegram HTML; all text from the string table via the `Formatter`, §8.6)
> **Amended (Plan 7).** The default layouts below are now the built-in templates, embedded Scriban files rendered by the same engine as edited templates. The layouts describe what they produce. The templates design ([templates design](2026-10-21-tgjelly-templates-design.md)) is binding for how they are built.

- **Movie / music video / book:** poster photo, with this caption:
  - **Title (Year)**,
  - `★ 7.8 · 2h 14m · Drama, Sci-Fi` (shown parts depend on the toggles),
  - the overview (truncated to fit),
  - `📁 Library`.

  Buttons as in §8.3.
- **Episodes:** poster of the season, falling back to the series, then the episode. Caption:
  - `🆕 New series` when it applies,
  - **Show** · Season 2 · 8 new episodes,
  - a list (`E01 Title` …) truncated with "+N more",
  - the library.

  Buttons for the series.
- **Album:** album cover. **Artist — Album (Year)**, N tracks, library.
- **Audiobook:** cover. **Folder title**, N parts, library.
- **Digest:** text only (§7.4 step 6).
- **Security:** text only. `🔐` plus a bold headline, then key/value lines (user, IPs, client · device, attempts, time).
- **Server:** text only. `⚙️`/`⬆️`/`✅` plus a headline, then key/value lines.
  - `server.started`: "✅ Jellyfin is up · 12.2.0 · *server name*".
  - Plugin changelogs and task errors go in `<blockquote expandable>`.
- **Delayed delivery:** a message that leaves the Outbox more than 5 min after it was **queued** (for media, after its release) gets a line `⏱ <tg-time …>` with the original time. The Renderer leaves room for this line when it measures length.

### 8.2 Posters (D8, D13)
- **Which image:**
  - Movie, music video, book: its Primary image.
  - Episodes: Season Primary → Series Primary → Episode Primary.
  - Album: the album's Primary image.
  - Audiobook: the first part's Primary image, then the folder item's.
- The poster is used only if:
  - `ItemImageInfo.IsLocalFile` is true, **and**
  - the file exists, is ≤ **10 MB**, and has one of `.jpg/.jpeg/.png/.webp`, **and**
  - the destination isn't `PhotosBlocked`.

  Otherwise the **text-only** layout is sent. `ConvertImageToLocal` is never called.
- **Upload** the original with `InputFile.FromStream(stream, "poster" + ext)` and close the stream afterwards.
- **file_id cache:** after the first successful upload, the returned `file_id` is cached, keyed by (item ID, image `DateModified`), for 24 h. It is reused for other destinations and resends.
- **Fallbacks:** if Telegram rejects the photo, or the upload fails twice with a transport error or timeout, the message is resent as text only (§9.4).
- Albums (`sendMediaGroup`) are not used in v1 (F36).

### 8.3 Buttons and links
- **Open in Jellyfin:** `{origin}{BaseUrl}/web/#/details?id={item.Id:N}&serverId={SystemId}` (F23).
  - `origin` and the base path come from `PublicUrl`. Jellyfin's BaseUrl is appended only if `new Uri(PublicUrl).AbsolutePath` doesn't already end with it (case-insensitive).
  - `PublicUrl` is valid if it is an absolute `http(s)` URL whose host contains a `.` and isn't an IP literal of IPv6 (F37). Telegram rejects IPv6 literals in button URLs with `BUTTON_URL_INVALID`, both public and private (§15 Q2). `http://` and private IPv4 addresses are accepted.
  - With an invalid or empty URL the button is left out, and the Messages section explains why.
  - The suggestion shown in the field comes from an `external=`/`all=` entry in `NetworkConfiguration.PublishedServerUriBySubnet`, otherwise `GetSmartApiUrl(IPAddress.Parse("8.8.8.8"))`. It is normalized by the same rule and labelled "verify".
- **IMDb** (`https://www.imdb.com/title/{imdb}/`) and **TMDb** (`https://www.themoviedb.org/{movie|tv}/{id}`) for movies and shows, when the IDs exist.
- **Release notes** for `server.update_available`.

### 8.4 Toggles
- **Global** (bot Settings or dashboard Messages): `ShowPoster`, `ShowOverview`, `ShowRatingGenres`, `ShowOpenInJellyfin`, `ShowProviderLinks` (all default **on**), `AnnounceUpgrades` (default **off**).
- **Per destination:** `SilentGroups` (media / security / server → `disable_notification=true`), default none.

### 8.5 Message templates (amended by Plan 7; see the [templates design](2026-10-21-tgjelly-templates-design.md))
- **Engine:** **Scriban 7.5.1**, pinned (≥ 7.2.6 required; F41).
- **Scope:** for each template key (the event keys and `media.digest`), the template in use **replaces only the text or caption**. That is an edited template, or the built-in one when none is edited. The poster and buttons still follow the toggles. Digests are templates too. Coalescing edits (§7.5) and late-arrival edits (§7.4 step 7) re-render the same template with the updated model.
- **Model:** a flat `ScriptObject` of plain values only (strings, numbers, booleans, arrays of these; timestamps as `<name>_unix` integers plus pre-formatted `<name>_text`). It never contains Jellyfin or Telegram objects. Examples:
  - `media.movie`: `title`, `year`, `overview`, `rating`, `runtime_minutes`, `genres[]`, `library`, `imdb_id`, `tmdb_id`, `jellyfin_url`, `upgraded`.
  - `media.episodes`: `series`, `season`, `count`, `episodes[]` {`number`, `title`}, `new_series`, `library`.
  - `security.login_failed`: `user`, `attempts`, `ip_count`, `last_ip`, `client`, `device`, `last_attempt_unix`, `locked`.
  - `server.task_failed`: `task`, `error`.

  The full list per key lives in `docs/templates.md`, which is written during implementation, and is also shown in the editor.
- **Escaping:** `TelegramTemplateContext : TemplateContext` overrides `Write(SourceSpan, object?)`:
  - values of type `RawHtml` are written unchanged,
  - every other expression result goes through `HtmlText.Escape`.

  `raw(x)` is a custom function registered in the model that wraps `x` in `RawHtml`. Literal template text is never escaped.
- **Limits per render:** `LoopLimit = 1000`, `RecursiveLimit = 20`, `ObjectRecursionLimit = 20`, `LimitToString = 16384` with `OnStringLimit = Throw`, `OutputLimit = 16384` with `OnOutputLimit = Throw`, `RegexTimeOut = 1 s`, and a `CancellationToken` from a 2 s `CancellationTokenSource`. Parsed templates are cached by (event key, hash of the text).
- **Preview** (`POST /TgJelly/Templates/Validate`, driven by the live preview; there is no separate Validate or Sample button):
  1. Parse errors: all of them, 1-based line and column.
  2. Two trial renders, **one with a full sample model and one with a sparse one** (optional fields null, arrays empty), with `StrictVariables = true` and `EnableRelaxedMemberAccess = false`. An unknown field is reported with its position.
  3. A check against the Telegram HTML whitelist (`b strong i em u ins s strike del span.tg-spoiler tg-spoiler a code pre blockquote[expandable] tg-emoji tg-time`) with balanced tags.
  4. A length check. Media templates are checked against 1024, with a warning when the result is over 1024 but at most 4096 ("will be shortened when sent with a poster"). Text-only keys are checked against 4096.
  5. A preview.
- **At send time:**
  - `StrictVariables = false`.
  - Output over the limit of the message type actually sent is shortened as in §8.6, and an edited template cut by `HtmlText.Truncate` is flagged "truncated".
  - Parse, runtime, limit or whitelist errors in an edited template → the **built-in template** is used, and the edited one is flagged with the error. If the built-in template fails, a minimal last-resort text is sent.
  - A flag clears when the template text changes or is reset.
  - Resetting removes the override. Saving the built-in text unchanged also removes it.

### 8.6 Formatting and i18n
- Every value inserted by a default layout goes through `HtmlText.Escape`.
- **Length** is measured with `HtmlText.PlainLength` (Telegram.Bot ≥ 22.10.3.3). Text is shortened in this order: the overview, then lists ("+N more"), then `HtmlText.Truncate`. Shortening applies to the template models, not to a layout: the template is rendered, and when too long, re-rendered with a smaller model (templates design §5.3). The shrinkable fields and the `<list>_more` counts are in `TemplateFieldCatalog.Shrink`.
- **Strings** use named placeholders and plural variants (`key.one` / `key.other`, chosen by a per-culture plural helper). Bot texts, command descriptions and the `setMyDescription`/`setMyShortDescription` texts all come from the table.
- **Language:** `PluginConfiguration.Language` (default `"en"`, not shown in the UI in v1). When more tables exist, they are also used with `language_code` for commands and descriptions.
- **Times in messages** are written as `<tg-time unix="…" format="t">HH:mm UTC</tg-time>` (`format="wDT"` when the time isn't today). Each reader sees their own local time; the fallback text is UTC and says so.
- **Numbers** are formatted by the `Formatter` for the configured culture.

---

## 9. Telegram integration

### 9.1 Client
- **HttpClient:** a `SocketsHttpHandler` built from the settings (`PooledConnectionLifetime = 3 min`) with `Timeout = 100 s`. It is not created through `IHttpClientFactory`, to keep its logging handlers away (F39).
- **Proxy `Mode`:**
  - **System** (default): .NET's default, which follows `HTTP_PROXY`/`HTTPS_PROXY`/`ALL_PROXY`,
  - **None**: `UseProxy=false`,
  - **Http**: `WebProxy(url)`,
  - **Socks5**: `WebProxy("socks5://…")`.

  Credentials live in `secrets.json`. The update checker (§7.6) uses the same settings.
- **Bot API base URL:** only from the environment variable `TGJELLY_BOT_API_BASE_URL`, read once at startup and used by the integration tests (§14.3). It is not part of the config, the API or any UI.
  - Client options: `new TelegramBotClientOptions(token, string.IsNullOrWhiteSpace(env) ? null : env) { RetryThreshold = 0 }` (F39).
  - An invalid value (`ParamName == "baseUrl"`) is logged, and Status shows it.
- Changing the token or the proxy cancels the poll loop, rebuilds the client and restarts the supervisor.

### 9.2 Polling supervisor
`BotSupervisor : BackgroundService`. It waits for `CoreStartupHasCompleted`, CompatGuard OK, the state lock (§10.4) and a token, then:
1. Build the client. `getMe`: a 401 → state **TokenRejected**: stop, and wait for a new token.
2. `getWebhookInfo`: if a URL is set → state **WebhookConflict** ("Another app set a webhook for this bot"). Retry every 5 min. The webhook is deleted only by the dashboard's `[Take over]` or by a token save (§6.1).
3. **Poll loop** (our own, D12):
   ```
   while (!ct.IsCancellationRequested) {
     updates = GetUpdates(offset: lastUpdateId is null ? null : lastUpdateId + 1, limit: 100,
                          timeout: 50, allowedUpdates: [message, callback_query, my_chat_member], ct)
     MarkPollOk()                      // state Connected, reset backoff
     foreach (u in updates) { Handle(u); lastUpdateId = u.Id; }
   }
   ```
   - `lastUpdateId` lives **in memory only**. The first poll after a start sends no offset, so Telegram returns every unconfirmed update (they are kept 24 h). Each later poll's offset confirms the previous batch.
     - This avoids stale offsets after a bot switch, and after a week without updates, when Telegram picks the next update ID at random.
     - On a graceful stop, one best-effort `GetUpdates(offset: lastUpdateId + 1, limit: 1, timeout: 0)` within 2 s confirms the last batch.
     - After a crash, the last batch may be delivered again. Handlers tolerate that: stale callbacks are answered as expired, and the duplicate rule (§6.2) stops repeated adds.
   - `Handle` stays fast and wraps everything in try/catch, so an exception is logged and the update skipped.
   - Callback queries are answered at once, ignoring "query is too old".
   - Long work goes to the bot work queue.
4. **Errors:**

   | Error | Reaction |
   |---|---|
   | 401 | **TokenRejected**, stop |
   | 409 "terminated by other getUpdates request" | **ConflictingInstance**, back off 30 s → 5 min |
   | 409 "webhook is active" / "terminated by setWebhook request" | **WebhookConflict** (see step 2) |
   | 429 | wait `retry_after` |
   | other `ApiRequestException` (5xx, 400) | exponential backoff 1 s → 60 s |
   | `RequestException` (transport) | **Unreachable** ("Telegram unreachable, check proxy"), backoff 1 s → 60 s |

   Every successful `GetUpdates` resets the backoff and sets **Connected**. Network failures during step 1 use the same backoff.
5. **Startup re-check:** every destination gets `getChat` + `getChatMember(botId)`, which sets its health (F31: updates are kept for only 24 h).
6. **Shutdown:** cancel the poll, confirm the last batch (step 3), run the graceful drain (§12), flush state. Nothing is thrown.

### 9.3 Outbox and pacing
- **Queues.** One queue per destination, saved in `queue.json`. Each entry holds:
  - `entryId`, `destinationId`, `eventKey`, `groupKey`,
  - `op` (send | edit) and `targetKey` (for edits: the `sentMessages` key, resolved to a message ID when the entry is sent; if it can't be resolved, the entry goes out as a new message),
  - `replyToKey` (optional; resolved when the entry is sent; if that message hasn't been sent yet, this entry waits behind it),
  - the render model (item IDs for media, a payload snapshot for security and server),
  - the rendered message (text or caption, poster reference, buttons, silent flag),
  - `queuedAt`, `attempts`, `notBefore`.

  Edits re-render from the model.
- **Priority:** within a destination, security and server entries go before queued media entries. Otherwise the order is FIFO.
- **Pacing:**
  - at least 1 s between sends to the same chat,
  - at most 20 per minute per group or channel,
  - at most 25 per second overall,
  - a 429's `retry_after` pauses that chat's queue.
- **Limits:**
  - **media** entries: 500 per destination; when full, the oldest media entry is dropped;
  - **security + server** entries: 50 per destination; beyond that, new ones are merged into one rolling summary entry ("+N more security/server events, see the dashboard") that is updated in place;
  - TTL is 24 h for all entries;
  - every drop is counted in Status.
- **Network retries:** exponential backoff from 2 s to 5 min with ±20 % jitter, until the TTL runs out.
- **Unhealthy destinations** (§9.4): their entries stay queued but **held**. The TTL and caps still apply.
- **Edits:** `editMessageCaption` for photo messages, `editMessageText` for text messages, found through `sentMessages` (§10.3). "message is not modified" counts as success.
- **Edit merging:** an edit entry replaces any edit already queued for the same `targetKey`, re-rendered from the latest model. Edits count toward pacing, but not toward the security/server cap.

### 9.4 Error classifier and destination health
**Matching rules**
- The description and every pattern are compared with `OrdinalIgnoreCase` on substrings.
- Rows are tried **top to bottom; the first match wins.**
- Exact strings are in Appendix A. Descriptions may change, so matching is defensive.

| # | Match | Reaction |
|---|---|---|
| 1 | 400 `not enough rights to send` and **not** `text messages` | resend the text-only layout; set **PhotosBlocked** (an informational badge; posters are skipped until the next re-check) |
| 2 | 403 `kicked`, `not a member`, `blocked`, `deactivated`, `was deleted`, `can't initiate conversation`; 400 `chat not found` | health **Lost** |
| 3 | 400 `need administrator rights`, `not enough rights to send text messages`, `have no write access` | health **NeedsPermissions** |
| 4 | 400 `message thread not found` | health **TopicMissing** |
| 5 | 400 with `Parameters.MigrateToChatId` | rewrite `ChatId` (if the new ID is already a destination, merge Events, LibraryIds and SilentGroups into it and delete the old one), save, resend once |
| 6 | 400 `can't parse entities`, `unsupported start tag`, `unmatched end tag`, `can't find end tag`, `byte offset` | resend as plain text (`HtmlText.ToPlain`); flag the template if one was used |
| 7 | 400 `inline keyboard button`, `button_url_invalid` | resend without buttons; flag the public URL. The flag clears when PublicUrl changes or after the next successful send that has buttons |
| 8 | 400 `photo_`, `image_process_failed`, `wrong file`, `photo_invalid` | resend the text-only layout |
| 9 | 400 `message is too long`, `caption is too long` | truncate to 90 % of the limit and resend once |
| 10 | 400 `message to edit not found`, `can't be edited`, `there is no text in the message to edit` | send as a new message |
| 11 | 400 `message is not modified` | success |
| 12 | 429 | wait `retry_after` |
| 13 | `RequestException` that is **not** an `ApiRequestException` (transport) | retry with backoff (§9.3). A photo send that fails this way twice → text-only |
| 14 | `ApiRequestException` with `ErrorCode >= 500` | retry with backoff (§9.3) |
| 15 | anything else | log, drop the entry, show it in Status as "last error" |

**Health**
- **Where it lives.** Health lives in `state.json` only and **never changes `Enabled`**. "Paused" (`Enabled = false`) is the user's switch.
- **While unhealthy.** A destination whose health isn't OK keeps getting newly routed events, but they are held (§9.3).
- **Re-checks** (`getChat` + `getChatMember(botId)`; for TopicMissing also a probe send to the thread) run:
  - at startup,
  - every 30 min while unhealthy (or `PhotosBlocked`),
  - on any `my_chat_member` for that chat, including a private-chat unblock,
  - on `[Check again]` or `[Send test]`, which bypass the hold.
- **Recovery.** A passing re-check sets OK (and clears PhotosBlocked if photos are now allowed), then releases the held entries (with the ⏱ line where §8.1 applies).
- **Notifications.** Becoming Lost, NeedsPermissions or TopicMissing is reported **once per new problem**, on the dashboard and as a private message to recent managers (§6.4).

---

## 10. Persistence

### 10.1 Settings: `PluginConfiguration` (XML; fields only ever added, never renamed or retyped)
```
SchemaVersion: int = 1
Revision: long = 0
Language: string = "en"                       (hidden in v1)
PublicUrl: string = ""
Proxy: ProxySettings { Mode: ProxyMode = System; Url: string = "" }
Message: MessageOptions { ShowPoster=true; ShowOverview=true; ShowRatingGenres=true;
                          ShowOpenInJellyfin=true; ShowProviderLinks=true; AnnounceUpgrades=false }
Batching: BatchingOptions {
  QuietWindowSeconds = 120   (5–3600)
  MaxHoldMinutes     = 30    (1–240, and ≥ QuietWindowSeconds/60)
  ReleaseQuietSeconds = 20   (5–600)
  DigestThreshold    = 10    (2–100)
  LateEditHours      = 6     (0–24; 0 = never edit)
  ReadyTimeoutMinutes = 15   (1–120) }
UpdateCheck: UpdateCheckOptions { Enabled = true; Mode: UpdateCheckMode = SameMajor }
Destinations: Destination[] = []
  Destination { Id: string (8 chars, [a-z0-9]); ChatId: long; ThreadId: int = 0 (0 = none / General);
                Title: string; Kind: ChatKind (Private|Group|Supergroup|Channel);
                Enabled: bool = true; Preset: PresetKind (Everything|NewMedia|AdminAlerts|Custom) = Everything;
                Events: string[]; LibraryIds: string[] (empty = all); SilentGroups: string[] }
  (ChatId, ThreadId) is unique across Destinations.
Templates: TemplateOverride[] = []
  TemplateOverride { EventKey: string; Enabled: bool (ignored since Plan 7; overrides with Enabled=false are dropped on load); Text: string = "" }
```
- Arrays are initialized to `Array.Empty<T>()` (F9), and enums are serialized by name.
- `PUT Settings` returns 400 with per-field errors for values outside the ranges.

### 10.2 Secrets: `secrets.json`
`{ "botToken": "", "proxyUsername": "", "proxyPassword": "" }`. Written to a temp file and moved into place, mode 600 on Unix. **No API ever returns these values.** The dashboard sees `hasToken`, `botUsername` and `hasProxyPassword`.

### 10.3 Runtime state
**`state.json`** (small; saved within 1 s of a change):

| Section | Contents | Cap → on overflow |
|---|---|---|
| `telegram` | `botId`, `botUsername`, `profile` {`setProfile`, `pendingCalls[]`} (the update offset is in memory only, §9.2) | — |
| `flows` | per private chat: screen, params (incl. `targetChatId`), `wizardMessageId`, `promptSentAt`, `params.await` (chat), `updatedAt` (expires 24 h) | 200 → evict least recently updated |
| `managers` | private chat ID → `lastUsedAt` (90 days) | 50 → evict oldest |
| `detectedChats` | chat ID, title, kind, `detectedAt` | 50 → evict oldest |
| `setupFailures` | kind, `at` (7 days) | 50 → evict oldest |
| `health` | per destination: status, `photosBlocked`, last error, `at`, `notified`, `nextCheckAt` | — |
| `updateCheck` | `lastCheckedAt`, `lastNotifiedVersion`, `consecutiveFailures` | — |
| `status` | `lastErrors` (20), counters (`droppedIntake`, `droppedOutbox`, `evictions`), `backupRestoredAt`, `templateFlags` {eventKey → error, kind, at, textHash}, `publicUrlFlag`, dismissed notice IDs | lastErrors 20 → drop oldest |

**`queue.json`** (larger; saved every 10 s while it changes, and within 1 s after a security or server entry is queued):

| Section | Contents | Cap → on overflow |
|---|---|---|
| `pendingMedia` | item or album ID, `firstSeen` | 20 000 → the oldest is treated as ready (the timeout path) |
| `openGroups` | destination, group key, item IDs, `firstAt`, `lastAt` | 5 000 → close the oldest group of that destination into its release buffer |
| `releaseBuffers` | per destination: closed groups, `firstAt`, `lastAt` | 2 000 groups → release now |
| `sentMessages` | (destination, key) → message ID, kind (photo or text), `sentAt`, `lastEditAt`, `eventKey`, model. Key = the media group key or `login_failed:<normalized user>`. Kept until max(`sentAt` + LateEditHours, `lastEditAt` + 10 min) | 5 000 → evict oldest |
| `outbox` | §9.3 | §9.3 |
| `dedupe` | logins (12 h), user-created markers (10 s) | 2 000 / 100 → evict oldest |
| `removedIdentities` | identity → `removedAt` (48 h) | 5 000 → evict oldest |
| `newSeries` | series ID → `firstSeen` (24 h) | 1 000 → evict oldest |
| `recentPluginEvents` | for restart reasons (10 min) | 50 → evict oldest |
| `fileIdCache` | (item ID, image DateModified) → file_id (24 h) | 2 000 → evict oldest |

Every eviction increments `status.counters.evictions`.

### 10.4 Writes and recovery
- **Settings writes** go only through SettingsStore:
  1. Lock, then clone and apply the change.
  2. `Revision++`.
  3. Set an `AsyncLocal` "from SettingsStore" flag around `UpdateConfiguration`.
  4. Write `settings.bak.json`.
  5. Raise `SettingsChanged` once.
- **Revision checks.** Mutating API calls carry `expectedRevision`; a mismatch returns 409 with the current data. The page **keeps the user's unsaved field values**, applies them on top of the fresh data, highlights fields that also changed on the server, and asks the user to save again. Bot screens always re-read the settings before rendering.
- **Outside writes.** `Plugin.UpdateConfiguration` override: when the AsyncLocal flag is **not** set (e.g. a POST to the generic endpoint), it sets `Revision = max(current, incoming) + 1`, writes `settings.bak.json` and raises `SettingsChanged`.
- **Restoring a wiped config.** At startup, if `Configuration` has no destinations, `Revision == 0`, and `settings.bak.json` has a higher revision, the backup is restored and Status shows a notice (F9).
- **State files** are written to a temp file and moved into place. A file that fails to parse is renamed `*.bad-<utc>.json`, the plugin starts with that file empty, and Status shows a warning.
- **`state.lock`** is held with `FileShare.None` while the plugin runs.
  - An instance that can't get it **starts nothing that consumes intake or talks to Telegram** (intake, bot and outbox all stay off). The API serves Status only.
  - It retries every 5 s. Status shows "Waiting for the previous instance to stop" (after an in-process restart, F4) or, after 2 min, "Another Jellyfin process is using TgJelly's data folder". The README says that two servers sharing one config folder are not supported.
- **Token change** (§6.1):
  - **Same bot** (`getMe.id == state.telegram.botId`): everything is kept.
  - **Different bot** (confirmed first when destinations exist, §6.1 step 3):
    - store the new bot ID and username (the in-memory update offset restarts with the supervisor),
    - clear `flows`, `managers`, `detectedChats`, `setupFailures`, `sentMessages`, `fileIdCache` and `telegram.profile`. Queued edits then can't resolve their `targetKey` and go out as new messages (§9.3),
    - set every destination's health to **Lost** ("bot changed: add @new to this chat"). Its outbox entries are held.
    - Adding the new bot to a destination's chat (through the inline add links, which match the existing destination per the duplicate rule in §6.2, or by hand) triggers a re-check, which brings it back to OK.
    - Settings, `pendingMedia`, `openGroups`, `releaseBuffers` and `dedupe` are kept.
- **Disconnect bot:**
  - clears `botToken` (proxy credentials are kept),
  - clears the bot-scoped sections (`flows`, `managers`, `detectedChats`, `setupFailures`, `health`, `sentMessages`, `outbox`, `fileIdCache`) and `telegram.profile`,
  - keeps `telegram.botId` and `botUsername`, so reconnecting the same bot is recognised as the same bot and is not treated as a switch,
  - keeps settings, `pendingMedia`, `openGroups`, `releaseBuffers`, `dedupe`, `removedIdentities` and `newSeries`.
- **Uninstall:** files stay on disk, as Jellyfin does with its own config.

---

## 11. Dashboard

### 11.1 Page (`Web/tgjelly.html` + `Web/tgjelly.js`)
- `GetPages()` returns `tgjelly` (HTML, `EnableInMainMenu=true`, `DisplayName="Telegram"`, `MenuIcon="send"`), **listed first**, then `tgjelly.js`. `MenuSection` is not set (F30).
- **HTML shell:** `<div data-role="page" id="tgjConfigPage" class="page type-interior pluginConfigurationPage" data-controller="__plugin/tgjelly.js">…</div>`. All markup, `<template>` and `<style>` elements go inside this div.
  - **No inline scripts and no `${` anywhere in the HTML** (F26).
  - Elements: `emby-input`, `emby-checkbox`, `emby-button`, `emby-linkbutton`. `<select is="emby-select">` is used only with `class="emby-select-withcolor emby-select"` and an explicit label, so it renders even without the upgrade (F27). Native `<details>/<summary>` for collapsible sections; a plain `<textarea>` with scoped CSS.
  - Colors only from the stock classes or `var(--jf-palette-*)`.
- **Controller:** `export default function TgJellyPage(view, params)`.
  - DOM listeners are bound once, in the constructor.
  - `viewshow` is idempotent: load the strings, status, settings and destinations, then render.
  - Elements are queried only through `view.querySelector`, and every ID and class is prefixed `tgj-`.
  - State lives in the controller closure.
- **Status polling:** every 3 s while the page is visible. It stops on `viewbeforehide`/`viewhide`/`viewdestroy`, when `!view.isConnected` or `view.classList.contains('hide')`, and pauses while `document.hidden` (F28).
- **Network:** only `ApiClient.fetch` / `ApiClient.ajax` with `dataType:'json'` and `url: ApiClient.getUrl('TgJelly/<path>')`. Never a hard-coded leading `/`, so BaseUrl setups work (F29). The page never calls `api.telegram.org`.
- **Strings:** from `GET TgJelly/Strings?lang=<document.documentElement.lang>`, with fallback to `en`.
- **QR code:** none. "Open @bot" links are plain `https://t.me/...` links.

### 11.2 API (`[ApiController] [Route("TgJelly")] [Authorize(Policy = Policies.RequiresElevation)]`)
- Every response is JSON.
- Mutating requests carry `expectedRevision`: in the body, or as a query parameter for `DELETE`. Token, test, check, validate and dismiss calls don't need it.

| Method & path | Purpose |
|---|---|
| `GET Status` | connection state, bot username, compat, revision, counts, last errors, notices |
| `POST Status/Dismiss` `{noticeId}` | dismiss a notice |
| `POST Token` `{token, setProfile, confirmSwitch?}` | §6.1 → Status, or `{needsConfirm, message}` |
| `DELETE Token` | Disconnect (§10.4) |
| `POST Webhook/TakeOver` | `deleteWebhook`, then restart the supervisor |
| `GET Settings` / `PUT Settings` | public URL (with suggestion), message toggles, batching, update check; GET also returns proxy mode and URL for display, PUT ignores proxy fields |
| `PUT Proxy` `{mode, url, username?, password?}` | proxy settings; an omitted username or password keeps the stored value, an empty string clears it |
| `GET Destinations` | destinations + health + detected chats (if any) + whether the manual block is unlocked |
| `POST Destinations` `{chatId \| topicLink, preset}` | Advanced add by ID or link (§6.5); checked first. The §6.5 gate only changes what the page shows; admins can always call this |
| `PUT Destinations/{id}` | preset, events, libraries, silent groups, enabled |
| `DELETE Destinations/{id}?leave=bool&expectedRevision=n` | remove, optionally `leaveChat` |
| `POST Destinations/{id}/Test` | test send (also a re-check) |
| `POST Destinations/{id}/Check` | re-check now |
| `POST DetectedChats/{chatId}` `{preset, threadId?}` / `DELETE DetectedChats/{chatId}` | set up (checked first, as in §6.2 step 2) / dismiss a detected chat |
| `GET Libraries` | `GetVirtualFolders()` → `{id, name, collectionType}` |
| `GET Templates` | `{Revision, Items}`, one item per template key (digest included): `EventKey`, `Label`, `Media`, `Text` (in use), `BuiltIn`, `Edited`, `Fields`, `SampleFull`, `SampleSparse`, `Flag` |
| `PUT Templates/{eventKey}` `{expectedRevision, text}` | save; empty text or text equal to the built-in removes the override (no `Enabled`) |
| `DELETE Templates/{eventKey}?expectedRevision=n` | reset to the built-in; also clears the flag |
| `POST Templates/Validate` `{eventKey, text}` | the live preview: `{Errors, Warnings, Preview, Length, Limit, Html, Caption, Poster, Buttons}` (§8.5) |
| `POST Templates/Test` `{eventKey, text, destinationId}` | send the text on a sample model to a destination (poster and buttons as for a real message) → the test result |
| `GET Links` | the `t.me` links: open bot, `?start=add`, startgroup, startchannel |
| `GET Strings?lang=xx` | best match among the embedded tables, falling back to `en` key by key |
| `GET Diagnostics` | `{ "text": "…" }`, a report with secrets redacted |

---

## 12. Reliability and safety
- **Host safety:** §5.4 rules 2–5.
- **Intake queues:** two bounded queues:
  - **media**, 10 000 events: when full, queued `ItemUpdated` entries are evicted before `ItemAdded`; drops are counted;
  - **other**, 1 000 events: when full, the newest is dropped and counted.
- **CompatGuard** banner: "This TgJelly build is for Jellyfin 10.11. Update the plugin (Dashboard → Plugins) to get the Jellyfin 12 build."
- **Logging:** `ILogger<T>`. Request URLs are never logged. Everything TgJelly logs, and the diagnostics report, has secrets redacted (`\d{5,}:[A-Za-z0-9_-]{30,}` → `<token>`). Proxy passwords are never logged.
- **Templates:** the limits in §8.5, a data-only model, and a pinned Scriban version.
- **Delivery semantics:** at least once.
  - **On a graceful stop**, within 5 s:
    1. the intake queues are drained into MediaReadiness and the Aggregator,
    2. open security windows are closed and rendered into the Outbox,
    3. `state.json` and `queue.json` are flushed.
  - **On a crash**, events from roughly the last second, anything still in the intake queues, and media changes since the last `queue.json` save (up to 10 s) may be lost. Duplicate deliveries after a restart are acceptable.
  - Security and server entries are never dropped for capacity: past their cap they are folded into the rolling summary (§9.3). They are removed only by the 24 h TTL.

---

## 13. Build, compatibility and release

### 13.1 Repository layout
```
Jellyfin.Plugin.TgJelly.sln
src/Jellyfin.Plugin.TgJelly/        plugin (TargetFrameworks net9.0;net10.0)
  Configuration/ State/ Intake/ Pipeline/ Rendering/ Telegram/ Bot/ Api/ Web/ Strings/ Compat/
tests/Jellyfin.Plugin.TgJelly.Tests/              unit tests (both TFMs)
tests/Jellyfin.Plugin.TgJelly.IntegrationTests/   docker smoke tests + fake Bot API server
build/package.(sh|py)                zip + meta.json + manifest generation
.github/workflows/ci.yml, release.yml
docs/templates.md, docs/superpowers/specs/…
README.md (setup with screenshots, BotFather restriction, GitHub contact for update checks,
           troubleshooting: proxy, CSP/Rocket Loader, 409/webhook conflicts, one server per config folder)
```

### 13.2 Project file
- `TargetFrameworks`: `net9.0;net10.0`. Nullable enabled, `TreatWarningsAsErrors`, `AnalysisMode=AllEnabledByDefault`, StyleCop and Serilog analyzers, the same as the official template.
- **net9.0:** `Jellyfin.Controller` and `Jellyfin.Model` **10.11.0**, with `ExcludeAssets=runtime`.
- **net10.0:** the same packages at **12.0.0**, with `ExcludeAssets=runtime`, and `DefineConstants` `JF12`.
- **Exact versions:** `Telegram.Bot 22.10.3.3`, `Scriban 7.5.1`.
- **Embedded resources:** `Web/tgjelly.html`, `Web/tgjelly.js`, `Strings/en.json`.

### 13.3 Versions and ABI
| Track | Framework | targetAbi | Version | Zip |
|---|---|---|---|---|
| Jellyfin 10.11 | net9.0 | `10.11.0.0` | `1.X.Y.0` | `tgjelly_1.X.Y.0.zip` |
| Jellyfin 12 | net10.0 | `12.0.0.0` | `2.X.Y.0` | `tgjelly_2.X.Y.0.zip` |

- Git tag `vX.Y` produces both builds from the same commit. Every 12 build has a higher version number than every 10.11 build (F7). After a server upgrade, the plugin update task moves it to the 12 build. Until then, CompatGuard keeps the plugin inactive (F8).
- Each track compiles against the **lowest** Jellyfin package version of its line, so its targetAbi is accurate.
- The 10.11 track gets the same features until a separate decision freezes it.

### 13.4 Packaging (`build/package`)
- **For each framework:**
  1. `dotnet publish -c Release -f <tfm>`.
  2. Copy **exactly** `Jellyfin.Plugin.TgJelly.dll`, `Telegram.Bot.dll` and `Scriban.dll`, and write a `meta.json`: guid, name, overview, description, owner `MrSnowball-dev`, category **`Administration`**, version, targetAbi, changelog, timestamp, **`imagePath` empty**, and `assemblies` = those three DLLs (F5).
  3. Zip and compute the MD5.
- **Manifest:** `manifest.json` is one `PackageInfo` entry with the GUID from §5.1 and `imageUrl` = `https://mrsnowball-dev.github.io/tgjelly_plugin/logo.png`, a PNG kept on `gh-pages`. New version entries (sourceUrl, checksum, timestamp, targetAbi, changelog) are appended; older ones are kept.
- **CI fails** if a zip contains anything other than the three DLLs and `meta.json`, or if `tgjelly.html` lacks `data-role="page"` or contains `${`.

### 13.5 CI and distribution
- **`ci.yml`** (PRs, pushes to `main`, manual runs): install .NET 9 and 10, build both frameworks, run unit tests on both, the web checks, package, then the integration smoke tests (§14.3). A change gets the full smoke matrix once, on its PR (and on a manual run). A push to `main` skips the smoke tests. Branch pushes without a PR run nothing.
- **`release.yml`** (tag `v*`): first checks that `ci.yml` passed on `main` for the tagged commit, and stops if it didn't. Then it builds and packages the release zips and smoke-tests them on one image per track (10.11.11 and 12.2, without a BaseUrl). It then creates a GitHub Release with both zips and commits the updated `manifest.json` to `gh-pages`.
- **Users add:** `https://mrsnowball-dev.github.io/tgjelly_plugin/manifest.json`.
- ⚠️ **Prerequisite:** the repo is currently **private**. On a free plan, Pages and public release downloads need it to be public, so the owner makes it public before the first release.
- **Local development:**
  - The implementation plan **asks the owner before installing anything**.
  - The .NET 9 and 10 SDKs can be installed user-locally (`dotnet-install.sh` into `~/.dotnet`).
  - A local container runtime is optional, because CI runs the smoke tests. `build/dev-deploy` (copy a build into a local Jellyfin container's `plugins/TgJelly_<ver>/` and restart it) is used only if the owner sets one up.

---

## 14. Testing

### 14.1 Unit tests (xUnit, run on both TFMs, `Microsoft.Extensions.TimeProvider.Testing` for time)
- **Router:** exact key matching, presets and preset upgrades on load, library filter (Guid parsing, removed libraries), `Enabled`.
- **Aggregator:**
  - group quiet window and max hold, `ScanFinished` closing groups,
  - release buffer: exactly `DigestThreshold` vs threshold + 1 groups, held while `IsScanRunning`, its own max hold,
  - late-arrival edits within and beyond `LateEditHours`, with no edits for digested groups.
- **Security coalescing:**
  - lockout arriving **before** login_failed (and after),
  - credential stuffing across 50 IPs giving one message,
  - the lockout's own notifying message plus the reply,
  - re-rendering an entry that is still queued,
  - user_created + password_changed,
  - login_succeeded dedupe.
- **MediaReadiness:**
  - the filter table (each excluded kind; AudioBook before Audio),
  - album tracking (100 tracks → 1 pending album),
  - multi-file audiobook folder → one group,
  - readiness via forwarded ItemUpdated, the timeout, deleted items,
  - ItemUpdated forwarding rules,
  - persistence across a simulated restart.
- **Upgrades:** a library lookup hit, a `removedIdentities` hit within 48 h, an empty identity set, `AnnounceUpgrades` on.
- **Renderer:**
  - **golden-file** tests per layout (including `server.started` and audiobooks),
  - escaping of `& < >`,
  - non-BMP titles at exactly 1024 and 4096 code points,
  - the truncation order and room for the ⏱ line,
  - `<tg-time>` output,
  - plural strings,
  - poster eligibility (non-local, missing, > 10 MB, unsupported type, PhotosBlocked),
  - the file_id cache.
- **LinkBuilder and PublicUrl:** no-dot hosts and IPv6 literals rejected, `http://` and private IPv4 accepted, BaseUrl appended exactly once.
- **Telegram-HTML validator:** allowed and forbidden tags, unbalanced tags.
- **Scriban wrapper:**
  - auto-escaping vs `raw()`,
  - each limit (loop, recursion, output, string, timeout),
  - 1-based error positions,
  - StrictVariables catching typos in validation only,
  - sparse-model render,
  - truncation flag at send time.
- **Error classifier:** table-driven over Appendix A, first match wins (e.g. "not enough rights to send photos" → row 1, not row 3), case-insensitive.
- **Outbox:**
  - pacing (per chat, per group, overall), priority of security and server entries, `retry_after`, jitter bounds,
  - edit merging per `targetKey`, unresolvable edit targets sent as new messages, `replyToKey` waiting for an unsent target,
  - 5xx `ApiRequestException` retried, permanent 400s not retried,
  - migration (including the merge on collision),
  - every fallback in §9.4, TTL, both caps and the rolling summary,
  - held entries for unhealthy destinations and their release after a passing re-check.
- **Health:** re-check schedule, `[Check again]`, a private-chat unblock, health never touching `Enabled`.
- **SettingsStore:**
  - revision conflicts (the API returns the current data),
  - AsyncLocal vs outside `UpdateConfiguration`,
  - backup and restore,
  - XML round-trip, and old XML (missing fields) loading with defaults,
  - range validation.
- **StateStore:**
  - atomic writes, recovery from a corrupt file,
  - every on-cap rule in §10.3,
  - lock contention (the loser starts nothing, retries, then starts),
  - write cadence of the two files.
- **Token change:** same bot keeps state; a different bot always resets bot-scoped state (confirmed first only when destinations exist) and marks destinations Lost. **Disconnect:** what is kept; reconnecting the same bot is not treated as a switch.
- **Poll loop:** no offset on the first poll, offset confirmation afterwards, the best-effort confirmation on graceful stop, and handlers tolerating a redelivered batch.
- **FlowEngine:**
  - callback data ≤ 64 bytes and versioned,
  - expired menus and prompts, stale v0.1 reply keyboards,
  - `my_chat_member` correlation by `from.id` (two admins, adds by others, redelivery, rights re-grant, group `/start@bot`),
  - events submenus,
  - the duplicate destination rule (non-forum in step 2, forum after the topic is chosen),
  - `Use an existing topic…` recording `topic_not_pickable` and opening the link prompt,
  - the topic promotion path,
  - topic link parsing,
  - detected-chat rules,
  - setup-failure gating.
- **UpdateChecker:**
  - `v12.2` / `v10.11.11` parsing, prereleases and drafts, SameMajor vs AnyNewer,
  - runs only while subscribed, uses the proxy,
  - failure counter, announce once.

### 14.2 Telegram fixtures
Recorded Bot API JSON for:
- `my_chat_member` (channel, group, kicked, private block and unblock),
- `chat_shared` (legacy), `my_chat_member` promotion (member → administrator with `can_manage_topics`) and rights-grant (administrator → administrator) fixtures, `getChat` (forum and non-forum, with permissions), `getChatMember` (member, restricted without photos, administrator with and without `can_manage_topics`),
- `forum_topic_created`, `createForumTopic`, callback queries,
- every error in Appendix A.

They are parsed with Telegram.Bot and passed to `UpdateRouter`, FlowEngine and the classifier.

### 14.3 Integration smoke tests (CI; Docker)
- **Matrix** (`.github/smoke-matrix.json`): the latest image of each track, `jellyfin/jellyfin:10.11.11` with the 1.x zip and `12.2` with the 2.x zip, each also with BaseUrl `/jf`, plus `12.2` with the 1.x zip (F8). The owner narrowed it to these images on 2026-10-21; 10.11.0 and 12.0 are no longer tested. A release runs only the entries marked `release` (§13.5).
- **Setup:**
  1. Unpack the zip into the container's `plugins/TgJelly_<ver>/` and start the container with `TGJELLY_BOT_API_BASE_URL=http://stub:8081`.
  2. Complete the startup wizard through `/Startup/*` and authenticate.
  3. Assert:
     - `GET /Plugins` lists TgJelly as **Active**,
     - `GET /web/ConfigurationPage?name=tgjelly` contains `data-role="page"`,
     - `GET /web/ConfigurationPage?name=tgjelly.js` has a JavaScript content type,
     - `GET /TgJelly/Status` returns JSON.
- **The stub** answers:
  - `getMe`, `getWebhookInfo`, `deleteWebhook`,
  - `setMyCommands`, `deleteMyCommands`, `setMyDescription`, `setMyShortDescription`, `setMyDefaultAdministratorRights`,
  - `getUpdates` (returns `[]` after 1 s),
  - `getChat`, `getChatMember`,
  - `sendMessage`, `sendPhoto`, `editMessageText`, `editMessageCaption`, `answerCallbackQuery`.

  Unknown methods return `{"ok":false,"error_code":404,"description":"Not Found"}`. Telegram.Bot needs the description to raise an `ApiRequestException`. Every request is recorded.
- **End to end:**
  1. `PUT Settings` with `Batching {QuietWindowSeconds:5, ReleaseQuietSeconds:5, MaxHoldMinutes:1, ReadyTimeoutMinutes:1}`.
  2. `POST Token` with a well-formed fake token.
  3. `POST Destinations {chatId, preset:"NewMedia"}`.
  4. Copy a sample movie into the library and call `POST /Library/Refresh`.
  5. Assert that a `sendPhoto` or `sendMessage` for that movie reaches the stub **within 180 s**.
  6. `POST Destinations {chatId: <second>, preset:"AdminAlerts"}`, then `POST /System/Restart`. Assert that **exactly one** `server.started` message for the second chat reaches the stub within 120 s of the restart. This also exercises the in-process restart path (F4).
  7. `/start setup` → find the `startchannel` URL button → push `my_chat_member` (channel, left → administrator, from the admin) → press New media → the destination exists, its test message arrived, and the wizard was edited to Done.

### 14.4 Manual pre-release checklist (real Telegram)
- **BotFather:** create a new bot and restrict it in BotFather. Record the exact menu path for the README. Verify that:
  - (a) the inline add links can still add the restricted bot to a channel and a group,
  - (b) members and subscribers who aren't on the allow-list see its posts,
  - (c) an unlisted user's `/start` produces no update.
- **Inline add links** on Android, iOS and Desktop: channel, group (with and without media permissions for members), forum (General, a created topic, the "Allow topic creation" promotion, an existing topic via link), and a chat the bot is already in.
- **Already a member:** does a group receive `/start@bot add` when the bot is already a member (Q10)? Yes (owner, 2026-10-21).
- **Posters:** different sizes and formats, including a > 10 MB file falling back to text.
- **Buttons:** with a LAN IP public URL and with a public HTTPS URL.
- **Conflicts and proxies:** a second app polling with the same token; a webhook set by another app (`[Take over]`); SOCKS5 and HTTP proxies.
- **Chat lifecycle:** kicking the bot, removing its rights, `[Check again]`, upgrading a group to a supergroup, switching to a different bot.
- **Server lifecycle:** an in-process server restart (no duplicate polling; `server.started` once), and a 10.11 → 12 server upgrade (CompatGuard, then auto-update).
- **Late edits:** editing a late-episode message several hours after it was sent.
- **Message templates** (templates design §12):
  - Open each event in the editor and check that the built-in template reads naturally and is easy to tweak.
  - Edit the Movie template, preview it, send a test to a channel and to a forum topic, save, and wait for a real movie to arrive.
  - Reset it, and confirm that the next real movie looks like v0.2.
  - Check the editor on a phone (one column, preview under the template).
- **Icons** on a bot whose owner has no Telegram Premium: every screen still sends; icons absent or stripped (TelegramSender fallback).

### 14.5 Process
Implementation follows test-driven development: write a failing test first for every unit in §14.1.

---

## 15. Open questions to settle during implementation
1. BotFather's exact menu path for the access restriction, and the three behaviours in §14.4 (a)–(c).
2. Does Telegram reject `http://` or private-IP URLs in inline buttons (`BUTTON_URL_INVALID`)? Only TDLib's "no dot" rule is verified. The result decides whether PublicUrl validation also rejects private IPs.
   **Answered (real bot, 2026-10-19):** `http://192.168.1.5:8096/…`, `http://10.0.0.5:8096/…`, `http://jellyfin.lan:8096/…` and `https://jf.example.com/web/#/details?…` are accepted. `http://localhost:8096/…` fails with `Bad Request: inline keyboard button URL 'http://localhost:8096/web/' is invalid: Wrong HTTP URL`. IPv6 literals fail, both `[fd00::1]` and `[2001:4860:4860::8888]`, with `Bad Request: BUTTON_URL_INVALID`. So private IPv4 stays valid, and IPv6 literals are now rejected (§8.3).
3. What does `request_chat` look like on current clients, including the `chat_is_forum` promotion prompt, and do users see a confirmation before the bot gets admin rights? (Handled either way by the check in §6.2 step 2.) **Superseded by rev. 3:** `request_chat` is no longer used.
4. How should the General topic of a forum be targeted: no `message_thread_id` (current choice) or thread 1?
   **Answered (real bot, 2026-10-19):** no `message_thread_id`. A send without it lands in General; `message_thread_id: 1` fails with `Bad Request: message thread not found`. `ThreadId = 0` stays. Also observed: a bot that is only a plain member of a supergroup can delete its own messages there.
5. Which exact `Content-Type` does 10.11 serve for embedded `.js`? (Checked by the smoke test.)
   **Answered (CI smoke, 2026-10-20):** `application/javascript` on 10.11.0, 10.11.11, 12.0 and 12.2 (run 39492717054), with and without a `/jf` BaseUrl. The smoke check only requires that the type contains `javascript`.
6. Are `emby-select`, `emby-textarea`, `emby-collapse` and `emby-toggle` registered on a deep link? (The page works either way.)
7. Does a net9.0/net10.0 build put `Microsoft.Extensions.Options.dll` or `System.Text.Json.dll` in `bin`? (Irrelevant for the zip; the CI check shows it.)
   **Answered (plan 2, CI publish listing):**
   - **net9.0:** `bin` gets `Microsoft.Extensions.Options.dll`, `Microsoft.Extensions.Primitives.dll` and `Microsoft.Extensions.DependencyInjection.Abstractions.dll`. These are Telegram.Bot's 8.0 dependencies, which Jellyfin 10.11's own packages don't bring in here because of `ExcludeAssets=runtime`.
   - **net10.0:** `bin` gets neither of these, and `System.Text.Json.dll` doesn't appear in either build.
   - **Effect on the zip:** none. The zip ships only the whitelist (`Jellyfin.Plugin.TgJelly.dll`, `Telegram.Bot.dll`). At runtime the plugin's load context falls back to the host's newer copies, which the smoke tests confirm on 10.11 and 12.
8. Is there a server-side time limit for bots editing their own messages (F38)? `LateEditHours` ≤ 24 keeps the exposure small.
   **Answered (owner, from experience running bots):** there is no age limit; a bot can edit its own messages at any time. The 0–24 range of `LateEditHours` stays as a product choice (a late edit is silent, so very old messages shouldn't change unnoticed), not a Telegram constraint.
9. The exact Bot API descriptions of an HTML parse error and of "can't initiate conversation", recorded into Appendix A and the fixtures. The classifier already matches several variants.
   **Partly answered (real bot, 2026-10-19):** the parse error is `Bad Request: can't parse entities: Can't find end tag corresponding to start tag "b"`, which classifier row 6 matches. "Can't initiate conversation" needs a user who knows the bot but never pressed Start; a user ID the bot has never seen gives `Bad Request: chat not found` instead, which row 2 also treats as Lost.
10. Does a group receive `/start@bot <payload>` from the adding user after a `startgroup` add, including when the bot is already a member (F43)? If clients don't send it, nothing breaks: the detected-chat rows on the prompt remain.
    **Answered (owner, 2026-10-21, v0.2 on real phones):** yes, the `/start` is posted, also into a group the bot is already in. The inline add flow and the topic creation step work as designed.

---

## Appendix A: Telegram error descriptions (from the research; test fixtures use these)
- 409: `Conflict: can't use getUpdates method while webhook is active; use deleteWebhook to delete the webhook first` · `Conflict: terminated by other getUpdates request; make sure that only one bot instance is running` · `Conflict: terminated by setWebhook request`
- 403: `Forbidden: bot was blocked by the user` · `Forbidden: user is deactivated` · `Forbidden: bot was kicked from the group chat` · `Forbidden: bot was kicked from the supergroup chat` · `Forbidden: bot was kicked from the channel chat` · `Forbidden: bot is not a member of the group chat` · `Forbidden: bot is not a member of the supergroup chat` · `Forbidden: bot is not a member of the channel chat` · `Forbidden: the group chat was deleted` · `Forbidden: bot can't initiate conversation with a user` (well known, but not among the research-verified strings; record it from a real bot, §15 Q9)
- 400 (chat/rights): `Bad Request: chat not found` · `Bad Request: private chat not found` · `Bad Request: need administrator rights in the channel chat` · `Bad Request: have no write access to the chat` · `Bad Request: not enough rights to send text messages to the chat` · `Bad Request: not enough rights to send photos to the chat` · `Bad Request: message thread not found` · `Bad Request: group chat was upgraded to a supergroup chat` (with `parameters.migrate_to_chat_id`)
- 400 (content): `Bad Request: message is too long` · `Bad Request: message caption is too long` · `Bad Request: can't parse entities: Unsupported start tag "x" at byte offset N` · `Bad Request: can't parse entities: Can't find end tag corresponding to start tag "b"` (recorded, §15 Q9) · `Bad Request: inline keyboard button URL '<url>' is invalid: Wrong HTTP URL` (recorded) · `Bad Request: BUTTON_URL_INVALID` (recorded, for IPv6 literal hosts)
- 400 (edits/callbacks): `Bad Request: message is not modified: specified new message content and reply markup are exactly the same as a current content and reply markup of the message` · `Bad Request: message to edit not found` · `Bad Request: message can't be edited` · `Bad Request: there is no text in the message to edit` · `Bad Request: query is too old and response timeout expired or query ID is invalid`
- 429: `Too Many Requests: retry after N` (with `parameters.retry_after`)
- Transport (Telegram.Bot `RequestException`): `Bot API Request timed out` · `Bot API Service Failure: <ExceptionType>: <message>`
