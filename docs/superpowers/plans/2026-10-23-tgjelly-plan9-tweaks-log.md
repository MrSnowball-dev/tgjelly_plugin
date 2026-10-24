# TgJelly plan 9: dashboard tweaks, per-message options and the Log — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Carry out the owner's v0.4 tweak list, make message switches per message, merge Templates into Messages with an exact Telegram preview, click-to-insert tables and a hover link, and add a Log section. No error the owner sees may be vague.

**Architecture:**
- **Server (C#):**
  - `MessageOptions` becomes a per-event-key map, with a migration from the global value.
  - Update checks become three flags.
  - A new `EventLog` (its own file, outside settings) records one row per message per destination. The pipeline and outbox delivery write to it. `GET Log` reads it.
  - `Templates/Validate` gains source-mapped preview pieces.
- **Page:** `tgjelly.js` (3,720 lines) is split. The new Messages section moves into `tgjelly-messages.js`, the Telegram preview into `tgjelly-preview.js`, and the Log into `tgjelly-log.js`. Each is an embedded `PluginPageInfo` page, like `tgjelly-ui.js`.
- **Labels:** one `label()` component replaces every status word.

**Tech Stack:**
- C# (net9.0 and net10.0), xunit.v3;
- plain ES modules and CSS, with no framework or bundler;
- Node 22 `node --test` with `tests/web/fake-view.mjs` and the resolve hook `tests/web/jellyfin-resolve.mjs`;
- Python smoke tests (`tests/integration/smoke.py`).

**Spec:** `docs/superpowers/specs/2026-10-23-tgjelly-dashboard-tweaks-design.md` is binding. Everything else comes from `2026-10-22-tgjelly-dashboard-redesign-design.md`, `2026-10-21-tgjelly-templates-design.md` and the v1 spec.

## Global Constraints

- **No framework and no Node build in the plugin build.**
  - New page files are embedded resources and registered `PluginPageInfo` pages, imported as `./configurationpage?name=<file>.js`.
  - Add every new `Web/*.js` to ci.yml's `node --check` list and to smoke's `WEB_ASSETS`.
- **Scoped CSS:** every rule sits under `#tgjelly-root`. Colours come from `--tgj-*` tokens over `--jf-palette-*`, except the Telegram preview's `tgj-tg-*` classes (Ruling R3 of plan 8).
- **No browser dialogs.** No decorative symbols in dashboard markup or `web.*` strings: no ●, ✓, ✔, ✅, ⚠, ❌, ✗, 🟢 or 🔴. The bot keeps its icons.
  - Exception (design §6.4): the Telegram preview's URL-button mark ↗ is part of imitating Telegram. It lives in CSS `content`, never in strings.
- **Auto-save is unchanged** (one page queue, one `Revision`, cue timing):
  - switches and selects save at once;
  - text and number fields save 800 ms after the last keystroke, or on blur;
  - the public URL and the proxy fields save on blur or Enter;
  - templates save 1000 ms after the last keystroke, only without errors and never empty.
- **Labels** (design §3) have the tones `good`, `progress`, `neutral`, `attention`, `bad` and `accent`.
  - The cue texts are "Saved", "Saving…" and "Not saved". The reason follows as plain error text.
  - "Saved" fades after 2000 ms. "Saving…" shows after 300 ms in flight.
- **Typing is never interrupted:** inserting, hovering and re-rendering never replace a focused field or one with a pending change.
- **The upgrade changes nothing visible.**
  - Every media message's switches start with the v0.4 global values.
  - Security and Server Poster starts off.
  - `SameMajor` becomes fixes + new releases; `AnyNewer` becomes all three.
- **Errors** say what happened and what to do. Never "unknown error", "something went wrong", or "try again" on its own.
- **Wording:** new strings go in `Strings/en.json`, as `web.*` for the dashboard and the existing prefixes for the bot. Every bot screen explains each button, one line per button.
- **Commands:**
  - prefix every `dotnet` command with `export PATH="$HOME/.dotnet:$PATH"`;
  - full .NET suite: `dotnet test --solution Jellyfin.Plugin.TgJelly.slnx -c Release` (warnings are errors);
  - web: `node --check` on every `src/Jellyfin.Plugin.TgJelly/Web/*.js`, then `node --import ./tests/web/jellyfin-resolve.mjs --test 'tests/web/*.test.mjs'`, whose output must stay free of stray logs;
  - Python: `python3 -m unittest discover -s build -p 'test_*.py'` and `python3 -m unittest discover -s tests/integration -p 'test_*.py'`.
- **Commits:** `git -c user.name="Daniil" -c user.email=igatorr19@gmail.com commit`, with every message ending in `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **A v0.4 config with Poster off and Overview on, loaded by this build:** every media message has Poster off and Overview on, Security and Server have Poster off, and the first Settings save writes the per-message map without dropping anything. Test: `Migration_CopiesGlobalTogglesToEveryMediaMessage` (Task 5).
2. **A Telegram send that fails, then succeeds on retry, while the Log page is open:** the row goes Waiting → Sent late with the reason, with no duplicate row. A send dropped after its retries run out shows Failed with Telegram's exact description. Tests: `Log_RetryThenSuccess_OneRowSentLate` and `Log_Dropped_FailedWithTelegramDescription` (Task 12).
3. **Clicking a field cell while the template has a pending debounced save:** the text is inserted at the caret, the one save that follows includes it, and focus stays in the textarea. Test: `fieldClick_insertsAtCaret_andSavesOnce` (Task 9).
4. **Hovering a preview piece after the template text has changed but before the new preview arrives:** no highlight points at stale offsets. Pieces carry the text they were computed for, and a mismatch disables hover until the next preview. Test: `hover_ignoresPiecesForOlderText` (Task 10).
5. **The Log file is missing, corrupt, or written by a newer version:** sending carries on, the Log starts empty, and the page says it started empty. Test: `EventLog_CorruptFile_StartsEmpty_SendingUnaffected` (Task 12).

---

### Task 1: Labels everywhere

**Files:**
- Modify: `Web/tgjelly-ui.js` (add `label`; `stateLabel` delegates to it or is replaced), `Web/tgjelly.css` (`.tgj-label` with tone modifiers; `.tgj-badge` and `.tgj-cue` restyled through it), `Web/tgjelly.js`. The users are: the header connection (`connectionLabel`/`connectionHtml`), destination health (`healthLabels`), the template badge, and `paintCue`.
- Test: `tests/web/ui.test.mjs`, `tests/web/css.test.mjs`, `tests/web/tgjelly.test.mjs`

**Interfaces:**
- Produces: `label(text: string, tone: 'good'|'progress'|'neutral'|'attention'|'bad'|'accent', attrs?: object) => string`. It returns `<span class="tgj-label tgj-label--<tone>">…</span>` with the text escaped. Every later task uses it for any state word.
- **The cue:** `paintCue` renders the state word as a label (`good` for Saved, `progress` for Saving…, `bad` for Not saved), followed by a `.tgj-cue-reason` span for the reason.

- [ ] **Step 1: Write the failing tests:**
  - `label_rendersOutlinedPillWithTone`: the class names, the escaping, and an unknown tone falls back to `neutral`;
  - `css_labelTonesUsePaletteTokens`;
  - `cue_saved_isGoodLabel`;
  - `cue_notSaved_labelPlusReason`;
  - `header_connected_isGoodLabel`;
  - `templateBadge_edited_isAccentLabel`.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement.** The style follows today's `.tgj-badge`: a 1px outline in the tone's ring colour, the tone's text colour, no fill, and a 999px radius.
- [ ] **Step 4: Run the web suite and confirm it passes.**
- [ ] **Step 5: Commit.** `feat: every state is an outlined label`

### Task 2: Overview and header

**Files:**
- Modify: `Web/tgjelly.js` (`renderShell`, `renderStatus`, `renderOverviewSummary`, and the Overview host list), `Strings/en.json`, `Web/tgjelly.css`
- Test: `tests/web/tgjelly.test.mjs`

**Interfaces:**
- `renderShell(S, { active, connection, counts, version })`: the header becomes `<h1>TgJelly</h1>` + `label('v' + version, 'neutral')` + the connection label. The version comes from `status.Version`.
- `renderStatus` no longer outputs the version line.
- `renderOverviewSummary` and its host are deleted, along with `web.summary.*` and their `StringKeys` entries.
- Access renders as `notice({ kind: 'info', … })` inside the Overview's status card, with today's text.

- [ ] **Step 1: Write the failing tests:**
  - `header_showsVersionLabelNextToTitle`;
  - `status_hasNoVersionLine`;
  - `overview_hasNoAtAGlance`;
  - `overview_accessIsInfoNotice`: Overview has no card titled with `web.access.*` and has a `.tgj-notice--info` with the access text.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement. Remove the unused strings.**
- [ ] **Step 4: Run the web suite and the .NET string-key tests, and confirm they pass.**
- [ ] **Step 5: Commit.** `feat: version in the header; Overview without At a glance; Access as a notice`

### Task 3: Destinations — health only when wrong, grouped events

**Files:**
- Modify: `Web/tgjelly.js` (`healthLabels`, `renderDestinations`, `renderEditor`), `Strings/en.json`, `Web/tgjelly.css`
- Test: `tests/web/tgjelly.test.mjs`

**Interfaces:**
- `healthLabels` returns `''` for `Ok`.
  - The other states use `label()`: `attention` for PhotosBlocked; `bad` for NeedsPermissions, TopicMissing and Lost.
- `renderEditor` renders one sub-card per `EventCatalog` group (`media`, `security`, `server`), titled by the group.
  - Each switch uses a new dashboard label `web.event.<eventKey>` without the group prefix, e.g. `web.event.media.movie` = "Movies".
  - The bot's `dest.event.*` stays as it is.
- Add `web.event.*` for every event key the catalog sends, checked against `/Catalog`. Add a `StringKeys` entry for each.

- [ ] **Step 1: Write the failing tests:**
  - `health_ok_rendersNothing` (row and editor);
  - `health_lost_isBadLabel`;
  - `editor_eventsGroupedInThreeCards`;
  - `editor_eventLabels_haveNoGroupPrefix`: no switch label starts with "Media:", "Security:" or "Server:";
  - `botEventStrings_unchanged`: `dest.event.media.movie` still reads "Media: Movies".
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the web and .NET suites, and confirm they pass.**
- [ ] **Step 5: Commit.** `feat: destinations show only problems; events grouped without prefixes`

### Task 4: Disconnect can remove destinations

**Files:**
- Modify: `Api/BotController.cs` (`DELETE Token`), the disconnect service it calls (`Bot/BotTokenService.cs`), `Bot/BotStateReset.cs`, `Web/tgjelly.js` (the disconnect `inlineConfirm`), `Strings/en.json`
- Test: `tests/Jellyfin.Plugin.TgJelly.Tests/Api/BotControllerTests.cs` (or the existing token tests file), `tests/Jellyfin.Plugin.TgJelly.Tests/Bot/BotStateResetTests.cs`, `tests/web/tgjelly.test.mjs`

**Interfaces:**
- `DELETE Token?removeDestinations=true|false`, default false.
  - **When true:** in the same settings change as the disconnect, `Destinations` becomes empty and the per-destination state is cleared: `Health`, `SentMessages` for those destinations and the Outbox. The returned `Revision` is new.
  - **When false:** today's behaviour.
- **Page:**
  - The confirmation shows a switch, `web.bot.disconnect.removeDestinations` = "Also remove all destinations". It is off by default. This switch is local to the confirmation and is not auto-saved.
  - Under the switch is one line saying what each choice keeps.
  - Confirm sends the flag.

- [ ] **Step 1: Write the failing tests:**
  - `Disconnect_Default_KeepsDestinations`;
  - `Disconnect_RemoveDestinations_ClearsDestinationsAndTheirState`;
  - `Disconnect_RemoveDestinations_BumpsRevision`;
  - web: `disconnectConfirm_hasRemoveSwitch_offByDefault`;
  - web: `disconnectConfirm_switchOn_sendsFlag`.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run both suites and confirm they pass.**
- [ ] **Step 5: Commit.** `feat: disconnect offers to remove all destinations`

### Task 5: Per-message options, server side

**Files:**
- Create: `Configuration/MessageSwitches.cs`
- Modify:
  - `Configuration/MessageOptions.cs`;
  - `Configuration/PluginConfiguration.cs` (`Normalize`, plus the migration);
  - `Api/SettingsDto.cs` and `Api/SettingsUpdateRequest.cs`;
  - `Configuration/SettingsValidator.cs`;
  - `Rendering/MediaLayouts.cs`;
  - `Templates/TemplateModelBuilder.cs` (`ApplyToggles`);
  - `Templates/TemplateSample.cs`;
  - `Pipeline/MediaReadiness.cs`;
  - `Status/DiagnosticsReport.cs`;
  - `Bot/Screens/SettingsScreens.cs`, a compile-fix only: it reads the movie's switches until Task 7 adds the picker.
- Test: `tests/…/Configuration/MessageOptionsMigrationTests.cs` (new), plus the existing tests of each consumer.

**Interfaces:**
- `MessageSwitches { bool ShowPoster; bool ShowOverview; bool ShowRatingGenres; bool ShowOpenInJellyfin; bool ShowProviderLinks; }`
- **`MessageOptions`:**
  - `MessageOptions.PerMessage: MessageSwitchesEntry[]`, where each entry is `{ string EventKey; MessageSwitches Switches }`. It is serialised as an array, for XML.
  - `MessageOptions.AnnounceUpgrades` stays one global bool (builder ruling for design §11; the reason is that `MediaReadiness` decides whether an upgrade event exists before any message is picked).
  - `MessageOptions.For(string eventKey) => MessageSwitches` returns the entry, or that key's default. Defaults: media keys use the v0.4 defaults (all five true). Security and server keys have every switch false.
  - The legacy global booleans stay as `[Obsolete]` migration-only properties, read once.
- **Migration** in `PluginConfiguration.MigrateMessageOptions()`, called from `Normalize`:
  - When `PerMessage` is empty and the legacy fields exist, each media key, including `media.digest`, copies the legacy values.
  - Security and server keys get `ShowPoster = false`.
  - The migration is idempotent.
- **Which switches apply:** `MessageSwitches.Applicable(string eventKey) => IReadOnlyList<string>`.
  - Media keys get all five.
  - `media.digest` gets exactly the switches its rendering honours today. Read the digest path in `MediaLayouts`, `NotificationRenderer` and `TemplateModelBuilder`, and pin the list in a test. A switch the digest ignores is not listed.
  - Security and server keys get `ShowPoster` only.
- **Settings API:** the `Message` field carries `PerMessage` and `AnnounceUpgrades`.
  - A PUT may send a single entry: `Message.PerMessage` entries merge by `EventKey`, and switches not sent keep their value. This keeps saves partial (plan 8, F1).
  - An unknown event key is a 400 with a clear message.
- **Consumers:**
  - `MediaLayouts.Render` reads `config.Message.For(model.EventKey)`;
  - `TemplateModelBuilder.ApplyToggles` takes `MessageSwitches`;
  - `TemplateSample` and `DiagnosticsReport` print per-message values. Diagnostics shows one line per key that differs from its default.

- [ ] **Step 1: Write the failing tests:**
  - `Migration_CopiesGlobalTogglesToEveryMediaMessage` (Review Focus 1);
  - `Migration_SecurityAndServerPosterOff`;
  - `Migration_Idempotent`;
  - `For_UnknownKey_ReturnsDefaults`;
  - `Put_SingleEntry_MergesAndKeepsOthers`;
  - `Put_UnknownEventKey_400WithMessage`;
  - `MediaLayouts_PosterOffForEpisodesOnly_MovieStillHasPoster`;
  - `ModelBuilder_OverviewOffForMovie_EmptiesOverviewForMovieOnly`.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the full .NET suite and confirm it passes.**
- [ ] **Step 5: Commit.** `feat: message switches per message, migrated from the global ones`

### Task 6: Logo as the poster for Security and Server messages

**Files:**
- Modify:
  - `Jellyfin.Plugin.TgJelly.csproj`: embed `build/logo.png` as `Jellyfin.Plugin.TgJelly.Logo.png`, via `<EmbeddedResource Include="../../build/logo.png" LogicalName="Jellyfin.Plugin.TgJelly.Logo.png" />`. Adjust the relative path to the real layout.
  - `Rendering/AlertLayouts.cs`;
  - `State/DataPaths.cs`;
  - `Templates/TemplateSample.cs`.
- Test: `tests/…/Rendering/AlertLayoutsTests.cs`, `tests/…/Templates/TemplateSampleTests.cs`

**Interfaces:**
- `DataPaths.LogoPath`: the logo is written once, at startup, to `<plugin data>/logo.png`. It is rewritten when its size differs. `OutboxDelivery` already uploads from a path and caches the file_id by `PosterCacheKey`.
- **`AlertLayouts.Render`:** when `config.Message.For(model.EventKey).ShowPoster` is true, it sets:
  - `PosterPath = DataPaths.LogoPath`;
  - `PosterCacheKey = "tgjelly-logo:" + <logo sha256 first 16 hex>`.
- **The template preview:** `Validate` reports `Poster = true` for those messages, and the sample poster is the logo.

- [ ] **Step 1: Write the failing tests:**
  - `Alert_PosterOn_UsesLogoPathAndStableCacheKey`;
  - `Alert_PosterOff_NoPoster`;
  - `Logo_WrittenOnce_RewrittenWhenSizeDiffers`;
  - `Validate_SecurityPosterOn_ReportsPoster`.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the full .NET suite and confirm it passes.**
- [ ] **Step 5: Commit.** `feat: Security and Server messages can carry the TgJelly logo`

### Task 7: The bot's Message style picks a message first; update checks become three switches

**Files:**
- Modify:
  - `Bot/Screens/SettingsScreens.cs`;
  - `Configuration/UpdateCheckOptions.cs`, `Configuration/UpdateCheckMode.cs` (kept for the migration only);
  - `Updates/UpdateChecker.cs` (the version filter);
  - `Strings/en.json`: the bot's `settings.messages.*` and `settings.updates.*`, plus the fix for the "plugin releases" wording;
  - `Api/SettingsDto.cs` and `Api/SettingsUpdateRequest.cs`.
- Test: `tests/…/Bot/SettingsScreensTests.cs`, `tests/…/Updates/UpdateCheckerTests.cs`, `tests/…/Configuration/UpdateCheckMigrationTests.cs` (new)

**Interfaces:**
- **`UpdateCheckOptions`:** `bool Fixes`, `bool NewReleases` and `bool NewMajor`, plus `Enabled`, which is unchanged.
  - **Migration:**
    - `Mode=SameMajor` gives `Fixes = NewReleases = true` and `NewMajor = false`.
    - `Mode=AnyNewer` gives all three true.
    - A config with no Mode uses the SameMajor defaults.
  - `Mode` is read once and then ignored.
- **`UpdateChecker`:** let the running version be `a.b.c` and the candidate `x.y.z`.
  - **Fixes:** `x==a && y==b && z>c`.
  - **NewReleases:** `x==a && y>b`.
  - **NewMajor:** `x>a`.
  - A candidate qualifies when any enabled kind matches. All three off means nothing is announced.
- **The bot's Message style screen:** Section buttons (Media, Security, Server), then Message buttons, then that message's applicable switches (`MessageSwitches.Applicable`).
  - There is a Back button at each level.
  - Every screen explains each button, one line per button.
  - Toggles save through `SettingsStore` as today.
- **The bot's Update checks screen:** three toggles, each with an explanation line. Its hint says it checks **Jellyfin** releases.

- [ ] **Step 1: Write the failing tests:**
  - `UpdateMigration_SameMajor_FixesAndNewReleases`;
  - `UpdateMigration_AnyNewer_AllThree`;
  - `Checker_FixesOnly_Ignores10_12` (running 10.11.1, candidates 10.11.2 and 10.12.0: only 10.11.2 qualifies);
  - `Checker_NewMajorOnly_Announces12`;
  - `Checker_AllOff_AnnouncesNothing`;
  - `MessageStyle_SectionThenMessageThenSwitches`;
  - `MessageStyle_SecurityShowsOnlyPoster`;
  - `UpdatesScreen_SaysJellyfinReleases`;
  - `UpdatesScreen_ThreeTogglesExplained`.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the full .NET suite and confirm it passes.**
- [ ] **Step 5: Commit.** `feat: per-message style in the bot; update checks by kind of release`

### Task 8: The Messages section (Templates merged in), with the split modules

**Files:**
- Create: `Web/tgjelly-messages.js`, the Messages section's render and controller pieces.
- Create: `Web/tgjelly-preview.js`. It takes over `previewTokens`, `previewHtml`, `previewButtons` and `renderTemplatePreview` from `tgjelly.js`, unchanged in this task, with re-exports kept so existing tests pass.
- Modify:
  - `Web/tgjelly.js`: `SECTIONS` becomes `['overview','destinations','messages','advanced','log']`, and Log is added in Task 13. Delete the templates section host.
  - `Web/tgjelly.css`, `Plugin.cs` (page registration), `.github/workflows/ci.yml`, `tests/integration/smoke.py` (`WEB_ASSETS`), `Strings/en.json`.
- Test: `tests/web/messages.test.mjs` (new), `tests/web/tgjelly.test.mjs` (moved template tests now import from the new module)

**Interfaces:**
- **The selection:**
  - `messageFromHash(hash) => string|null`, read from `tgjmsg=<eventKey>`;
  - `hashWithMessage(hash, key)`;
  - the default is the first media key.
- **`renderMessagesSection(state, S, ui)`**, top to bottom:
  - **Pickers:** a Section select (`media`, `security` or `server`) and a Message select (that section's keys from `TemplatesDto.Items`, plus Digest under media), with `label(Edited|Built-in)` beside them. The options carry no suffix.
  - **The "What it shows" card:** the applicable switches for the key, saving through the queue with key `msg:<eventKey>:<Switch>` and body `{ Message: { PerMessage: [{ EventKey, Switches: { <Switch>: v } }] } }`.
    - The **public URL** row is nested under `ShowOpenInJellyfin` and shown only while it's on. It keeps today's blur-or-Enter save and validation, and its hint says it applies to every message's button.
    - For media keys only, **Announce upgrades** is a switch row with the hint "applies to all media messages".
  - **The Template card:** today's editor (textarea, cue, length, errors, Send test, Reset), unchanged in behaviour.
  - **Preview:** the existing preview. It is sticky beside the cards on wide screens (an `@container` rule on `#tgjelly-root`) and below the template on narrow ones.
- The Batching card is removed from this section; Task 11 adds it to Advanced.

- [ ] **Step 1: Write the failing tests:**
  - `nav_hasNoTemplatesItem`;
  - `messages_pickers_sectionThenMessage`;
  - `messages_pickerOptions_haveNoEditedSuffix`;
  - `messages_selectionPersistsInHash`;
  - `messages_switches_areApplicableOnly` (Security shows Poster only);
  - `messages_switchSave_sendsSingleEntry`;
  - `publicUrl_onlyWhileOpenInJellyfinOn`;
  - `publicUrl_stillSavesOnBlurOnly`;
  - `messages_noBatchingCard`;
  - `newModules_registeredAndChecked`, where the C# `WebPagesTests` checks the three new pages are registered and smoke's asset list includes them.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement.** Move the code without changing its behaviour; the template-editor tests must pass unchanged apart from their imports.
- [ ] **Step 4: Run every suite and confirm it passes.**
- [ ] **Step 5: Commit.** `feat: one Messages section with per-message switches and the template`

### Task 9: Fields and Formatting tables with click-to-insert

**Files:**
- Modify: `Web/tgjelly-messages.js`, `Web/tgjelly.css`, `Strings/en.json`
- Test: `tests/web/messages.test.mjs`

**Interfaces:**
- **`renderFieldsTable(item, S)`:** columns Field, What it is and Example.
  - The example is `item.SampleFull[name]`, formatted as the preview would show it, or a muted dash when empty.
  - "optional" is shown as `label('optional','neutral')`.
- **`FORMATTING`:** a static list of `{ write, snippet, placeholder, descriptionKey, looksLikeHtml }`. It covers b, i, u, s, tg-spoiler, a, code, pre, blockquote, blockquote expandable, `{{ if x }}…{{ end }}`, `{{ plural n "a" "b" }}`, `raw`, and `{{-`/`-}}`.
  - The "Looks like" column uses `tgj-tg-` classes.
- **The first cell of each row is a `button` styled as a cell, with `data-tgj-insert`.**
  - **A field:** inserts `{{ name }}` at the caret.
  - **Formatting with a selection:** wraps the selection.
  - **Formatting without a selection:** inserts the snippet and selects its placeholder.
  - Insert through `document.execCommand('insertText')` when available, so undo works. Otherwise use `setRangeText`, then dispatch `input`.
  - Focus stays in the textarea.
- **Both tables collapse under a `<details>`-like toggle** (a button with `aria-expanded`), closed by default. Their state is kept in `localStorage` under `tgj.tables.fields` and `tgj.tables.formatting`, wrapped in try/catch.

- [ ] **Step 1: Write the failing tests:**
  - `fieldsTable_rowsWithDescriptionAndExample`;
  - `fieldClick_insertsAtCaret_andSavesOnce` (Review Focus 3);
  - `formatClick_wrapsSelection`;
  - `formatClick_noSelection_insertsAndSelectsPlaceholder`;
  - `tables_collapsedByDefault_rememberState`;
  - `tables_storageThrows_stillWork`;
  - `insertCell_isKeyboardActivatable`.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the web suite and confirm it passes.**
- [ ] **Step 5: Commit.** `feat: click a field or a format to insert it; expandable reference tables`

### Task 10: Exact Telegram preview and the hover link

**Files:**
- Modify:
  - `Templates/TemplateEngine.cs` and `Templates/TemplateRenderer.cs` (a preview-only marked render);
  - `Api/TemplateValidationDto.cs` (new fields);
  - `Api/TemplatesController.cs`;
  - `Web/tgjelly-preview.js`, `Web/tgjelly-messages.js`, `Web/tgjelly.css`.
- Test: `tests/…/Templates/TemplatePiecesTests.cs` (new), `tests/web/preview.test.mjs` (new), `tests/web/messages.test.mjs`

**Interfaces:**
- **Pieces:** `TemplateValidationDto.Pieces: IReadOnlyList<TemplatePieceDto>`, with `TemplatePieceDto { int Index; int Start; int Length; }`. These are source offsets into the request text, one per top-level statement of the parsed template.
  - A run of literal text, an output expression, and a whole `if…end` block are each one piece.
- **`TemplateValidationDto.MarkedHtml: string`:** the same preview HTML, with each piece's output delimited:
  - `U+E000` + the decimal index + `U+E001` before it;
  - `U+E002` after it.
  - It is produced by a second, preview-only render. The sent message, `Html`, `Length` and the fitting are computed exactly as today and never see the markers.
  - Approach: render each top-level statement through a Scriban `TemplateContext` whose output is captured per statement, or an equivalent. Whitespace trimming (`{{-`/`-}}`) must give byte-identical text to the unmarked render once the markers are removed. A test checks that.
- **`TemplateValidationDto.ForText: string`:** an echo of the request text, so the page can drop pieces for older text.
- **`previewHtml(markedHtml)`:** strips the markers while sanitising, and wraps each text run inside piece k in `<span data-tgj-piece="k">`. A piece made only of tags produces no span. The sanitiser's allowed-tag behaviour and tests are unchanged.
- **Hover:**
  - Hovering `[data-tgj-piece=k]` in the preview, or the matching source range in the template, adds `.tgj-piece-hl` to both sides.
  - On the template side, a mirror overlay behind a transparent textarea draws the highlight. The overlay never takes focus or the caret, and scrolls with the textarea.
  - Hover is disabled when `ForText !== textarea.value`.
- **Exact Telegram look** (design §6.4):
  - the bubble has a tail and the time at the bottom right;
  - a photo sits on top of the caption;
  - inline keys sit under the bubble, in the DTO's `Buttons` rows, with translucent fill;
  - URL keys get the ↗ mark via CSS `::after`;
  - spoiler, quote, link and code use Telegram's styles.

- [ ] **Step 1: Write the failing tests:**
  - `Pieces_TwoFieldsOneLine_AreSeparatePieces`;
  - `Pieces_IfBlock_IsOnePiece`;
  - `MarkedRender_StrippedEqualsUnmarked` (with `{{-` trimming);
  - `SentMessage_NeverHasMarkers`;
  - `previewHtml_markersBecomePieceSpans`;
  - `previewHtml_unknownTagsStillStripped`;
  - `hover_previewPiece_highlightsSourceRange`;
  - `hover_ignoresPiecesForOlderText` (Review Focus 4);
  - `hover_neverMovesCaretOrFocus`;
  - `preview_buttonsUnderBubbleInRows`;
  - `preview_hasTimeInBubble`.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run every suite and confirm it passes.**
- [ ] **Step 5: Commit.** `feat: Telegram-exact preview and a hover link between template and preview`

### Task 11: Advanced — proxy, update switches, Batching, Diagnostics

**Files:**
- Modify: `Web/tgjelly.js` (`renderProxyForm`, `renderUpdates`, `renderBatching` moved here, `renderDiagnostics`), `Strings/en.json`
- Test: `tests/web/tgjelly.test.mjs`

**Interfaces:**
- **Proxy:**
  - The Username and Password rows render only when `ProxyMode` is `Socks5` or `Http`. Use the real enum names from `ProxyMode.cs`.
  - Hiding them never sends a change.
  - A hint, `web.proxy.typesHint`, says only SOCKS5 and HTTP proxies work for bots, and that MTProto proxies are for Telegram apps.
- **Updates:** three switches, `UpdateCheck.Fixes`, `.NewReleases` and `.NewMajor`, each with an example line. The hint says all off means no update messages.
- **Batching:** the existing card, unchanged, placed after Updates.
- **Diagnostics:**
  - a `notice({ kind: 'info' })` built from what `DiagnosticsReport` actually writes. Read the code and list its sections in `web.diag.contains`.
  - It also says the token is never included, that the Log is not part of the report (its last errors already are), and why the report exists (`web.diag.why`).

- [ ] **Step 1: Write the failing tests:**
  - `proxy_noProxy_hidesCredentials`;
  - `proxy_socks5_showsCredentials`;
  - `proxy_hint_mentionsMtproto`;
  - `updates_threeSwitches_saveAtOnce`;
  - `advanced_hasBatchingCard`;
  - `diagnostics_hasInfoNoticeListingContents`.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the web suite and confirm it passes.**
- [ ] **Step 5: Commit.** `feat: Advanced shows proxy credentials only with a proxy; update kinds; Batching; Diagnostics explained`

### Task 12: The event log, server side

**Files:**
- Create: `Log/EventLog.cs`, `Log/LogEntry.cs`, `Log/LogResult.cs`, `Log/SkipReason.cs`, `Api/LogController.cs`, `Api/LogDto.cs`
- Modify:
  - `Pipeline/NotificationPipeline.cs`: records Skipped, Waiting and the hand-off to the outbox;
  - `Pipeline/Router.cs`: adds `Explain`;
  - `Pipeline/MediaReadiness.cs`: records items dropped for metadata;
  - `Delivery/OutboxDelivery.cs`: records Sent, attention details and Failed;
  - `State/Documents/OutboxEntry.cs`: `LogId`;
  - `PluginServiceRegistrator.cs`, `State/DataPaths.cs`.
- Test: `tests/…/Log/EventLogTests.cs`, `tests/…/Log/LogRecordingTests.cs`, `tests/…/Api/LogControllerTests.cs`

**Interfaces:**
- **`LogEntry`:** `{ string Id; DateTimeOffset At; DateTimeOffset UpdatedAt; string EventKey; string Title; string? DestinationId; string? DestinationTitle; LogResult Result; IReadOnlyList<string> Details; string? Telegram; int Tries; DateTimeOffset? RetryAt; string? Advice; }`
  - `Details` holds plain reasons, most serious first.
  - `Telegram` is Telegram's or the network's error, verbatim and redacted with `SecretRedactor`.
- **`LogResult`:** `Waiting`, `Skipped`, `Sent`, `SentLate`, `SentWithBuiltIn`, `SentWithoutPhoto`, `Failed`. The label priority (design §8.1) is Failed > SentWithBuiltIn > SentWithoutPhoto > SentLate.
- **`EventLog`:**
  - `Upsert(LogEntry)`;
  - `Query(LogQuery { bool ProblemsOnly; string? DestinationId; string? EventKey; DateTimeOffset? Before; int Limit = 100 }) => LogPage { IReadOnlyList<LogEntry> Items; DateTimeOffset? LastSentAt; DateTimeOffset? LastEventAt; bool StartedEmpty }`.
  - **Storage:** JSON lines in `<data>/event-log.jsonl`, written by a background writer. Callers never wait on disk.
  - **Retention:** 7 days and 5,000 rows, oldest first.
  - **A bad file:** an unreadable file is renamed `.corrupt` and the log starts empty, with `StartedEmpty = true`.
- **One row per message per destination** (design §8.1):
  - `LogId` = `groupKey + "|" + destinationId` for media, and the outbox `EntryId` otherwise.
  - Waiting rows are upserted when a media group is held, and moved to their final result by delivery.
  - Pre-routing outcomes use `DestinationId = null`.
- **`Router.Explain(eventKey, libraryIds, destination) => SkipReason?`:** `Paused`, `EventOff` or `LibraryExcluded`. `null` means routed. `NoDestinationWants` covers the null-destination row.
  - Each reason maps to a `web.log.why.*` string, filled with the destination's or the library's name.
- **Delivery:**
  - **Success:** Sent, plus details for late (`QueuedAt` more than 60 s before the send, or any retry), built-in fallback (`TemplateRenderer` flag), and no photo (`PhotosBlocked` fallback). Shortening adds no detail.
  - **Drop:** Failed, with `Telegram` = the error description, `Tries`, and `Advice` mapped from `DeliveryErrorKind`. Lost advises adding the bot back and pressing Check again. NeedsPermissions names the missing right.
  - **Retry scheduled:** the row stays Waiting, with `RetryAt` and the last error.
- **Not logged:** test messages.
- **`GET Log?problemsOnly=&destinationId=&eventKey=&before=&limit=`:** admin only, returns `LogDto`.

- [ ] **Step 1: Write the failing tests:**
  - `EventLog_Upsert_UpdatesInPlace`;
  - `EventLog_Retention_7DaysAnd5000`;
  - `EventLog_CorruptFile_StartsEmpty_SendingUnaffected` (Review Focus 5);
  - `EventLog_WriteNeverBlocksCaller`;
  - `Log_LibraryExcluded_SkippedWithLibraryName`;
  - `Log_NoDestinationWants_DashRow`;
  - `Log_BatchedEpisodes_OneRowWaitingThenSent`;
  - `Log_RetryThenSuccess_OneRowSentLate` (Review Focus 2);
  - `Log_Dropped_FailedWithTelegramDescription`;
  - `Log_TemplateFallback_SentWithBuiltIn`;
  - `Log_TestMessage_NotLogged`;
  - `LogController_ProblemsOnly_FiltersAndPages`.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the full .NET suite and confirm it passes.**
- [ ] **Step 5: Commit.** `feat: an event log of what happened to every message`

### Task 13: The Log section

**Files:**
- Create: `Web/tgjelly-log.js`
- Modify: `Web/tgjelly.js` (append `log` to `SECTIONS`, add the nav label, wire the status poll), `Web/tgjelly.css`, `Plugin.cs`, ci.yml, `smoke.py`, `Strings/en.json`
- Test: `tests/web/log.test.mjs` (new)

**Interfaces:**
- **`renderLog(page: LogDto, filters, S, lang)`:**
  - **Lead:** `web.log.lead`, stating 7 days.
  - **Filters:** All / Problems only (a segmented control), then the Destination and Event selects.
  - **Top line:** `web.log.lastSent` with the time. `web.log.quiet` appears when `LastEventAt` is more than 24 h ago, or null. `web.log.startedEmpty` appears when the log started empty.
  - **Table columns:** When, Event, Destination, Result, Why.
    - **When:** the local time, plus the date when it isn't today. Its `title` attribute holds the full date and time with seconds.
    - **Destination:** a dash when null.
    - **Result:** `label(…)` with the tones from design §8.1.
    - **Why:** empty for Sent.
  - **Rows with Failed or attention results:** they toggle an expansion showing Telegram's words, the tries, the retry and the advice. The expanded ids survive a refresh.
  - **Paging:** a "Show older" button requests `before=<oldest At>`.
- **Refresh:** the page's existing status poll reloads the Log only while the Log section is visible.

- [ ] **Step 1: Write the failing tests:**
  - `log_navAfterAdvanced`;
  - `log_sentRow_hasEmptyWhy`;
  - `log_columnIsDestination`;
  - `log_timeTitle_isFullDateTime`;
  - `log_failedRow_expandsWithTelegramWords`;
  - `log_problemsOnly_requestsFilter`;
  - `log_refresh_keepsExpandedRows`;
  - `log_quietLine_whenNoEventsFor24h`;
  - `log_showOlder_pages`;
  - `log_noSummaryCounts`.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run every suite and confirm it passes.**
- [ ] **Step 5: Commit.** `feat: the Log section`

### Task 14: No vague errors

**Files:**
- Modify: `Strings/en.json`, `Web/tgjelly.js` (`requestError` and `web.header.unknown`), any controller returning a bare error, and the bot's error strings found by the audit.
- Test: `tests/…/Strings/StringTableTests.cs`, `tests/web/tgjelly.test.mjs`

**Interfaces:**
- **`requestError(S, result)`** always includes the HTTP status and the server's message when there is one. When there is neither, it gives the network error text with "the page retries automatically" or a concrete next step. It never returns a string without a cause.
- **`web.header.unknown`** becomes "Status unavailable: {reason}. The page retries every {seconds} s." The reason comes from the failed request.
- **The audit:** go through every string containing "error", "failed", "try again" or "couldn't", in both `web.*` and the bot's strings. Rewrite any that lack a cause or a next step. List each change in the commit body.

- [ ] **Step 1: Write the failing tests:**
  - `Strings_NoVagueErrors`: no string contains "unknown error" or "something went wrong", and none is only "try again" in any case;
  - `requestError_includesStatusAndServerText`;
  - `requestError_networkFailure_namesTheCause`;
  - `headerUnknown_includesReason`.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Audit and rewrite.**
- [ ] **Step 4: Run every suite and confirm it passes.**
- [ ] **Step 5: Commit.** `fix: every error says what happened and what to do`

### Task 15: Docs, verification and the PR

**Files:**
- Modify: `README.md` (sections, per-message switches, the Log, update kinds), the v1 spec (§6.6, and §14.4 with the design's §12 manual checks), `TemplateDocs.cs`, then regenerate `docs/templates.md` with `TGJELLY_WRITE_DOCS=1`.

- [ ] **Step 1: Update the docs.**
- [ ] **Step 2: Run the full local verification:**
  - `dotnet build -c Release` with 0 warnings;
  - `dotnet test`;
  - `node --check` on every `Web/*.js`;
  - the web suite, with no stray logs;
  - both Python suites.
- [ ] **Step 3: Run the greps.**
  - `grep -n "confirm(\|alert(\|prompt(" src/Jellyfin.Plugin.TgJelly/Web/*.js` finds nothing.
  - `grep -rniE "unknown error|something went wrong" src/Jellyfin.Plugin.TgJelly/Strings/en.json` finds nothing.
- [ ] **Step 4: Commit** `docs: Messages, the Log and update kinds`. **Push** `feat/dashboard-tweaks`, **open the PR** into `main`, and **wait for CI to be green.** The PR body ends with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
- [ ] **Step 5: Hand over.** The owner tests on the next pre-release, using the design's §12 checks. Ask before tagging.
