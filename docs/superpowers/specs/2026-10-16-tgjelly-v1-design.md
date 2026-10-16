# TgJelly v1: design spec

- **Status:** draft, waiting for owner review
- **Date:** 2026-10-16
- **Repo:** `github.com/MrSnowball-dev/tgjelly_plugin` (GPL-3.0)
- **Supersedes:** nothing; this is the first spec

TgJelly is a Jellyfin server plugin with a built-in Telegram bot. Version 1 is a **notifier**. It sends Jellyfin events (new media, security/account events, server/admin events) to Telegram chats, groups and channels that the server's admin chooses. Setup is easy to follow and can be done both from the Jellyfin dashboard and from inside the bot.

---

## 1. Intent

### 1.1 What the owner asked for
- The repo description reads: "A (better) Jellyfin plugin containing telegram bot interface to be notified about Jellyfin events."
- First and foremost a **notifier** with **simple, understandable setup**, configurable through **a GUI in the Jellyfin plugin settings AND in the bot itself** (inline buttons, persistent flows). Later features build on it.
- Only the **server admin** sets up destinations (chats/groups/channels). Regular Jellyfin users never deal with the bot.
- v1 event groups: **new media**, **security/accounts**, **server/admin**.
- Must support **Jellyfin 10.11 and 12.x**. Released **publicly** through a plugin repository.
- **English only, but i18n-ready.**
- Messages: **polished default layouts with toggles, plus an optional hidden custom template for each event**.
- Setup corrections the owner gave during review:
  1. Start by asking whether to create a new bot or use an existing one, and accept a pasted token.
  2. No "connect your Telegram account" step. One personal bot is the normal case. When a new bot is created, point the owner to **restrict the bot to themselves (or whoever they choose) in @BotFather**.
  3. Add destinations through **Telegram's native chat picker**. Manual setup appears only under Advanced, and only when the main flow fails.
- Access model: **trust the BotFather restriction**. Anyone who can open the bot's private chat can manage it.
- Posters are sent **as-is**, without resizing.
- Template engine: **Scriban**.
- Roadmap after v1: **interactive bot features** (search or browse the library, stats, trigger tasks) and **playback notifications**.
- Out of scope: per-user subscriptions and non-Telegram transports.

### 1.2 Assumptions (not stated by the owner)
- "(better)" means better than notifiers built on the Webhook plugin: no hand-written templates, no chat IDs, no extra service to run.
- Most installs sit behind NAT or in Docker without a public HTTPS endpoint. That is why the bot uses long polling and uploads posters instead of linking to them.

### 1.3 Success criteria
1. **From a fresh install to the first notification takes only a token paste plus a few taps:** choose new or existing bot → paste the token → Open bot → pick a chat in the native picker → pick a preset. No chat IDs, templates or config files.
2. The **same zip format and the same code** run on Jellyfin 10.11.x and 12.x. Each server automatically gets the build that matches it.
3. **The plugin can never harm Jellyfin.** No unhandled exception, slow handler or Telegram outage may break, block or stop the server.
4. A library scan with hundreds of new items produces **a few messages or one digest**, never one message per item.
5. Telegram outages, chats the bot was removed from, missing permissions and rate limits **degrade gracefully**, and the plugin explains them in the dashboard and in the bot.

---

## 2. Scope

**In v1:** the event catalog in §7, the message layouts and templates in §8, the setup UX in §6, both builds with the release pipeline in §13.

**Planned later (the architecture must leave room for these, but v1 does not implement them):** playback notifications (including a "now playing" message edited in place) and interactive bot features.

**Non-goals for v1:**
- per-user subscriptions or linking Jellyfin users to Telegram,
- transports other than Telegram,
- a webhook receive mode,
- image resizing,
- languages other than English (only the structure for them),
- settings export and import,
- a "server started" notification,
- notifying failed logins for unknown usernames (Jellyfin publishes no event for them).

---

## 3. Decision log

| # | Decision | Why |
|---|---|---|
| D1 | One C# plugin hosts everything: the bot (long polling), the event pipeline and the dashboard page | A single install, no ports to open, one shared config, direct access to Jellyfin |
| D2 | Telegram.Bot **22.10.3.3**, pinned exactly | Well maintained (checked by the owner) and covers the full Bot API. This version or later counts text length in Unicode code points (`HtmlText.PlainLength`) |
| D3 | Two builds: net9.0 / ABI 10.11.0.0 and net10.0 / ABI 12.0.0.0; version numbers 1.x and 2.x | Jellyfin 12 moved to .NET 10, and its compatibility check only enforces an upper bound (§4) |
| D4 | Only the admin configures; the bot sends to chats, groups and channels | The owner chose admin-managed destinations |
| D5 | **Trust the BotFather restriction** (no pairing) | Owner's decision. The risk and how we reduce it are in §6.4 |
| D6 | Destinations are added through the native `request_chat` picker; manual methods are under Advanced only | The owner's setup correction |
| D7 | Default layouts with toggles, plus an optional Scriban template per event that overrides only the text or caption | The owner's choice. Scriban gives structured error positions and execution limits |
| D8 | Posters uploaded as-is; if a poster is over 10 MB or Telegram rejects it, the message is sent as text only | Owner's decision |
| D9 | The token and proxy credentials live in a separate `secrets.json`, not in the Jellyfin config XML | Jellyfin's generic config GET returns the whole XML to admins, and a failed load overwrites it with defaults (§4) |
| D10 | The dashboard uses only TgJelly's own API endpoints (fine-grained updates with a revision check), never the generic `Plugins/{id}/Configuration` | The generic POST replaces the whole config and would overwrite changes made in the bot |
| D11 | Server updates are detected by polling GitHub releases | Jellyfin has no update check of its own (§4) |

---

## 4. Platform facts this design relies on

These facts were checked against Jellyfin **v10.11.11** and **v12.2** server sources, jellyfin-web at the same tags, jellyfin-plugin-webhook (main), Telegram.Bot (main, matching 22.10.3.3), and the Telegram Bot API docs (Bot API 10.3, 2026-08-24). A separate verifier re-checked every load-bearing claim. Paths are relative to the jellyfin repo.

**Plugin hosting (identical in both versions unless noted)**
- F1. Plugins register `IHostedService`s from `IPluginServiceRegistrator.RegisterServices`. That runs before the host is built (`Emby.Server.Implementations/Plugins/PluginManager.cs`). The services are started by `_jellyfinHost.StartAsync()` (`Jellyfin.Server/Program.cs`).
- F2. Discovered types (registrator, plugin class, controllers) must be **public**, non-generic and concrete. The registrator needs a public parameterless constructor.
- F3. **Jellyfin sets no HostOptions,** so .NET's default `BackgroundServiceExceptionBehavior.StopHost` applies. An unhandled exception in a background service **stops the whole server**. A throw from `StartAsync` keeps Jellyfin from starting.
- F4. Restarts happen **in-process**. A new host, a new plugin instance and a new collectible `PluginLoadContext` are created, and the old context is only unloaded cooperatively. On **10.11**, an exception from any `StopAsync` cancels a pending restart; 12 guards against this.
- F5. Each plugin folder gets its own `PluginLoadContext`. If `meta.json` has a non-empty `assemblies` list, **only those DLLs** are loaded. Otherwise every `*.dll` in the folder is loaded.
- F6. `IEventConsumer<T>` implementations are **not** found by scanning; they must be registered in DI. Events are published **inline**, inside the login or install code path. The DI services are resolved **outside** EventManager's try/catch (`Jellyfin.Server.Implementations/Events/EventManager.cs`), so a consumer constructor that throws breaks the publisher.
- F7. Compatibility checks only `targetAbi <= server version`; there is no lower bound. A 12.x server will pick a 10.11 build if its version number is higher. The official repo uses version ranges that don't overlap (Webhook 21.x for 10.11, 22.x for 12).
- F8. On a 10.11→12 upgrade the server **still loads the old 10.11 build** (its targetAbi ≤ 12.2) until the plugin update task replaces it.
- F9. Plugin config: `BasePlugin<T>` uses `XmlSerializer` (no `Dictionary<,>`, no interface-typed members). The file is `plugins/configurations/{AssemblyName}.xml`. `LoadConfiguration` **swallows load errors and saves a fresh default config over the file.** `POST Plugins/{id}/Configuration` **replaces the whole object** and fires `ConfigurationChanged`.
- F10. `BasePlugin.DataFolderPath` points at `plugins/{AssemblyName}`. It is not the install folder and is not created automatically, so we don't use it.
- F11. Plugin ASP.NET controllers are discovered automatically. The admin policy is `Policies.RequiresElevation` (`MediaBrowser.Common/Api/Policies.cs`), the same in both versions.

**Events**
- F12. `ILibraryManager.ItemAdded` fires **before any metadata provider runs or any image is downloaded**. It also fires for containers (Series, Season, MusicAlbum, folders) and for `IItemByName` entities: Person, Genre, Studio, Year, MusicArtist. Persons are re-saved on every refresh, which floods the event. On 12 it also fires for TMDB **virtual** episodes and seasons, and for stacked parts.
- F13. Metadata is ready when `ItemUpdated` arrives with `item.DateLastRefreshed > DateTime.MinValue`. That date stays `MinValue` if a provider failed.
- F14. `ILibraryManager.IsScanRunning` is true only during the full "Scan Media Library" task. A refresh of a single library doesn't set it.
- F15. Failed logins (`AuthenticationRequestEventArgs`) are published **only** when an existing user enters a wrong password. Unknown usernames and disabled or locked accounts throw before the event is published. The payload has Username, App, DeviceName, DeviceId and RemoteEndPoint.
- F16. Successful logins (`AuthenticationResultEventArgs`) carry the user and the session (client, device, IP). Created, deleted, locked-out and password-changed users fire their own events. Creating a user with a password fires Created **and then** PasswordChanged.
- F17. Plugin events: `PluginInstalled`, `PluginUpdated` (comes with a changelog), `PluginUninstalled`, `InstallationFailed`.
- F18. `TaskCompletionEventArgs` and `PendingRestartEventArgs` are **never published** through IEventManager. The working sources are the C# events `ITaskManager.TaskCompleted` and `IServerApplicationHost.HasPendingRestartChanged`.
- F19. Jellyfin has **no server update check**. Release tags look like `v10.11.11` and `v12.2`.
- F20. Images: `BaseItem.GetImageInfo(ImageType.Primary, 0)` → `ItemImageInfo.Path` / `IsLocalFile`. A remote stub can be downloaded with `ILibraryManager.ConvertImageToLocal`.
- F21. There is no canonical public server URL, so the admin has to enter it. The web deep link is `{base}/web/#/details?id={id:N}&serverId={SystemId}` in both versions.

**Web client (identical in both versions)**
- F22. 10.11 and 12 have **one** MUI dashboard, which hosts classic plugin pages through the legacy view container. The plugin-page code path is byte-identical between the two versions, and nothing newer replaces it.
- F23. The page HTML must contain the literal `data-role="page"` (double quotes). `data-controller="__plugin/<name>.js"` loads an ES module whose default export is constructed with `(view, params)`.
- F24. `globalize.translateHtml` rewrites **every** `${...}` in the page HTML, inline scripts included. JS loaded through `data-controller` is not touched.
- F25. Guaranteed custom elements: `emby-input`, `emby-checkbox`, `emby-select`, `emby-button`, `emby-linkbutton`. `emby-textarea`, `emby-collapse` and `emby-toggle` are **not** guaranteed.
- F26. Up to 3 cached views stay in the DOM; returning to a cached view fires only the show events. Events: `viewshow`/`pageshow`, `viewbeforehide`, `viewhide`, `viewdestroy`.
- F27. `GET /web/ConfigurationPage` is **anonymous**, so pages must never embed secrets. 12 disables legacy auth headers, so pages must use `ApiClient.fetch`/`ajax` (`Authorization: MediaBrowser Token=...`).
- F28. `MenuIcon` is shown only on 12, and `MenuSection` is ignored. 10.11 needs the class `type-interior` to get the dashboard theme.

**Telegram / Telegram.Bot**
- F29. `my_chat_member` is delivered by default. Always send an explicit `allowed_updates`. Updates are kept for **24 h**.
- F30. Three kinds of 409: a webhook is active, "terminated by other getUpdates request", and "terminated by setWebhook request".
- F31. `KeyboardButtonRequestChat` works only in private chats with the bot. It can add the bot to the chosen chat with the requested admin rights (`bot_administrator_rights`, plus `user_administrator_rights` that must be a superset; `bot_is_member` for groups). The bot then receives a `chat_shared` message.
- F32. A message that has (or ever had) a reply keyboard **cannot be edited**. Only inline-keyboard messages can be edited.
- F33. The Bot API has no method to list forum topics. Topic IDs come from messages sent in the topic, from `forum_topic_created`, or from `createForumTopic` (which needs `can_manage_topics`).
- F34. Limits: text 4096 and caption 1024 **code points after entity parsing**, photo upload ≤ 10 MB, `sendMediaGroup` has **no** reply_markup. Rate guidance: about 1 msg/s per chat, 20 msg/min per group, about 30 msg/s overall.
- F35. A URL button whose host has no dot (`localhost`, a Docker service name) **makes the whole send fail**. A bad `<a href>` in the text is silently dropped.
- F36. Bots may edit their own messages with no time limit (deletion is limited to 48 h).
- F37. Telegram.Bot: `ReceiveAsync` has **no backoff** after polling errors. It **returns silently** if an `OperationCanceledException` escapes a handler. It retries 429 inside every call (default `RetryThreshold` 60 s × `RetryCount` 3). The long-poll timeout is taken from `HttpClient.Timeout`, so that must stay above 50 s.
- F38. Bot API 10.0 (2026-05-08) added `BotAccessSettings`: a bot can be restricted to its owner plus up to 10 users. **A bot cannot read its own access settings or find out who its owner is.** Only a manager bot can, for bots it manages.
- F39. The token is part of every request URL. It must never be logged.
- F40. Scriban ≥ 7.2.6 fixes the 2026 advisories (including a sandbox escape and crashes from stack overflow). It is **not** a security boundary for reflected objects, so templates only get plain data.

---

## 5. Architecture

### 5.1 Identity (fixed forever)
| Item | Value |
|---|---|
| Plugin name | `TgJelly` |
| GUID | `e547b901-0dc4-42ae-8999-3871dd141270` |
| Assembly / DLL | `Jellyfin.Plugin.TgJelly` / `Jellyfin.Plugin.TgJelly.dll` |
| Config XML | `plugins/configurations/Jellyfin.Plugin.TgJelly.xml` (name comes from the DLL name, F9) |
| Data folder | `plugins/configurations/TgJelly/` (`secrets.json`, `state.json`, `settings.bak.json`, `state.lock`) |
| Dashboard pages | `tgjelly` (HTML), `tgjelly.js` (controller) |
| API route prefix | `/TgJelly` |

Changing the name, GUID or DLL name after the first release would lose every user's configuration.

### 5.2 Data flow
```
Jellyfin ──► Intake adapters ──► [bounded queue] ──► Router ──► Aggregator ──► Renderer ──► Outbox ──► Telegram
                                                                                             ▲
Dashboard page ──► TgJelly API ──┐                                                           │
                                 ├──► SettingsStore ◄──── Bot (long poll, flows, screens) ◄──┘
Telegram (private chats / groups) ┘        │
                                     StateStore (state.json) · SecretsStore (secrets.json)
```

### 5.3 Units
| Unit | Responsibility | Depends on |
|---|---|---|
| `Plugin` | `BasePlugin<PluginConfiguration>`, `IHasWebPages`. Exposes `Instance`. | Jellyfin |
| `PluginServiceRegistrator` | Registers every service, consumer and hosted service explicitly (F6). | — |
| `CompatGuard` | Compares the major version this build was compiled for (10 or 12) with `IServerApplicationHost.ApplicationVersion.Major`. On a mismatch, intake and the bot stay off and Status reports it (F8). | Jellyfin |
| **Intake** | Adapters: event consumers (auth, user, plugin events), `LibraryWatcher` (ItemAdded/ItemUpdated), `TaskWatcher` (`ITaskManager.TaskCompleted`), `RestartWatcher` (`HasPendingRestartChanged`), `UpdateChecker` (GitHub). Each maps its event to an immutable `NotificationEvent` and writes it to the **bounded intake queue**. O(1), never throws, no I/O. | Jellyfin |
| **MediaReadiness** | Item-type filter, the pending set, the readiness signal, timeouts (§7.4). Produces `MediaReady` events. | Jellyfin (reads items only) |
| **Router** | For each event, finds the destinations whose rules match (event key, library filter, enabled, healthy). | — (pure) |
| **Aggregator** | Batching and coalescing per destination (§7.4, §7.5), digests, late-arrival edits. | — (pure, with `TimeProvider`) |
| **Renderer** | Default layouts, toggles, Scriban templates, Telegram-HTML escaping, validation and truncation, `PosterPicker`, `LinkBuilder`. | Jellyfin (images, item lookup) |
| **Outbox** | Send queues per destination, pacing, retries, the error classifier, fallbacks, destination health, edits of sent messages. | `ITelegramSender` |
| `ITelegramSender` | A thin wrapper over Telegram.Bot that calls methods with **named arguments** (parameter order changes between minor versions). The only file that calls Telegram.Bot's send and edit methods. | Telegram.Bot |
| **Bot** | `BotSupervisor` (client lifecycle and polling, §9.2), `UpdateRouter`, `FlowEngine`, screens (§6.3). | Telegram.Bot, SettingsStore, StateStore |
| **SettingsStore** | The only way to change settings: lock → clone → change → `Revision++` → `UpdateConfiguration` → write `settings.bak.json`. Raises `SettingsChanged`. | Jellyfin config |
| **SecretsStore** | Atomic read and write of `secrets.json` (mode 600 on Unix). | disk |
| **StateStore** | Runtime state (§10.3). Atomic, saved at most once per second, size-capped. | disk |
| **TgJellyController** | Admin-only REST API for the dashboard (§11.2). | stores, Bot, Outbox |
| **Web** | `tgjelly.html` plus the `tgjelly.js` ES module (§11.1). | ApiClient |
| **Strings** | `Strings/en.json`, an embedded string table used by the bot, the layouts and the web page. | — |

### 5.4 Rules that apply everywhere
1. Only Intake, MediaReadiness and Renderer use Jellyfin APIs. Only Outbox/`ITelegramSender` and Bot use Telegram.Bot. Router and Aggregator are pure and are tested with a fake clock.
2. Every background service returns from `StartAsync` at once (`BackgroundService.ExecuteAsync`), wraps all work in a top-level catch-all with a supervised restart and backoff, honors its stopping token, and **never throws from `StopAsync`** (F3, F4).
3. Consumer and watcher constructors only take the singleton intake queue writer, plus `ILogger`. Nothing in them can fail to resolve (F6).
4. No static mutable state; a restart creates fresh instances (F4).
5. Nothing is sent before `IServerApplicationHost.CoreStartupHasCompleted` is true.
6. The places where 10.11 and 12 differ stay behind `#if JF12` in a single `Compat` file (e.g. `Video.PrimaryVersionId`: `string` on 10.11, `Guid?` on 12).

### 5.5 How the roadmap fits
- **Playback:** a new Intake adapter (`ISessionManager` playback events, identical in both versions) plus layouts. The Outbox already supports editing sent messages, which is what a "now playing" message needs.
- **Interactive features:** new commands and screens on `UpdateRouter`/`FlowEngine`. Long-running actions go to a work queue so they never block polling.

---

## 6. Setup and configuration UX

### 6.1 Dashboard, first run
A single card: **"Connect a Telegram bot"** with `[Create a new bot]` `[I already have a bot]`.

- **Create a new bot:** numbered steps, each with a link:
  1. Open **@BotFather** (`https://t.me/BotFather`) and send `/newbot`. Choose a display name and a username ending in `bot`.
  2. In BotFather's settings for the new bot, **restrict access to yourself** and to anyone else who should manage Jellyfin (up to 10 people) (F38). *The exact BotFather menu labels are checked on a real bot during implementation, see §15.*
  3. Copy the token BotFather gives you and paste it below.
- **I already have a bot:** a token field, with two notes:
  - "Restrict access in BotFather if you haven't, because anyone who can open the bot can manage notifications."
  - "Don't use this bot in another program at the same time."
- **Save** → `POST /TgJelly/Token` checks the format (Telegram.Bot's constructor throws `ArgumentException`), calls `getMe` and checks `getWebhookInfo`. If a webhook is set, it calls `deleteWebhook(drop_pending_updates=false)` and shows a warning. It then configures the bot:
  - `setMyCommands` for the all-private-chats scope (`/menu`, `/status`, `/test`, `/help`), with an empty default scope,
  - `setMyDescription` ("Jellyfin notifications from *server name*") and `setMyShortDescription`,
  - `setMyDefaultAdministratorRights`: for channels post + edit messages; for groups nothing.

  Only then is the token stored in `secrets.json`.
- Result: **"Connected as @MyJellyBot"** and a **[Open @MyJellyBot]** button linking to `https://t.me/<username>?start=setup`.
- If `getMe` fails with a network error, a **"Can't reach Telegram?"** panel opens with the proxy settings (§9.1). Otherwise proxy settings stay under Advanced.

### 6.2 Bot onboarding: the main flow, native picker only
Triggered by `/start setup`, `/start add`, a plain `/start` when there are no destinations yet, or **Add destination** in `/menu`.

1. The bot sends a **prompt message with a reply keyboard**: "Where should notifications go?"
   - `📢 Choose a channel`: `request_chat` with `chat_is_channel=true`, `bot_administrator_rights` = {can_post_messages, can_edit_messages}, `user_administrator_rights` the same (a superset of the bot's rights), `request_title=true`.
   - `👥 Choose a group`: `request_chat` with `chat_is_channel=false`, `bot_is_member=true`, `request_title=true`.
   - `💬 Right here`: a plain text button; the destination is this private chat.
   - `Cancel`.

   Each picker button gets a fresh `request_id`, which is saved in the chat's flow state.
2. On `chat_shared` (with a matching `request_id`) the bot removes the keyboard (`ReplyKeyboardRemove`) and deletes the prompt message. It then checks `getChatMember(chat, botId)`, retrying up to 3 times over 5 s because `my_chat_member` may arrive late:
   - **Channel:** status `administrator` with `can_post_messages`.
   - **Group:** status `member`, `administrator` or `restricted` with `can_send_messages`.
   - **On failure** the bot explains what is missing and offers `[Try again] [Advanced options]` (§6.5).
3. **Forum supergroup** (`is_forum`): `[General topic] [Create a "Jellyfin" topic]`. The second button appears only if the bot has `can_manage_topics`; otherwise the bot says it needs that permission and points to `/here` under Advanced. The destination is (chat, thread). General uses no thread ID; see §15.
4. **"What should I send there?"** `[Everything] [New media] [Admin alerts] [Custom…]` (presets in §7.2). *Custom…* opens the event switches screen.
5. The bot sends a test message to the new destination and edits the wizard message to "✅ Done: *Title* will receive *groups*. `[Add another place] [Menu]`".

Steps 3–5 happen in **one inline-keyboard "wizard" message** that gets edited as the flow moves on. The reply-keyboard prompt in step 1 is a separate, throwaway message (F32).

### 6.3 Bot menus (`/menu`)
```
🏠 Menu
├─ 📬 Destinations (n) ─► <destination> ─► Events · Libraries · Silent groups · Send test · Pause/Resume · Remove
│                       └─ (detected chats waiting for setup, §6.5)
├─ ➕ Add destination ─► §6.2
├─ ⚙️ Settings ─► Message style (toggles §8.4) · Update checks (on/off, mode) · Public URL (show/clear)
└─ 🩺 Status ─► connection, build/compat, health of each destination, pending media, outbox size, last errors
```
- `/status` and `/test` (pick a destination) are shortcuts. `/help` gives a short description and a link to the dashboard page.
- **Dashboard only:** entering and changing the token, proxy settings, custom Bot API URL, typing in the public URL, batching values, templates, adding a chat by ID, diagnostics, disconnect. These all involve typing long values or have security consequences.

### 6.4 Access model
- **Anyone who can open the bot's private chat can manage it** (D5). Telegram enforces the restriction set in BotFather (F38). The plugin can't check whether the bot is restricted, so:
  - both setup paths push the restriction (§6.1),
  - the dashboard's Bot section always shows a one-line reminder: "Anyone who can open @bot can manage notifications. Keep it restricted in BotFather."
- **In groups and channels** the bot ignores every command except `/here` (§6.5). `/here` is accepted only while a topic-selection flow is pending for that chat (started from a private chat within the last 10 min), and only from a user whose `getChatMember` status in that chat is `creator` or `administrator`.
- **"Managers"**: the private chats that used `/menu` or the onboarding in the last 90 days. They are kept in state and used only to tell people about destination problems (§9.4). Tracking them adds no setup step.

### 6.5 Fallbacks (Advanced; offered only when the main flow fails)
- **Bot added by hand** (the Telegram way). A `my_chat_member` update (left/kicked → member/administrator) for a chat that isn't a destination creates a **detected chat** in state. It shows up in Destinations as "Detected: *Title*, set it up?" in the bot and the dashboard. It receives nothing until it is set up.
- **Add-to-group / add-to-channel links** on the dashboard (Advanced): `https://t.me/<bot>?startgroup&admin=` (no rights) and `https://t.me/<bot>?startchannel&admin=post_messages+edit_messages`. The chat then appears as a detected chat.
- **Add chat by ID** on the dashboard (Advanced): a chat ID plus an optional topic ID. It is checked with `getChat`/`getChatMember` before saving.
- **`/here@<bot>`** sent inside a forum topic binds that topic. The conditions are in §6.4.

### 6.6 Dashboard page after setup
Sections, top to bottom:
1. **Status:** connection (Connected as @x / Token rejected / Another app is using this bot / Telegram unreachable, check proxy / Wrong build for this server), last error, counts (pending media, outbox, dropped), restored-from-backup notices, flagged templates.
2. **Bot:** @username, the access reminder (§6.4), `[Change token]`, `[Disconnect bot]` (clears secrets and state after a confirmation).
3. **Destinations:** a list with health badges (OK / Needs permissions / Topic missing / Lost / Paused). Each one has an editor (event switches, library multi-select, silent groups) plus `[Send test]` `[Remove]` (with a "Bot leaves the chat" checkbox → `leaveChat`). The `[Add destination]` button opens `https://t.me/<bot>?start=add`, because the native picker only exists in Telegram.
4. **Messages:** public URL (§8.3, pre-filled with a suggestion marked "verify"), the toggles from §8.4.
5. **Advanced** (collapsed with `<details>`):
   - proxy and custom Bot API URL (§9.1),
   - the batching values from §7.4,
   - update-check mode,
   - the fallbacks from §6.5,
   - custom templates (§8.5),
   - `[Copy diagnostics]`.

The dashboard and the bot can make every change in §6.3 that isn't marked dashboard-only.

---

## 7. Events

### 7.1 Catalog (event keys are stable strings, used in config and templates)
| Key | Source | Notes |
|---|---|---|
| `media.movie` | ItemAdded → readiness | `Movie` |
| `media.episodes` | ItemAdded → readiness | `Episode`, batched by series and season |
| `media.album` | ItemAdded → readiness | `MusicAlbum`, or `Audio` grouped by album. Audio without an album is not announced. |
| `media.musicvideo` | ItemAdded → readiness | `MusicVideo` |
| `media.book` | ItemAdded → readiness | `Book`, `AudioBook` |
| `security.login_failed` | `IEventConsumer<AuthenticationRequestEventArgs>` | Wrong password for an existing user (F15). The label says exactly that. |
| `security.lockout` | `IEventConsumer<UserLockedOutEventArgs>` | Merged with the failed attempt that caused it (§7.5) |
| `security.login_succeeded` | `IEventConsumer<AuthenticationResultEventArgs>` | Deduplicated per (user, device) for 12 h |
| `security.user_created` | `IEventConsumer<UserCreatedEventArgs>` | Merged with a PasswordChanged that follows within 10 s |
| `security.user_deleted` | `IEventConsumer<UserDeletedEventArgs>` | |
| `security.password_changed` | `IEventConsumer<UserPasswordChangedEventArgs>` | Also fires for resets |
| `server.update_available` | `UpdateChecker` | §7.6 |
| `server.plugin_installed` | `IEventConsumer<PluginInstalledEventArgs>` | |
| `server.plugin_updated` | `IEventConsumer<PluginUpdatedEventArgs>` | Changelog included |
| `server.plugin_uninstalled` | `IEventConsumer<PluginUninstalledEventArgs>` | |
| `server.plugin_install_failed` | `IEventConsumer<InstallationFailedEventArgs>` | Error message included |
| `server.task_failed` | `ITaskManager.TaskCompleted` | Only `Result.Status == Failed`. Cancelled and Aborted are ignored (F18). |
| `server.restart_pending` | `HasPendingRestartChanged` → true | Lists the plugin events from the previous 10 min as reasons |

Groups: `media.*` = **media**, `security.*` = **security**, `server.*` = **server**.

### 7.2 Presets
| Preset | Keys |
|---|---|
| Everything | all keys |
| New media | `media.*` |
| Admin alerts | `security.*` except `security.login_succeeded`, plus `server.*` |
| Custom | individual switches, starting from the preset that was last selected |

### 7.3 Library filter
For each destination: `LibraryIds` (empty means all). An item matches when one of `ILibraryManager.GetCollectionFolders(item)` has an ID in the list. The choices offered come from `ILibraryManager.GetVirtualFolders()` (`VirtualFolderInfo.ItemId`). The filter applies only to `media.*`.

### 7.4 New-media pipeline
1. **Filter** (inside the ItemAdded handler, O(1)). Keep only `Movie`, `Episode`, `MusicAlbum`, `Audio`, `MusicVideo`, `Book`, `AudioBook`. Drop the item if it:
   - has `IsVirtualItem`,
   - has a non-empty `OwnerId` or a non-null `ExtraType`,
   - is a Video with a primary version set (an alternate version; via `Compat`),
   - is an `IItemByName`, or
   - is anything else (F12).

   `Series` and `Season` adds are recorded only as context, so a batch can be labeled "🆕 New series".
2. **Pending.** Item IDs go into the pending set, which is saved to state, with `firstSeen`.
3. **Ready** when `ItemUpdated` arrives for a pending ID and `DateLastRefreshed > MinValue` (F13), or when `ReadyTimeoutMinutes` (default **15**) has passed since `firstSeen`. A timed-out item is rendered with whatever metadata it has. If an item has disappeared (`GetItemById` returns null), it is dropped quietly. Other items are not affected.
4. **Routing → groups.** Group keys per destination:
   - a movie, music video or book is its own group,
   - episodes are grouped by (series, season),
   - audio by album; a `MusicAlbum` item joins its own album's group.
5. **Batch windows** per group:
   - a group is sent when no new item has joined it for `QuietWindowSeconds` (default **120**), or `MaxHoldMinutes` (default **30**) after its first item, whichever comes first,
   - a full library scan finishing (`TaskCompleted` for key `RefreshLibrary`) sends all open groups right away.
6. **Digest.** If more than `DigestThreshold` (default **10**) media groups are sent to one destination within one quiet window, they become **one text digest** instead. It is grouped by library ("*Movies*: A (2024), B (2023) …; *Shows*: X S02 (8 episodes) …"), truncated to fit 4096 with "…and N more". This also covers adding a whole new library.
7. **Late arrivals.** If a group with the same key was sent to the same destination less than `LateEditHours` ago (default **6**), the new items are **added to the message already sent by editing it** (F36). The edit makes no sound. If the edit fails, a new message is sent (§9.4).
8. **Upgrades.** Unless `AnnounceUpgrades` is on (default **off**), an item whose identity was announced in the last 30 days is suppressed. Identity is a provider ID (IMDb/TMDb/TVDb/MusicBrainz); for episodes without one, it is series ID + SxxEyy. This catches files replaced by Sonarr or Radarr.

### 7.5 Coalescing security events
- `login_failed`: key (username, remote IP). The first attempt sends a message. Further attempts within 10 min **edit that message** ("×5, last at 14:03"). A `lockout` for the same user within that window edits it again to add "🔒 Account locked". If only `lockout` is enabled for a destination, it gets a standalone lockout message.
- `user_created` followed by `password_changed` for the same user within 10 s → only the creation message.
- `login_succeeded`: suppressed if the same (user ID, device ID) was announced in the last 12 h.

### 7.6 Update checker
- First check 5 min after startup, then every **12 h**. It calls `GET https://api.github.com/repos/jellyfin/jellyfin/releases?per_page=20` with a `User-Agent: TgJelly/<version>` header, without authentication, through a plain `HttpClient` (`IHttpClientFactory`).
- Draft and prerelease entries are skipped. Tags are parsed leniently (`v12.2` → 12.2.0.0) and compared with `ApplicationVersion` after both are normalized to four parts.
- `Mode`: **SameMajor** (default; only newer releases with the same major version) or **AnyNewer**.
- Each version is announced once (`state.updateCheck.lastNotifiedVersion`).
- 403 or 429 replies (GitHub rate limits) or network errors: log at debug level and try again at the next interval.
- Message: "⬆️ Jellyfin **12.3** is available (running 12.2)" with a `[Release notes]` button linking to the release's GitHub page.

---

## 8. Messages

### 8.1 Default layouts (Telegram HTML; all strings from `Strings/en.json`)
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
- **Album:** album cover. **Artist — Album (Year)**, track count, library.
- **Digest:** text only, as in §7.4 step 6.
- **Security:** text only. `🔐` plus a bold headline, then `key: value` lines: user, IP, client · device, attempts or time.
- **Server:** text only. `⚙️`/`⬆️` plus a headline, then `key: value` lines. Plugin changelogs and task errors go in `<blockquote expandable>`.
- **Delayed delivery:** if a message goes out more than 5 min after its event (an outage, say), a `⏱ 14:02` line with the original time is added.

### 8.2 Posters (D8)
- **Which image:**
  - Movie, music video, book: its Primary image.
  - Episodes: Season Primary → Series Primary → Episode Primary.
  - Album: the album's Primary image → the first track's album art.
- If `ItemImageInfo.IsLocalFile` is false, call `ConvertImageToLocal` first (F20). If the file is missing, larger than **10 MB**, or not `.jpg/.jpeg/.png/.webp`, send the **text-only** version of the layout instead.
- Upload the original file with `InputFile.FromStream(stream, "poster" + ext)` and close the stream afterwards. If Telegram rejects the photo (for example because of its dimensions), resend as text only.
- Albums (`sendMediaGroup`) are not used in v1: they can't have buttons (F34).

### 8.3 Buttons and links
- **Open in Jellyfin:** `{PublicUrl}/web/#/details?id={item.Id:N}&serverId={SystemId}` (F21). Shown only if `PublicUrl` is valid: an absolute `http(s)` URL whose host contains a `.` or is an IPv6 literal (F35). Otherwise it is left out, and the Messages section explains why.
  - The PublicUrl field is pre-filled with a suggestion: an `external=`/`all=` entry from `NetworkConfiguration.PublishedServerUriBySubnet`, otherwise `GetSmartApiUrl(IPAddress.Parse("8.8.8.8"))`. It is labeled "verify".
  - If PublicUrl already contains Jellyfin's BaseUrl, it is not appended again.
- **IMDb** (`https://www.imdb.com/title/{imdb}/`) and **TMDb** (`https://www.themoviedb.org/{movie|tv}/{id}`) buttons for movies and shows, when the provider IDs exist. They don't need a public URL.
- **Release notes** for `server.update_available`.

### 8.4 Toggles (global, in Settings or the dashboard's Messages section)
`ShowPoster`, `ShowOverview`, `ShowRatingGenres`, `ShowOpenInJellyfin`, `ShowProviderLinks` (all default **on**), `AnnounceUpgrades` (default **off**).

For each destination: `SilentGroups` (media / security / server → `disable_notification=true`), default none.

### 8.5 Custom templates (Advanced, hidden by default)
- **Engine:** **Scriban 7.5.1** (pinned; ≥ 7.2.6 required, F40).
- **Scope:** for each event key, an enabled template **replaces only the text or caption**. The poster and buttons still follow the toggles.
- **Model:** a flat `ScriptObject` built from plain values only (strings, numbers, booleans, dates, arrays of such). It never contains Jellyfin or Telegram objects. Field names are documented, in snake_case, per event key. Examples:
  - `media.movie`: `title`, `year`, `overview`, `rating`, `runtime_minutes`, `genres[]`, `library`, `imdb_id`, `tmdb_id`, `jellyfin_url`.
  - `media.episodes`: `series`, `season`, `count`, `episodes[]` {`number`, `title`}, `new_series`, `library`.
  - `security.login_failed`: `user`, `ip`, `client`, `device`, `attempts`, `locked`.
  - `server.task_failed`: `task`, `error`.

  The complete field list lives in `docs/templates.md`, which is written during implementation, and is also shown in the editor.
- **Escaping:** a `TelegramTemplateContext` (a `TemplateContext` subclass) HTML-escapes every expression result in `Write(SourceSpan, object)`. Values wrapped by the `raw` function are written as-is. Literal template text is left untouched.
- **Limits per render:** `LoopLimit=1000`, `RecursiveLimit=20`, `ObjectRecursionLimit=20`, output limit 16 KB (`ScriptLimitBehavior.Throw`), `RegexTimeOut=1 s`, and a `CancellationToken` with a **2 s** timeout. Parsed templates are cached by (event key, hash of the text).
- **Validate** (`POST /TgJelly/Templates/Validate`):
  - parse errors (all of them, 1-based line and column),
  - a trial render with a sample model,
  - a check against Telegram's HTML tag whitelist (`b strong i em u ins s strike del span.tg-spoiler tg-spoiler a code pre blockquote[expandable] tg-emoji tg-time`) with balanced tags,
  - a length check against 1024 (caption) or 4096 (text),
  - and a preview.
- **At send time:** any template error → the default layout is used, and the template is flagged in Status with the error.

### 8.6 Formatting rules
- Every value inserted by a default layout goes through `HtmlText.Escape`.
- Length is measured with `HtmlText.PlainLength` (Telegram.Bot ≥ 22.10.3.3). The overview is shortened first, then lists ("+N more"), and finally `HtmlText.Truncate`.
- The send fallback chain is in §9.4.

---

## 9. Telegram integration

### 9.1 Client
- **HttpClient:** a `SocketsHttpHandler` built from the settings (`PooledConnectionLifetime = 3 min`) with `Timeout = 100 s` (it must stay > 50 s, F37). Not created through `IHttpClientFactory`, to keep its logging handlers away (F39).
- **Proxy `Mode`:**
  - **System** (default): .NET's default, which follows `HTTP_PROXY`/`HTTPS_PROXY`/`ALL_PROXY`, so Docker environment variables work,
  - **None**: `UseProxy=false`,
  - **Http**: `WebProxy(url)`,
  - **Socks5**: `WebProxy("socks5://…")`.

  The proxy username and password live in `secrets.json`.
- **Custom Bot API URL** (`ApiBaseUrl`, Advanced, hidden): root URL only (Telegram.Bot drops any path). It is used by the integration tests (§14) and by local Bot API servers. Its warning explains that the cloud `logOut` it requires is never done automatically.
- `TelegramBotClientOptions(token, baseUrl) { RetryThreshold = 0 }`. The Outbox owns all retries (F37).
- Changing the token, proxy or base URL cancels polling, rebuilds the client and restarts the supervisor.

### 9.2 Polling supervisor
- `BotSupervisor : BackgroundService`. It waits for `CoreStartupHasCompleted` and `CompatGuard` OK, plus a token, then loops:
  1. Build the client. Call `getMe` (401 → state **TokenRejected**: stop, and wait for a new token).
  2. Call `getWebhookInfo`. If a URL is set, delete the webhook and record a warning.
  3. `await client.ReceiveAsync(handler, new ReceiverOptions { AllowedUpdates = [Message, CallbackQuery, MyChatMember], Offset = state.lastUpdateId + 1 }, ct)` (F29).
  4. If `ReceiveAsync` returns while `ct` is not cancelled (F37), back off and go to step 1.
- **Error handler** (`HandleErrorAsync`): **never throws**. It classifies the error and awaits a backoff:
  - network errors: exponential, 1 s → 60 s, reset after a successful poll,
  - 409 "terminated by other getUpdates request": 30 s → 5 min, state **ConflictingInstance**,
  - 401: stop.
- **Update handler:** stays fast and wraps everything in try/catch, so an exception is logged and the update skipped. It records `lastUpdateId`. Processed update IDs are kept in an LRU (1000 entries), so a second delivery has no effect. Each callback query is answered at once, ignoring "query is too old". Work that may take long goes to the bot work queue.
- **Startup re-check:** for every destination, `getChat` + `getChatMember(botId)` → health (Telegram keeps updates for only 24 h, F29).
- **Shutdown:** cancel the long poll, flush state, then make one best-effort `getUpdates(offset=last+1, limit=1, timeout=0)` within 2 s to confirm the offset. All of it is wrapped so that nothing is thrown.

### 9.3 Outbox and pacing
- One FIFO queue per destination, with entries saved to state. Each entry holds the rendered message (text, caption, poster reference, buttons, silent flag) plus a reference to the event, so it can be rendered again for edits.
- **Pacing:** at least 1 s between sends to the same chat, at most 20 per minute per group or channel, at most 25 per second overall. A `retry_after` from a 429 pauses that chat's queue for that long.
- **Limits:** 500 entries per destination. When full, the oldest **media** entries are dropped first; security and server entries stay until their TTL. TTL is 24 h. Dropped entries are counted in Status.
- **Network errors:** retry with backoff until the TTL runs out.
- **Edits:** `editMessageCaption` for photo messages, `editMessageText` for text messages, using the sent-message index (§10.3). "message is not modified" counts as success.

### 9.4 Error classifier and reactions
Errors are matched on `ApiRequestException.ErrorCode` plus a lower-case substring of the description. Descriptions may change, so matching is defensive.

| Match | Destination health / action |
|---|---|
| 403 `kicked`, `not a member`, `blocked`, `deactivated`, `was deleted`; 400 `chat not found` | **Lost** → pause the destination |
| 400 `need administrator rights`, `not enough rights`, `have no write access` | **Needs permissions** → pause |
| 400 `message thread not found` | **Topic missing** → pause |
| 400 + `Parameters.MigrateToChatId` | rewrite `ChatId`, save, resend once |
| 400 `can't parse entities` | resend as plain text (`HtmlText.ToPlain`); flag the template if one was used |
| 400 `button url` / `BUTTON_URL_INVALID` | resend without buttons; flag the public URL |
| 400 photo errors (`photo`, `PHOTO_`, `IMAGE_PROCESS_FAILED`, `wrong file`) | resend the text-only layout |
| 400 `message is too long` / `caption is too long` | shorten more and resend once |
| 400 `message to edit not found` / `can't be edited` | send as a new message |
| 429 | wait `retry_after` |
| `RequestException` (transport) | retry with backoff; Status "Telegram unreachable, check proxy" |
| anything else | log, drop the entry, show it in Status as "last error" |

- **Health changes and who hears about them:**
  - becoming **Lost**, **Needs permissions** or **Topic missing** is reported **once per new problem**, on the dashboard and as a private message to recent managers (§6.4),
  - a successful send or re-check sets the destination back to **OK**,
  - `my_chat_member` with a new status of left or kicked → **Lost**,
  - promotion or rights restored → a re-check.

---

## 10. Persistence

### 10.1 Settings: `PluginConfiguration` (XML; fields only ever added, never renamed or retyped)
```
SchemaVersion: int = 1
Revision: long = 0
PublicUrl: string = ""
ApiBaseUrl: string = ""
Proxy: ProxySettings { Mode: ProxyMode = System; Url: string = "" }
Message: MessageOptions { ShowPoster=true; ShowOverview=true; ShowRatingGenres=true;
                          ShowOpenInJellyfin=true; ShowProviderLinks=true; AnnounceUpgrades=false }
Batching: BatchingOptions { QuietWindowSeconds=120; MaxHoldMinutes=30; LateEditHours=6;
                            DigestThreshold=10; ReadyTimeoutMinutes=15 }
UpdateCheck: UpdateCheckOptions { Enabled=true; Mode: UpdateCheckMode = SameMajor }
Destinations: Destination[] = []
  Destination { Id: string (8 chars, [a-z0-9]); ChatId: long; ThreadId: int = 0 (0 = none);
                Title: string; Kind: ChatKind (Private|Group|Supergroup|Channel);
                Enabled: bool = true; Events: string[]; LibraryIds: string[] (empty = all);
                SilentGroups: string[] }
Templates: TemplateOverride[] = []
  TemplateOverride { EventKey: string; Enabled: bool = false; Text: string = "" }
```
Arrays are initialized to `Array.Empty<T>()` (XmlSerializer appends to pre-filled lists, F9). Enums are serialized by name.

### 10.2 Secrets: `secrets.json`
`{ "botToken": "", "proxyUsername": "", "proxyPassword": "" }`. Written to a temp file and moved into place, mode 600 on Unix. **No API ever returns these values.** The dashboard sees `hasToken`, `botUsername` and `hasProxyPassword`.

### 10.3 Runtime state: `state.json`
| Section | Contents | Cap |
|---|---|---|
| `telegram` | `lastUpdateId`, `botId`, `botUsername` | — |
| `handledUpdates` | LRU of update IDs | 1000 |
| `flows` | per chat: screen, params, `wizardMessageId`, `promptMessageId`, `pickerRequestIds`, `updatedAt` (expires after 24 h) | 200 chats |
| `managers` | private chat ID → `lastUsedAt` (90 days) | 50 |
| `detectedChats` | chat ID, title, kind, detected at | 50 |
| `pendingMedia` | item ID, `firstSeen` | 20 000 |
| `openBatches` | destination, group key, item IDs, `firstAt`, `lastAt` | 5 000 |
| `sentMessages` | (destination, group key) → message ID, kind (photo or text), `sentAt`, event reference | entries older than 24 h are removed |
| `dedupe` | upgrade identities (30 days), logins (12 h), failed-login counters (10 min), user-created markers (10 s) | 20 000 / 2 000 / 1 000 / 100 |
| `outbox` | §9.3 | 500 per destination |
| `health` | per destination: status, last error, `at`, `notified` | — |
| `updateCheck` | `lastCheckedAt`, `lastNotifiedVersion` | — |
| `recentPluginEvents` | for restart reasons (10 min) | 50 |

### 10.4 Writes and recovery
- **Settings:** only through SettingsStore (§5.3).
  - Every mutating API call carries `expectedRevision`; a mismatch returns HTTP 409 with the current revision, and the page reloads and says so.
  - Bot screens always read the current settings before rendering.
  - After each successful save, `settings.bak.json` gets a copy.
  - **Startup check:** if `Configuration` has no destinations, `Revision == 0`, and `settings.bak.json` has a higher revision, the backup is restored and Status reports it. This covers the reset described in F9.
- **Plugin.UpdateConfiguration override:** calls that don't come from SettingsStore (for example someone POSTing to the generic endpoint) are accepted. `Revision` is bumped and `SettingsChanged` raised, so every component re-reads.
- **State:** written to a temp file and moved into place, at most once per second while it is changing, plus a final flush in `StopAsync`.
  - A file that fails to parse is renamed `state.bad-<utc>.json`, the plugin starts empty, and Status shows a warning.
  - `state.lock` is held with `FileShare.None` from start to stop, so only one instance writes at a time (F4). An instance that can't get the lock within 10 s doesn't write state, and Status reports that.
- **Disconnect bot:** clears `secrets.json` and `state.json`. Settings are kept.
- **Uninstall:** files stay on disk, as Jellyfin does with its own config.

---

## 11. Dashboard

### 11.1 Page (`Web/tgjelly.html` + `Web/tgjelly.js`)
- `GetPages()` returns `tgjelly` (HTML, `EnableInMainMenu=true`, `DisplayName="Telegram"`, `MenuIcon="send"`), **listed first**, then `tgjelly.js`. `MenuSection` is not set (F28).
- **HTML shell:** `<div data-role="page" id="tgjConfigPage" class="page type-interior pluginConfigurationPage" data-controller="__plugin/tgjelly.js">…</div>`. All markup, `<template>` and `<style>` elements go inside this div.
  - **No inline scripts and no `${` anywhere in the HTML** (F24).
  - Only `emby-input`, `emby-checkbox`, `emby-select`, `emby-button` and `emby-linkbutton` are used (F25). Native `<details>/<summary>` for collapsible sections, and a plain `<textarea>` with scoped CSS for templates.
  - Explicit labels with the upgrade classes already applied.
  - Colors only from the stock classes or `var(--jf-palette-*)`.
- **Controller:** `export default function TgJellyPage(view, params)`.
  - DOM listeners are bound once, in the constructor.
  - `viewshow` is idempotent: load the strings, status, settings and destinations, then render.
  - Elements are queried only through `view.querySelector`, and every ID and class is prefixed `tgj-`.
  - State lives in the controller closure, not at module level.
- **Status polling:** every 3 s while the page is visible. It stops on `viewbeforehide`, `viewhide` and `viewdestroy`, when `!view.isConnected` or `view.classList.contains('hide')`, and pauses while `document.hidden` (F26).
- **Network:** only `ApiClient.fetch` / `ApiClient.ajax` with `dataType:'json'`, against same-origin `/TgJelly/*` (F27). The page never calls `api.telegram.org`.
- **Strings:** from `GET /TgJelly/Strings`. Labels are rendered from JS. The language is taken from `document.documentElement.lang`, falling back to `en`.
- **QR code:** none. "Open @bot" links are plain `https://t.me/...` links.

### 11.2 API (`[ApiController] [Route("TgJelly")] [Authorize(Policy = Policies.RequiresElevation)]`)
Every response is JSON (no 204). Every mutating request carries `expectedRevision`, except token, test and validate calls.

| Method & path | Purpose |
|---|---|
| `GET Status` | connection state, bot username, compat result, revision, counts, last errors, flags (template, public URL, backup restored) |
| `POST Token` `{token}` | validate and store (§6.1) → Status |
| `DELETE Token` | disconnect: clear secrets and state |
| `GET Settings` / `PUT Settings` | public URL (with suggestion), message toggles, batching, update check, proxy (without password), API base URL |
| `PUT Proxy` `{mode, url, username?, password?}` | proxy settings, including secrets |
| `GET Destinations` | destinations plus detected chats plus health |
| `POST Destinations` `{chatId, threadId?}` | Advanced: add by ID (§6.5); checked with getChat/getChatMember |
| `PUT Destinations/{id}` | events, libraries, silent groups, enabled |
| `DELETE Destinations/{id}?leave=bool` | remove, optionally `leaveChat` |
| `POST Destinations/{id}/Test` | send a test message |
| `GET Libraries` | `GetVirtualFolders()` → `{id, name, collectionType}` |
| `GET Templates` / `PUT Templates/{eventKey}` | template overrides plus the field docs for each key |
| `POST Templates/Validate` `{eventKey, text}` | §8.5 |
| `GET Links` | the `t.me` links: open bot, `?start=add`, startgroup, startchannel |
| `GET Strings` | `Strings/en.json` |
| `GET Diagnostics` | plain-text report with secrets redacted |

---

## 12. Reliability and safety (summary of rules spread across the spec)
- **Host safety:** §5.4 rules 2–5. Intake handlers are O(1), catch everything and never block. The intake queue is bounded at **10 000** events; when it is full, the oldest media events are dropped first and the count shows in Status.
- **CompatGuard:** §5.3. The dashboard banner reads "This TgJelly build is for Jellyfin 10.11. Update the plugin (Dashboard → Plugins) to get the Jellyfin 12 build."
- **Logging:** `ILogger<T>`. Request URLs are never logged. Everything TgJelly logs, and the diagnostics report, has secrets redacted (`\d{5,}:[A-Za-z0-9_-]{30,}` → `<token>`). Proxy passwords are never logged.
- **Templates:** limits from §8.5, a data-only model, and a pinned Scriban version.
- **Delivery semantics:** at least once, with in-process de-duplication (handled updates, dedupe caches). After a crash or restart, a duplicate notification is acceptable; lost security events are not, within the 24 h TTL.

---

## 13. Build, compatibility and release

### 13.1 Repository layout
```
Jellyfin.Plugin.TgJelly.sln
src/Jellyfin.Plugin.TgJelly/        plugin (TargetFrameworks net9.0;net10.0)
  Configuration/ State/ Intake/ Pipeline/ Rendering/ Telegram/ Bot/ Api/ Web/ Strings/ Compat/
tests/Jellyfin.Plugin.TgJelly.Tests/          unit tests (both TFMs)
tests/Jellyfin.Plugin.TgJelly.IntegrationTests/   docker smoke tests + fake Bot API server
build/package.(sh|py)                zip + meta.json + manifest generation
.github/workflows/ci.yml, release.yml
docs/templates.md, docs/superpowers/specs/…
README.md (setup with screenshots, BotFather restriction, troubleshooting: proxy, CSP/Rocket Loader, 409)
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
- Each track compiles against the **lowest** Jellyfin package version of its line (10.11.0 and 12.0.0), so its targetAbi is accurate.
- The 10.11 track gets the same features until a separate decision freezes it.

### 13.4 Packaging (`build/package`)
- **For each framework:**
  1. `dotnet publish -c Release -f <tfm>`.
  2. Copy **exactly** `Jellyfin.Plugin.TgJelly.dll`, `Telegram.Bot.dll` and `Scriban.dll`, and write a `meta.json` (guid, name, overview, description, owner `MrSnowball-dev`, category `Notifications`, version, targetAbi, changelog, timestamp, `imagePath`, and `assemblies` = those three DLLs, F5).
  3. Zip and compute the MD5.
- **Manifest:** `manifest.json` is one `PackageInfo` entry with the GUID from §5.1. New version entries are appended (with sourceUrl, checksum, timestamp, targetAbi, changelog); older ones are kept.
- **CI fails** if a zip contains anything other than the three DLLs and `meta.json`, or if `tgjelly.html` lacks `data-role="page"` or contains `${`.

### 13.5 CI and distribution
- **`ci.yml`** (push and PR): install .NET 9 and 10, build both frameworks, run unit tests on both, the web checks from §13.4, package, then the integration smoke tests (§14.3).
- **`release.yml`** (tag `v*`): everything above, then create a GitHub Release with both zips and commit the updated `manifest.json` to the `gh-pages` branch.
- **Users add:** `https://mrsnowball-dev.github.io/tgjelly_plugin/manifest.json`.
- ⚠️ **Prerequisite:** the repo is currently **private**. On a free plan, Pages and public release downloads need it to be public, so the owner makes it public before the first release.
- **Local development:** this machine currently has no .NET SDK and no container runtime. The implementation plan starts by installing the .NET 9 and 10 SDKs and Docker or Podman. A `build/dev-deploy` script copies a build into a local Jellyfin container's `plugins/TgJelly_<ver>/` and restarts the container.

---

## 14. Testing

### 14.1 Unit tests (xUnit, run on both TFMs, `Microsoft.Extensions.TimeProvider.Testing` for time)
- Router: event keys, presets, library filter, paused or unhealthy destinations.
- Aggregator: quiet window, max hold, scan-end flush, digest threshold, late-arrival edits, coalescing (§7.5).
- MediaReadiness: filter table (each excluded kind), readiness signal, timeout, deleted items, persistence across a simulated restart.
- Dedupe: upgrade identities, login dedupe windows.
- Renderer: **golden-file** tests per layout. Escaping of `& < >`; emoji and other non-BMP titles at exactly 1024 and 4096 code points; the truncation order; poster-picker fallbacks (stubs, missing, > 10 MB, unsupported type).
- Telegram-HTML validator: allowed and forbidden tags, unbalanced tags.
- Scriban wrapper: auto-escaping vs `raw`, each limit (loop, recursion, output, timeout), error positions (1-based), the sample model for every event key.
- Error classifier: table-driven, using the **real description strings** in §9.4 and research notes TG-16, TG-20 and TG-7.
- Outbox: pacing (per chat, per group, overall), `retry_after`, migrate-to-supergroup, every fallback in §9.4, TTL, the cap and drop order. Uses a fake `ITelegramSender`.
- SettingsStore: revision conflicts, backup and restore, XML round-trip, old XML (missing fields) loading with defaults.
- StateStore: atomic write, recovery from a corrupt file, caps, the lock file.
- FlowEngine: callback data ≤ 64 bytes, versioned format, expired or stale menus, `request_id` correlation, `/here` conditions.
- UpdateChecker: `v12.2` / `v10.11.11` parsing, prereleases and drafts, SameMajor vs AnyNewer, 403/429 handling, announce once.
- LinkBuilder and PublicUrl validation: no-dot hosts rejected, IPv6 accepted, BaseUrl not doubled.

### 14.2 Telegram fixtures
Recorded Bot API JSON for `my_chat_member` (channel, group, kicked), `chat_shared`, `forum_topic_created`, callback queries, and every error response type. They are parsed with Telegram.Bot and passed to `UpdateRouter` and the error classifier.

### 14.3 Integration smoke tests (CI; Docker)
- **Matrix:** `jellyfin/jellyfin:10.11.0` and `10.11.11` with the 1.x zip; `12.0` and `12.2` with the 2.x zip.
- **Steps:**
  1. Mount the zip's contents into the container's plugin folder and start the container.
  2. Complete the startup wizard through `/Startup/*` and authenticate.
  3. Assert:
     - `GET /Plugins` lists TgJelly as **Active**,
     - `GET /web/ConfigurationPage?name=tgjelly` contains `data-role="page"`,
     - `GET /web/ConfigurationPage?name=tgjelly.js` has a JavaScript content type,
     - `GET /TgJelly/Status` returns JSON.
- **End to end:**
  1. Start a **fake Bot API server**: a small HTTP stub that answers `getMe`, `getWebhookInfo`, `getUpdates`, `getChat`, `getChatMember`, `sendMessage`, `sendPhoto` and `editMessage*`, and records every request.
  2. Set `ApiBaseUrl` to the stub, add a destination by ID, and copy a sample media file into a library.
  3. Trigger a library scan.
  4. Assert that a `sendPhoto` or `sendMessage` for that item reaches the stub within the timeout.

### 14.4 Manual pre-release checklist (real Telegram)
- Create a new bot and restrict it in BotFather. Recording the exact menu path goes into the README.
- Native picker on Android, iOS and Desktop: channel, group, forum (General topic and a created topic).
- Posters of different sizes and formats, including a > 10 MB file falling back to text.
- Buttons with a LAN IP public URL and with a public HTTPS URL.
- A second app polling with the same token (409 handling); SOCKS5 and HTTP proxies.
- Kicking the bot, removing its rights, upgrading a group to a supergroup.
- An in-process server restart (no duplicate polling), and a 10.11 → 12 server upgrade (CompatGuard, then auto-update).

### 14.5 Process
Implementation follows test-driven development: write a failing test first for every unit in §14.1.

---

## 15. Open questions to settle during implementation
1. BotFather's exact current menu path for restricting access (for the setup guide and README).
2. Does Telegram reject `http://` or private-IP URLs in inline buttons (`BUTTON_URL_INVALID`)? Only TDLib's "no dot" rule is verified. The result decides whether the PublicUrl validation also rejects private IPs.
3. What does `request_chat` look like on current clients, and does the user see a confirmation before the bot is added as an admin? (Handled either way by the check in §6.2 step 2.)
4. How should the General topic of a forum be targeted: no `message_thread_id`, or thread 1?
5. Which exact `Content-Type` does 10.11 serve for embedded `.js`? (Checked by the smoke test.)
6. Are `emby-textarea`, `emby-collapse` and `emby-toggle` really unregistered on a deep link? We don't use them either way; this only confirms the decision.
7. Does a net9.0/net10.0 build that references Telegram.Bot put `Microsoft.Extensions.Options.dll` or `System.Text.Json.dll` in `bin`? It doesn't matter for the zip (whitelist), but the CI check will show it.
