# TgJelly plan 8: dashboard redesign and auto-save — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rebuild the dashboard as a custom-styled page: a header, a sidebar of sections and cards of setting rows, themed from Jellyfin's palette. Every setting saves itself, with a "Saved" cue beside it. The page shows no decorative symbols. The bot's Status screen says which Jellyfin server it is connected to.

**Architecture:**
- **The page:** `tgjelly.html` becomes a thin shell with one root element. A new scoped stylesheet `tgjelly.css` and two new ES modules are served as embedded plugin pages, next to `tgjelly.js`:
  - `tgjelly-ui.js`: components and design tokens;
  - `tgjelly-autosave.js`: the save queue and cues.
- **Sections:** `tgjelly.js` keeps its page controller and its pure `render*` functions, and moves each section onto the components.
- **Saving:** goes through one page-wide queue, which calls today's endpoints with today's revision checks.

**Tech Stack:**
- plain ES modules and CSS, with no framework and no bundler;
- C# (net9.0 and net10.0) for the bot string and the page registration;
- Node 22 `node --test` with the existing fake-view harness (`tests/web/fake-view.mjs`);
- xunit.v3 for C#;
- Python smoke tests (`tests/integration/smoke.py`).

**Spec:**
- `docs/superpowers/specs/2026-10-22-tgjelly-dashboard-redesign-design.md`, binding;
- v1 spec §6.6 for everything it doesn't change;
- `2026-10-21-tgjelly-templates-design.md` for the template editor's behaviour.

## Global Constraints

- **No framework and no Node build in the plugin build.** New files are embedded resources and registered `PluginPageInfo` pages, exactly like `tgjelly.js` today.
  - Modules import each other with relative URLs of the form `./configurationpage?name=tgjelly-ui.js`, the same form Jellyfin serves `tgjelly.js` under.
- **Scoped CSS:** every CSS rule is scoped under `#tgjelly-root`. A test parses `tgjelly.css` and fails on any selector that doesn't start with `#tgjelly-root`, or with a `@media`/`@container`/`@supports`/`@keyframes` block whose rules do.
- **Design tokens:** colours from `--jf-palette-*` with dark-theme fallbacks, one spacing scale, one type scale, and radius from `--jf-card-borderRadius`. Narrow layouts use container queries on `#tgjelly-root`, not media queries on the window.
- **No browser dialogs:** no `alert`/`confirm`/`prompt`. Destructive actions use the inline confirmation component.
- **No decorative symbols in the dashboard:** no ●, ✓, ✅, ⚠️, ❌ or emoji status icons in dashboard markup or `web.*` strings. The bot's own strings and icons are unchanged.
- **Auto-save timing** (design §6.1):
  - switches and selects save at once;
  - text and number fields save 800 ms after the last keystroke, or on blur;
  - the public URL and the proxy fields save only on blur or Enter;
  - templates save 1000 ms after the last keystroke, only with no validation errors, and never with empty text.
- **The cue** (design §6.2):
  - "Saved" for 2000 ms in the success colour;
  - "Saving…" in muted text after 300 ms in flight;
  - "Not saved:" plus the reason in the error colour, until the field is fixed.
- **One page-wide save queue.** Settings, destinations and templates share one `Revision`.
  - On a 409, the page takes the server data, re-applies the pending change and retries once.
  - On a second 409, it shows "Not saved: changed elsewhere, check the value".
- **Typing is never interrupted:** a re-render never replaces a focused field or a field with a pending change. This generalises v0.3's `keepTemplateCaret`.
- **The template preview sanitizer** (`previewHtml`) and its tests keep their behaviour.
- **Wording:** unchanged, except for the Save buttons and their messages, the symbols, and the new cue and confirmation strings. New strings go in `Strings/en.json` under `web.*`.
- **The page must still pass** `WebPagesTests`: `data-role="page"`, no `${`, and the controller attribute.
- **Commands:**
  - prefix every `dotnet` command with `export PATH="$HOME/.dotnet:$PATH"`. Full .NET suite: `dotnet test --solution Jellyfin.Plugin.TgJelly.slnx -c Release`; warnings are errors;
  - web: `node --check` on every `Web/*.js`, then `node --test 'tests/web/*.test.mjs'`.
- **Commits:** use `git -c user.name="Daniil" -c user.email=igatorr19@gmail.com commit`, and end every message with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **Two quick changes in a row,** for example toggling Poster then Overview within 300 ms, or typing in batching while a destination save is in flight. Both must save, in order, with no 409 from the page's own revision race. Test `queue_serializesSavesAcrossSections` (Task 3).
2. **A setting changed in the bot while the admin types in a dashboard field:** the typed text stays, the save lands on the newer revision, and nothing the bot changed is lost. Test `conflict_reappliesPendingChangeOnce` (Task 3).
3. **The public URL typed character by character:** nothing saves until blur or Enter, an invalid URL shows "Not saved:" and isn't sent, and an empty URL saves. Test `publicUrl_savesOnBlurOnly_validatesClientSide` (Task 6).
4. **Leaving the page with a debounced save pending:** navigating away within the debounce window still saves; the queue flushes on the page's `viewbeforehide`. Test `pendingSave_flushesOnHide` (Task 3).
5. **A non-default Jellyfin theme, and other Jellyfin pages after visiting TgJelly:** no TgJelly rule applies outside `#tgjelly-root` (Task 2 scoping test). Every colour comes from a `--jf-palette-*` token with a fallback (Task 2 `tokens_allColorsFromPalette`).

---

### Task 1: The bot's Status screen names the Jellyfin server

**Files:**
- Modify: `src/Jellyfin.Plugin.TgJelly/Bot/Screens/StatusScreen.cs` (`ConnectionText`)
- Modify: `src/Jellyfin.Plugin.TgJelly/Strings/en.json`
- Test: `tests/Jellyfin.Plugin.TgJelly.Tests/Bot/Screens/StatusScreenTests.cs`

**Interfaces:**
- Consumes: `IServerInfo.ServerName` and `IServerInfo.Version`, from `Library/IServerInfo.cs`, injected into `StatusScreen`.
- Produces: a new string `status.connection.connected` = `connected to Jellyfin {server} ({version})`.
  - The version comes from `Formatter.Version`.
  - It is used only when the state is `Connected`.
  - `connection.Connected` ("Connected as @{bot}") stays for the dashboard's connect result.
  - An empty server name gives `connected to Jellyfin ({version})`.

- [ ] **Step 1: Write the failing tests:**
  - `Connected_ShowsJellyfinServerAndVersion`: the server is "Home" with version 12.2.0.0, and the text contains `Connection: connected to Jellyfin Home (12.2)` and not `@`;
  - `Connected_EmptyServerName_ShowsVersionOnly`;
  - `OtherStates_Unchanged`.
- [ ] **Step 2: Run the tests and confirm they fail.**
- [ ] **Step 3: Implement.** The server name is HTML-escaped by the existing `Escape` call.
- [ ] **Step 4: Run the full .NET suite and confirm it passes.**
- [ ] **Step 5: Commit.** `feat: the bot's status names the Jellyfin server instead of the bot`

---

### Task 2: Design tokens, the stylesheet and the components module

**Files:**
- Create: `src/Jellyfin.Plugin.TgJelly/Web/tgjelly.css`
- Create: `src/Jellyfin.Plugin.TgJelly/Web/tgjelly-ui.js`
- Modify: `src/Jellyfin.Plugin.TgJelly/Jellyfin.Plugin.TgJelly.csproj`: embed both files.
- Modify: `src/Jellyfin.Plugin.TgJelly/Plugin.cs` `GetPages()`: register `tgjelly.css` and `tgjelly-ui.js`.
- Modify: `tests/integration/smoke.py`: assert that `configurationpage?name=tgjelly.css` has a content type containing `css`, and that `tgjelly-ui.js` has one containing `javascript`. Also update `tests/integration/test_smoke.py` if it pins the asset list.
- Test: create `tests/web/ui.test.mjs`, `tests/web/css.test.mjs`; update `PluginIdentityTests`/`WebPagesTests` if they pin the pages.

**Interfaces:**
- Produces, in `tgjelly-ui.js`, pure functions returning HTML strings. Every text argument is escaped inside, and every helper takes an optional `extraClass`:
  - `card({ title, description = '', body, id = null })`;
  - `row({ label, hint = '', control, cueKey = null })`, which renders `<span class="tgj-cue" data-tgj-cue="cueKey">` beside the control;
  - `switchControl({ input, checked, disabled = false, label })`: a `<button role="switch" aria-checked>` with `data-tgj-input`;
  - `textField({ input, value, type = 'text', placeholder = '', invalid = false, attrs = {} })`;
  - `selectField({ input, value, options: [{ value, label }] })`;
  - `numberField({ input, value, min, max })`;
  - `actionButton({ label, act, kind = 'secondary' | 'primary' | 'danger', disabled = false, id = null })`;
  - `inlineConfirm({ question, confirmLabel, confirmAct, cancelAct, id = null })`;
  - `notice({ kind = 'info' | 'warning' | 'error', html })`: the only helper that takes trusted HTML, marked in JSDoc;
  - `emptyState({ text, actionHtml = '' })`;
  - `stateLabel({ kind = 'ok' | 'warning' | 'error' | 'muted', text })`: a coloured word, with no symbol.
- **CSS classes**, all `tgj-` prefixed and all under `#tgjelly-root`: `.tgj-shell`, `.tgj-header`, `.tgj-sidebar`, `.tgj-content`, `.tgj-card`, `.tgj-row`, `.tgj-switch`, `.tgj-field`, `.tgj-btn` (`--primary`, `--danger`), `.tgj-confirm`, `.tgj-notice` (`--info`/`--warning`/`--error`), `.tgj-state` (`--ok`/…), `.tgj-cue` (`--saved`/`--saving`/`--error`).
- **Tokens:** CSS custom properties on `#tgjelly-root`:
  - `--tgj-fg`, `--tgj-fg-2`, `--tgj-line`, `--tgj-paper`, `--tgj-accent`, `--tgj-success`, `--tgj-warning`, `--tgj-error`, each `var(--jf-palette-…, <fallback>)`;
  - `--tgj-space-{1..5}`, `--tgj-step-{-1..2}` and `--tgj-radius`.
  - Status text colours are `color-mix`ed toward `--tgj-fg`, as Intro Skipper does, for contrast.
- **The cue fade** respects `prefers-reduced-motion`.

- [ ] **Step 1: Write the failing tests:**
  - `css.test.mjs`:
    - `everySelectorScopedToRoot`, which parses rules, including those inside `@media`/`@container`/`@supports`;
    - `tokens_allColorsFromPalette`: every `color`/`background`/`border-color` value is a `var(--tgj-…)` token or `transparent`/`currentColor`/`inherit`, and every `--tgj-` colour token is a `var(--jf-palette-…, …)` or a `color-mix` of tokens.
  - `ui.test.mjs`:
    - `row_escapesLabelAndHint`;
    - `switchControl_isAriaSwitch`;
    - `inlineConfirm_hasNoDialogCall`;
    - `stateLabel_hasNoSymbol`;
    - `notice_kinds`;
    - `textField_invalidSetsAriaInvalid`.
  - Smoke unit test: the new asset checks in `tests/integration/test_smoke.py`.
- [ ] **Step 2: Run the tests and confirm they fail.**
- [ ] **Step 3: Implement.** The mockups (cards on `--tgj-paper`, thin row dividers, a pill-shaped accent switch) are the visual bar.
- [ ] **Step 4: Run the node, .NET and Python suites and confirm they pass.**
- [ ] **Step 5: Commit.** `feat: dashboard design tokens, scoped stylesheet and components`

---

### Task 3: The auto-save queue and cues

**Files:**
- Create: `src/Jellyfin.Plugin.TgJelly/Web/tgjelly-autosave.js`, embedded and registered like Task 2's files.
- Test: `tests/web/autosave.test.mjs`

**Interfaces:**
- Produces: `createAutosave({ clock, onCue, onSettled })` → `{ change(job), flush(), cancel(key) }`.
  - `clock` is `{ now(), setTimeout, clearTimeout }`, so tests control time.
  - `job` is `{ key, mode: 'now' | 'debounce' | 'blur', delay?, validate?: () => string | null, send: () => Promise<SaveResult> }`.
    - `key` identifies the field or resource, e.g. `settings.Message.ShowPoster`, `template:media.movie`, `dest:<id>`.
    - `validate` returns an error message or null. With an error, nothing is sent and the cue is `{ state: 'error', text }`.
  - `SaveResult` is `{ status: 'saved' } | { status: 'invalid', message } | { status: 'conflict', retry: () => Promise<SaveResult> } | { status: 'failed', message }`.
    - `retry` is produced by the caller after taking the newer server data and re-applying the pending change.
    - It is called once. A second `conflict` gives the cue error `web.autosave.conflict`.
  - **Queue:** jobs run strictly one at a time, page-wide, in arrival order. A newer `change` for a key that is still queued, but not yet sent, replaces it.
  - **Debouncing:**
    - a `debounce` job runs `delay` ms after its last `change`;
    - a `blur` job runs only on `flush(key)` or `flush()`;
    - the controller calls `flush(key)` on blur or Enter, and `flush()` on `viewbeforehide`.
  - **Cues:** `onCue(key, { state: 'saving' | 'saved' | 'error' | 'idle', text? })`.
    - `saving` fires only once the request has been in flight 300 ms.
    - `saved` fires on success, then `idle` 2000 ms later.
    - `error` stays until the next `change` for that key.
  - `onSettled(key, result)` lets the controller update its state.
- **Strings:** `web.autosave.saved` = "Saved", `web.autosave.saving` = "Saving…", `web.autosave.notSaved` = "Not saved: {reason}", `web.autosave.conflict` = "changed elsewhere, check the value".

- [ ] **Step 1: Write the failing tests:**
  - `now_sendsImmediately_cueSavedThenIdle`;
  - `debounce_sendsOnceAfterLastChange`;
  - `blur_waitsForFlush`;
  - `validateError_neverSends_cueError`;
  - `queue_serializesSavesAcrossSections`;
  - `newerChange_replacesQueued`;
  - `saving_cueOnlyAfter300ms`;
  - `conflict_reappliesPendingChangeOnce`: the first send returns `conflict`, `retry` returns `saved`, and the cue is `saved`;
  - `secondConflict_cueError`;
  - `failed_cueErrorStaysUntilNextChange`;
  - `pendingSave_flushesOnHide`.
- [ ] **Step 2: Run the tests and confirm they fail.**
- [ ] **Step 3: Implement.** The module is pure (no DOM), so it is tested only through the fake clock.
- [ ] **Step 4: Run the node suite and confirm it passes.**
- [ ] **Step 5: Commit.** `feat: page-wide auto-save queue with cues and conflict retry`

---

### Task 4: The shell, section navigation and focus-safe rendering

**Files:**
- Modify: `src/Jellyfin.Plugin.TgJelly/Web/tgjelly.html`. Keep `data-role="page"`, the id and `data-controller`. Replace the body with `<link rel="stylesheet" href="configurationpage?name=tgjelly.css">` and `<div id="tgjelly-root"></div>`. Delete the old `<style>` block, since its rules move to `tgjelly.css`.
- Modify: `src/Jellyfin.Plugin.TgJelly/Web/tgjelly.js`: the shell render, navigation, the section host elements, and the generalised focus preservation.
- Test: `tests/web/tgjelly.test.mjs` and `fake-view.mjs`

**Interfaces:**
- Produces:
  - `export const SECTIONS = ['overview', 'destinations', 'messages', 'templates', 'advanced']`, with labels from `web.nav.<name>`: "Overview", "Destinations", "Messages", "Templates", "Advanced".
  - `export function renderShell(S, { active, connection, counts })` renders:
    - a header with "TgJelly" and a `stateLabel`;
    - a sidebar `<nav>` of `<button data-tgj-nav="name" aria-current>`, with muted counts such as "3" and "1 edited";
    - one `<section data-tgj-section="name">` per section, only the active one visible.
- **Section hosts:** today's section container ids move inside their section host:
  - `tgj-status`, `tgj-connect`, `tgj-bot` → overview;
  - `tgj-destinations` → destinations;
  - `tgj-messages` → messages;
  - `tgj-templates` → templates;
  - `tgj-proxy`, `tgj-batching`, `tgj-updates`, `tgj-manual`, `tgj-diagnostics` → advanced.

  `renderSection(name)` keeps working unchanged.
- **The active section** is kept in the URL hash (`#tgj=messages`) and restored on load. An unknown value falls back to `overview`.
- **Before a bot is connected,** `overview` is active on first load.
- **Focus-safe writes:** `keepFocus(rootEl, write)` generalises `keepTemplateCaret`. Every section write keeps focus, selection and scroll for any focused `input`, `textarea` or `select` inside it. A field with a pending auto-save keeps the value it had at the time of the write.
- **The narrow layout:** at a container width of 40rem or less, the sidebar becomes a horizontally scrolling tab row.

- [ ] **Step 1: Write the failing tests:**
  - `shell_rendersAllSections_onlyActiveVisible`;
  - `nav_click_switchesSection_updatesHash`;
  - `hash_restoresSection_unknownFallsBack`;
  - `notConnected_opensOverview`;
  - `sectionWrite_keepsFocusInAnyField`, with an input inside the messages section;
  - `pendingField_keepsTypedValueAcrossRefresh`.
  - Update the existing tests that query by the old layout, keeping their intent.
- [ ] **Step 2: Run the tests and confirm they fail.**
- [ ] **Step 3: Implement.** Old section markup still renders inside its host. Tasks 5–8 restyle it.
- [ ] **Step 4: Run the node and .NET suites and confirm they pass.** `WebPagesTests` must pass.
- [ ] **Step 5: Commit.** `feat: dashboard shell with section sidebar and focus-safe rendering`

---

### Task 5: Overview section

**Files:**
- Modify: `tgjelly.js`: `renderStatus`, `renderConnect`, `renderBot`, and a new `renderOverviewSummary`
- Modify: `en.json`
- Test: `tests/web/tgjelly.test.mjs`

**Interfaces:**
- Consumes: Task 2 components, Task 4 shell.
- Produces: Overview as cards:
  - **Status** (today's status content as rows, health as `stateLabel`s);
  - **Bot** (connect when no bot; otherwise the bot rows with Change bot, Disconnect and Open in Telegram as `actionButton`s; Disconnect uses `inlineConfirm`);
  - **Summary**: rows for destinations (count and how many need attention), templates (edited count, flagged count) and notices. Each row ends with an `actionButton` that switches to its section through `data-tgj-nav`.
  - No symbols.
  - The connect form keeps its flow and texts, with no Save button. Connecting stays an explicit action.

- [ ] **Step 1: Write the failing tests:**
  - `overview_cards_statusBotSummary`;
  - `summary_linksToSections`;
  - `overview_noSymbols`: no `●✓✅⚠️❌` in the rendered HTML for every status fixture;
  - `disconnect_asksInline`.
- [ ] **Step 2: Run the tests and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the suites and confirm they pass.**
- [ ] **Step 5: Commit.** `feat: Overview section with status, bot and settings summary`

---

### Task 6: Messages and Advanced, with auto-save

**Files:**
- Modify: `tgjelly.js`: `renderMessages`, `renderBatching`, `renderUpdates`, `renderProxyForm`, `renderManual`, `renderDiagnostics`, `saveSettings`, the proxy save, and the input handlers
- Modify: `en.json`: remove `web.messages.save`, `web.batching.save` and `web.updates.save`, and the proxy save label, when unused.
- Test: `tests/web/tgjelly.test.mjs`

**Interfaces:**
- Consumes: Tasks 2–4.
- **Each setting is a `row`:**
  - toggles are `switchControl`, saved with `mode: 'now'`;
  - selects are `mode: 'now'`;
  - batching numbers are `numberField`, saved with `mode: 'debounce', delay: 800`, plus `flush` on blur;
  - the **public URL** is `mode: 'blur'`, and **the proxy fields** are `mode: 'blur'` as one job keyed `proxy`, flushed when focus leaves the proxy card.
- **Validation:**
  - **The public URL:** `validatePublicUrl(text)` is a JS port of `PublicUrlRules`, e.g. an absolute `http(s)` URL whose host contains a dot and isn't an IPv6 literal. Empty text is valid. An invalid URL uses `web.messages.urlInvalid`.
  - **The proxy:** choosing a mode with no address gives `web.proxy.needsAddress` = "enter the proxy address".
  - **Batching:** the existing range rules.
- **`send`:** each send reuses today's request builders (`settingsRequest`, the proxy body) with the current revision. A 409 maps to `conflict`, with a `retry` that takes `body.Current`, re-applies the field and re-sends. A 400 maps to `invalid`, with the server's message.
- The "Save" buttons and the "saved" banner are removed. Copy diagnostics, Check now and the manual add stay as actions.

- [ ] **Step 1: Write the failing tests:**
  - `toggle_savesImmediately_showsSavedBesideRow`;
  - `batching_savesAfter800ms_orOnBlur`;
  - `publicUrl_savesOnBlurOnly_validatesClientSide`: typing doesn't send; an invalid URL on blur gives a cue error and no PUT; an empty URL on blur sends;
  - `proxy_savesOnceWhenLeavingCard_notPerKeystroke`;
  - `proxyModeWithoutAddress_notSaved`;
  - `noSaveButtons_inMessagesOrAdvanced`;
  - `settingsConflict_retriesWithServerData`.
- [ ] **Step 2: Run the tests and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the suites and confirm they pass.**
- [ ] **Step 5: Commit.** `feat: Messages and Advanced as cards with auto-save`

---

### Task 7: Destinations, with auto-save and preset confirmation

**Files:**
- Modify: `tgjelly.js`: `renderDestinations`, `renderEditor`, and the destination save and actions
- Modify: `en.json`
- Test: `tests/web/tgjelly.test.mjs`

**Interfaces:**
- Consumes: Tasks 2–4.
- **The list** is a card of rows: the title, a muted kind and preset, and the health as a `stateLabel`. An Edit action opens the editor. The `emptyState` is "No destinations yet" with the add actions.
- **The editor** is a card with rows. Each change saves through the queue with key `dest:<id>`:
  - preset `select`: `now`, except that leaving Custom asks first (below);
  - event switches: `now`;
  - library choices: `now`;
  - silent groups: `now`;
  - text fields: `debounce` 800.
- **Leaving Custom for a preset** shows `inlineConfirm` with `web.dest.presetConfirm` = "Replace your custom events with this preset?", plus **Replace** and **Cancel**. Until it is confirmed, the select shows Custom and nothing is sent.
- **Remove, Test and Check** stay actions. Remove keeps its inline confirmation.
- **A 409** retries through `body.Current`, as in Task 6.

- [ ] **Step 1: Write the failing tests:**
  - `destList_rowsWithStateLabels_noSymbols`;
  - `eventSwitch_savesImmediately`;
  - `presetFromCustom_asksInline_cancelKeepsCustom_replaceSends`;
  - `presetToPreset_savesImmediately`;
  - `destConflict_retries`;
  - `noSaveButton_inEditor`.
- [ ] **Step 2: Run the tests and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the suites and confirm they pass.**
- [ ] **Step 5: Commit.** `feat: Destinations as cards with auto-save and a preset confirmation`

---

### Task 8: Templates, with auto-save

**Files:**
- Modify: `tgjelly.js`: `renderTemplates`, `renderTemplatePreview`, `tplSave`, and the template input handler
- Modify: `en.json`: remove `web.templates.save` when unused, and add `web.templates.notSavedErrors` = "fix the errors above"
- Test: `tests/web/tgjelly.test.mjs`

**Interfaces:**
- Consumes: Tasks 2–4. The v0.3 template editor behaviour stays:
  - the live preview with a 500 ms debounce;
  - stale-reply dropping;
  - Send test;
  - Reset with its inline confirmation;
  - the badge as the word "Edited" or "Built-in";
  - the event select marks edited items with the word "Edited", not "●".
- **Restyle:** the editor uses cards and rows. The Telegram bubble keeps its Telegram look.
- **The template text** saves through the queue with key `template:<key>`, `mode: 'debounce', delay: 1000`. `validate()` returns:
  - `web.templates.notSavedErrors` when the latest validation for exactly this text has errors;
  - a message when the text is empty or whitespace (Reset is the way to reset);
  - otherwise null.
- **The cue** sits beside the textarea's label.
- **The Save button is removed.**

- [ ] **Step 1: Write the failing tests:**
  - `template_autosavesAfter1s`;
  - `template_withErrors_notSaved`;
  - `template_empty_notSaved`;
  - `template_fixedAfterError_saves`;
  - `noSaveButton_inTemplates`;
  - `templateSelect_marksEditedWithWord`.
- [ ] **Step 2: Run the tests and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the suites and confirm they pass.**
- [ ] **Step 5: Commit.** `feat: templates save themselves as you type`

---

### Task 9: Symbol sweep, string clean-up, docs and spec

**Files:**
- Modify: `src/Jellyfin.Plugin.TgJelly/Strings/en.json` (`web.*`), `tgjelly.js`
- Modify: `docs/superpowers/specs/2026-10-16-tgjelly-v1-design.md` §6.6, and §14.4 (add the design's §10 manual checks)
- Modify: `README.md`: describe the dashboard sections and auto-save
- Modify: `tests/Jellyfin.Plugin.TgJelly.Tests/Templates/TemplateDocs.cs`, wherever the guide mentions Save; regenerate `docs/templates.md`
- Test: `tests/web/tgjelly.test.mjs`, `tests/Jellyfin.Plugin.TgJelly.Tests/Strings/*`

**Interfaces:**
- **A string test:** `WebStrings_HaveNoDecorativeSymbols` checks that no `web.*` string contains any of `● ✓ ✔ ✅ ⚠ ❌ ✗ 🟢 🔴`.
- **A JS test:** `renderedDashboard_hasNoDecorativeSymbols` renders every section with full fixtures and asserts that none of those symbols appears.
- **Dashboard-rendered shared strings,** such as the `dest.health.*` labels: if a string is shared with the bot, the dashboard uses its own `web.*` copy, and the bot's string stays unchanged.
- **Unused strings:** remove every `web.*` key no longer used, checked by grep, and keep the string-keys test in step.

- [ ] **Step 1: Write the failing tests:** the two above. Run them and confirm they fail.
- [ ] **Step 2: Remove the symbols and the unused strings, and update the docs and spec.**
- [ ] **Step 3: Run all suites and confirm they pass.**
- [ ] **Step 4: Commit.** `chore: no decorative symbols in the dashboard; docs for the new layout and auto-save`

---

### Task 10: Verify and open the PR

- [ ] **Step 1: Run the full local verification:**
  - `dotnet build -c Release` (0 warnings);
  - `dotnet test` (both TFMs);
  - `node --check` on every `Web/*.js`;
  - `node --test 'tests/web/*.test.mjs'`;
  - both Python suites.
- [ ] **Step 2: Run the constraint greps:**
  - `grep -n "confirm(\|alert(\|prompt(" src/Jellyfin.Plugin.TgJelly/Web/*.js` → none;
  - `grep -rn "web\.[a-z]*\.save\"" src/Jellyfin.Plugin.TgJelly/Strings/en.json` → none.
- [ ] **Step 3: Push `feat/dashboard-redesign` and open the PR into `main`.**
  - The body ends with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
  - Wait for CI. Every check must be green, including the smoke runs that load the new CSS and JS assets.
- [ ] **Step 4: Hand over to the owner.** Ask them to:
  - run the design's §10 manual checks;
  - merge;
  - approve a `v0.4` tag.

  Do not push a tag without approval.
