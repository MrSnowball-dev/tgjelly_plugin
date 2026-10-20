# TgJelly Plan 5: Dashboard, Templates, Release — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An admin can do everything from the Jellyfin dashboard page, not only from the bot:
- connect a bot;
- see status and notices;
- manage destinations, the manual block and detected chats;
- edit message settings, proxy, batching and update checks;
- write custom Scriban templates.

Templates are used when messages are sent. The release pipeline publishes both zips and a plugin repository manifest.

**Architecture:**
- **Backend.** The backend gains five pieces:
  - Status notices with Dismiss;
  - `GET Strings` and `GET Diagnostics`;
  - a Scriban `TemplateEngine` behind a data-only model built from `RenderModel`;
  - send-time templates in `NotificationRenderer`, with template flags in `state.status`;
  - the Templates API.
- **Page.** It stays one ES module, `Web/tgjelly.js`, as the spec's two embedded web files require (§13.2):
  - **Render functions are pure.** Each one takes data and returns an escaped HTML string, and is a named export.
  - **The default-export controller is thin.** It holds the state in its closure, calls the API, sets `innerHTML` on section containers, and handles clicks through one delegated listener keyed by `data-tgj-act`.
  - **Testing.** Node's built-in test runner tests the named exports and the controller (with a small fake view), so no npm packages are needed.
- **Release.** `release.yml` reuses `ci.yml` as a called workflow, then publishes a GitHub Release and the manifest on `gh-pages`.

**Tech Stack:**
- C# / .NET 9 + .NET 10 (unchanged), Telegram.Bot 22.10.3.3, **Scriban 7.5.1**;
- xunit.v3, NSubstitute, `Microsoft.Extensions.TimeProvider.Testing`;
- Node 22 (`node --test`, stdlib only) for the page;
- Python 3 stdlib for packaging and the smoke test;
- GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-10-16-tgjelly-v1-design.md` (rev. 2). This plan implements:
- §6.1 and §6.6, the dashboard first run and the page after setup;
- the dashboard side of §6.5, the manual block and detected chats;
- §8.5, custom templates;
- §11.1 and §11.2, the full page plus `Status/Dismiss`, `Templates`, `Templates/Validate`, `Strings` and `Diagnostics`;
- §13.4 and §13.5, adding `Scriban.dll` to the whitelist and `release.yml`;
- §13.1, the README and `docs/templates.md`;
- §14.3, the BaseUrl variant.

It settles §15 Q5 and Q6. Plans 1–4 are merged into `main`, and their Global Constraints still apply.

**Deliberate deviations and decisions (so reviewers don't flag them):**
- **One JS file with pure, exported render functions.**
  - Jellyfin loads the page controller as `__plugin/tgjelly.js`. A relative import from that URL isn't served, so a second module can't be loaded reliably.
  - Every render function returns a string built only through `esc()` for values. The controller assigns it to `innerHTML` on a `tgj-` container.
  - Status polling re-renders only `#tgj-status`, so it never wipes a form someone is typing in.
- **No `window.confirm`/`alert`.** Destructive buttons, Remove and Disconnect, use a two-step inline confirm: the button turns into "Confirm …" plus "Cancel".
- **Notices are computed, not stored.**
  - `GET Status` derives them from state on every read.
  - A notice's ID includes its occurrence (a timestamp or a hash), so a problem that comes back shows again even after an earlier one was dismissed.
  - The dismissed IDs are pruned to the active notices on every read and every dismiss, with a cap of 50.
- **"Truncated" is judged at render time.** A template is flagged "truncated" when its output exceeds the limit of the type the renderer chose. That is the caption limit when a poster was picked and the text limit otherwise. A later fallback to text-only in delivery doesn't change the flag.
- **`GET Strings` returns the whole table,** with the fallback overlaid key by key. The page reuses the bot's labels: `dest.event.*`, `dest.preset.*`, `dest.health.*`, `groups.*`. Strings that only the page uses are new `web.*` keys.
- **The template preview is shown as escaped text** in a `<pre>`, not as rendered HTML. The page never injects template output as markup.
- **`GET Destinations` gains a `Catalog` field** (event keys, groups, preset expansions), so the editor can show what a preset includes without hard-coding the catalog in JS.
- **Out of scope here:** README screenshots, the repo going public, the logo, the §14.4 checklist and the first tag. These are the owner's actions in Task 15.

## Global Constraints

Plans 1–4's Global Constraints apply unchanged:
- identity, two builds, analyzers with zero warnings and one type per file;
- host safety, no static mutable state;
- Telegram.Bot used only through `ITelegramSender`/`BotSupervisor`, redaction, strings from `en.json` (new keys also in `tests/Jellyfin.Plugin.TgJelly.Tests/Strings/StringKeys.cs`);
- PascalCase API JSON, the `export PATH="$HOME/.dotnet:$PATH" &&` prefix;
- one commit per task, made with `git -c user.name=Daniil -c user.email=igatorr19@gmail.com commit` plus the harness's trailer lines.

In addition, from the spec:
- **Zip whitelist (§13.4):** exactly `Jellyfin.Plugin.TgJelly.dll`, `Telegram.Bot.dll`, `Scriban.dll` and `meta.json`. `meta.json` `assemblies` lists those three DLLs (F5).
- **Scriban 7.5.1, pinned exactly (§13.2, F41).**
  - **Limits per render:** `LoopLimit = 1000`, `RecursiveLimit = 20`, `ObjectRecursionLimit = 20`, `LimitToString = 16384` with `OnStringLimit = Throw`, `OutputLimit = 16384` with `OnOutputLimit = Throw`, `RegexTimeOut = 1 s`, and a `CancellationToken` from a 2 s `CancellationTokenSource`.
  - **Validation renders** use `StrictVariables = true` and `EnableRelaxedMemberAccess = false`. **Send-time renders** use `StrictVariables = false`.
- **Template model (§8.5):** a flat model of plain values only: strings, numbers, booleans, and arrays of these. Episodes are an array of `{number, title}`. Timestamps are `<name>_unix` integers plus a pre-formatted `<name>_text`. It never holds Jellyfin or Telegram objects.
  - Every other expression result goes through `HtmlText.Escape`. `RawHtml` values are written as-is, and `raw(x)` wraps `x` in one.
  - Literal template text is never escaped.
- **Telegram HTML whitelist (§8.5):** `b strong i em u ins s strike del span.tg-spoiler tg-spoiler a code pre blockquote[expandable] tg-emoji tg-time`, with balanced tags.
- **Template scope (§8.5):** a template replaces only the text or caption; the poster and buttons follow the toggles. Digests always use the default layout. A flag clears when the template's text or its `Enabled` changes.
- **Page rules (§11.1, F25–F30):**
  - **HTML shell.** The shell is exactly `<div data-role="page" id="tgjConfigPage" class="page type-interior pluginConfigurationPage" data-controller="__plugin/tgjelly.js">…</div>`. All markup, `<template>` and `<style>` go inside it.
  - **No inline scripts, and no `${`** anywhere in the HTML.
  - **Elements.** Use only `emby-input`, `emby-checkbox`, `emby-button` and `emby-linkbutton`. `<select is="emby-select">` is used only with `class="emby-select-withcolor emby-select"` and an explicit label. Collapsibles use native `<details>/<summary>`, and text areas are plain `<textarea>`.
  - **Colors** come only from the stock classes or `var(--jf-palette-*)`.
  - **Controller.** `export default function TgJellyPage(view, params)`. Listeners are bound once, in the constructor. `viewshow` is idempotent. Elements are queried only through `view.querySelector`, and every ID and class is prefixed `tgj-`.
  - **Polling.** Status is polled every 3 s while the page is visible. Polling stops on `viewbeforehide`/`viewhide`/`viewdestroy`, when `!view.isConnected` or `view.classList.contains('hide')`. It pauses while `document.hidden`.
  - **Network.** Only `ApiClient.fetch` with `ApiClient.getUrl('TgJelly/<path>')`, never a leading `/`. The page never calls `api.telegram.org`.
  - **Strings** come from `GET TgJelly/Strings?lang=<document.documentElement.lang>`. There is no QR code.
- **API (§11.2):** admin-only. Mutating requests carry `expectedRevision` (in the body, or as a query parameter for `DELETE`). Token, test, check, validate and dismiss calls don't need it. No API returns the token or the proxy credentials (§10.2).
- **Revision conflicts (§10.4):** on a 409, the page keeps the user's unsaved field values, applies them on top of the fresh data, highlights fields that also changed on the server, and asks the user to save again.
- **Logging and diagnostics (§12):** secrets redacted with `SecretRedactor`. Proxy passwords are never logged or reported.

## Review Focus

1. **Concurrent edits from the bot and the page.** A 409 must keep the admin's typed values, show the server's other changes, and highlight fields both sides changed. *(Task 7: `mergeConflict` tests. Tasks 9 and 11: the editors use it.)*
2. **Hostile text in the page.** Chat titles, library names, error text, notices and the template preview containing `<img src=x onerror=…>`, quotes or `&` must render as text. *(Tests: Task 7 (`esc`, status), Task 9 (destination titles), Task 10 (detected titles), Task 12 (preview).)*
3. **Hostile or broken templates at send time.** An infinite loop, a 1 MB output, deep recursion, or a field the event doesn't have must:
   - never block delivery past the 2 s cap;
   - fall back to the default layout, or render empty for a missing field;
   - set a flag, and never throw out of `NotificationRenderer`.

   *(Tests: Task 3 (limits), Task 5 (send-time fallbacks).)*
4. **The page is visited again or runs under BaseUrl `/jf`.** A second `viewshow` must not bind listeners twice or start a second poller. All URLs must go through `ApiClient.getUrl`. *(Task 7: fake-view tests. Task 13: the BaseUrl smoke variant.)*
5. **Diagnostics pasted into a public GitHub issue.** It must not contain the token, the proxy password, chat IDs or chat titles. *(Task 2.)*

---

## File Structure

| Area | Files |
|---|---|
| Notices | Create `Api/NoticeDto.cs`, `Api/DismissRequest.cs`, `Status/NoticeBuilder.cs`, `Status/Notice.cs`. Modify `Api/StatusDto.cs`, `Api/TgJellyController.cs`, `State/Documents/StatusSection.cs` |
| Strings, diagnostics | Create `Api/StringsController.cs`, `Api/DiagnosticsController.cs`, `Api/DiagnosticsDto.cs`, `Status/DiagnosticsReport.cs`. Modify `Strings/StringTable.cs` |
| Template engine | Create `Templates/RawHtml.cs`, `Templates/TelegramTemplateContext.cs`, `Templates/TemplateEngine.cs`, `Templates/TemplateError.cs`, `Templates/TemplateRenderResult.cs`, `Rendering/TelegramHtmlValidator.cs`. Modify the csproj and `build/package.py` |
| Template model | Create `Templates/TemplateField.cs`, `Templates/TemplateFieldCatalog.cs`, `Templates/TemplateModelBuilder.cs`, `docs/templates.md` |
| Send-time templates | Create `State/Documents/TemplateFlag.cs`, `Templates/TemplateFlags.cs`, `Templates/TemplateText.cs`. Modify `Rendering/NotificationRenderer.cs`, `State/Documents/OutboxMessage.cs`, `Delivery/OutboxDelivery.cs` |
| Templates API | Create `Api/TemplatesController.cs`, `Api/TemplatesDto.cs`, `Api/TemplateItemDto.cs`, `Api/TemplateFieldDto.cs`, `Api/TemplateFlagDto.cs`, `Api/TemplateUpdateRequest.cs`, `Api/TemplateValidateRequest.cs`, `Api/TemplateValidationDto.cs` |
| Page | Modify `Web/tgjelly.html`, `Web/tgjelly.js`, `Strings/en.json`. Create `tests/web/tgjelly.test.mjs`, `tests/web/fake-view.mjs`. Modify `Api/DestinationsDto.cs` (`Catalog`) and `.github/workflows/ci.yml` |
| Smoke, release | Modify `tests/integration/smoke.py`, `.github/workflows/ci.yml`. Create `.github/workflows/release.yml`, `build/publish_pages.py`, `build/test_publish_pages.py`, `README.md` |

All C# paths are under `src/Jellyfin.Plugin.TgJelly/` unless they start with `tests/`. Tests mirror the folder: `tests/Jellyfin.Plugin.TgJelly.Tests/<Folder>/<Type>Tests.cs`.

---

### Task 1: Status notices and Dismiss

**Files:**
- Create: `Status/Notice.cs`, `Status/NoticeBuilder.cs`, `Api/NoticeDto.cs`, `Api/DismissRequest.cs`
- Modify: `State/Documents/StatusSection.cs`, `Api/StatusDto.cs`, `Api/TgJellyController.cs`, `Strings/en.json`, `StringKeys.cs`
- Test: `tests/.../Status/NoticeBuilderTests.cs`, `tests/.../Api/TgJellyControllerTests.cs`

**Interfaces:**
- Produces:
  - `sealed record Notice(string Id, string Kind, string Text)`.
  - `sealed class NoticeBuilder(StateStore state, SecretsStore secrets, SettingsStore settings, ILibraryAccess library, StringTable strings, TimeProvider time)` with:
    - `IReadOnlyList<Notice> Active()`: every current notice, dismissed ones included;
    - `IReadOnlyList<Notice> Visible()`: `Active()` minus the dismissed ones. It also prunes `DismissedNotices` to the active IDs, writing only on a change;
    - `bool Dismiss(string id)`: stores the ID if it is active (cap 50, oldest dropped) and returns whether it was active.
  - `StatusSection.DismissedNotices: List<string>` (new field, `[]`).
  - `StatusDto.Notices: IReadOnlyList<NoticeDto>`, where `NoticeDto { Id, Kind, Text }`.
  - `POST TgJelly/Status/Dismiss` with `DismissRequest { NoticeId }`. It returns 204 whether or not the ID was active.
- Consumes: `StatusSection.PublicUrlFlag`, `BackupRestoredAt`, `LoadWarnings`; `UpdateCheckState`; `LibraryFilter.RefersOnlyToRemoved`; and `StatusSection.TemplateFlags`. That last field doesn't exist yet: Task 5 adds it, and this task leaves a `// Task 5 adds template flags` slot in `Active()`.

Notice kinds, IDs and text. Notice text is plain text from `StringTable`, and the page escapes it:

| Kind | ID | Source | String key |
|---|---|---|---|
| `BackupRestored` | `backup:<BackupRestoredAt unix s>` | `BackupRestoredAt` set | `notice.backupRestored` `{time}` (UTC, `yyyy-MM-dd HH:mm`) |
| `LoadWarning` | `load:<first 12 hex of SHA-256(text)>` | each `LoadWarnings` entry and `secrets.LoadWarning` | the warning text itself |
| `PublicUrlFlagged` | `publicUrl:<At unix s>` | `PublicUrlFlag` set | `notice.publicUrlFlagged` `{url}` |
| `UpdateCheckFailing` | `update:<12 hex of SHA-256(LastError)>` | `ConsecutiveFailures >= 3` | existing `update.failing` `{reason}` |
| `RemovedLibraries` | `libraries:<12 hex of SHA-256(sorted affected destination IDs joined by ",")>` | some destination `RefersOnlyToRemoved` | `notice.removedLibraries` plural over the count |

- [ ] **Step 1: Write the failing tests** in `NoticeBuilderTests`:
  - `NoProblems_NoNotices`;
  - `EachSource_GivesItsNotice`: a theory over the five rows, asserting `Kind` and the `Id` prefix;
  - `Dismissed_IsHiddenButStillActive`;
  - `DismissUnknown_ReturnsFalse_StoresNothing`;
  - `Recurrence_ShowsAgain`: a public URL flag at t1 is dismissed, cleared, then set at t2, and `Visible()` contains `publicUrl:<t2>`;
  - `ResolvedNotice_PrunesItsDismissal`: dismiss, clear the flag, call `Visible()`, and `DismissedNotices` is empty;
  - `DismissCap_Is50_OldestDropped`.

  In `TgJellyControllerTests`:
  - `Status_ListsVisibleNotices`;
  - `Dismiss_Returns204_AndHidesIt`.
- [ ] **Step 2: Run them and see them fail.** Run `export PATH="$HOME/.dotnet:$PATH" && dotnet test --project tests/Jellyfin.Plugin.TgJelly.Tests -c Release -f net10.0 -- --filter-class "*NoticeBuilderTests" --filter-class "*TgJellyControllerTests"`. Expected: a compile error, because `NoticeBuilder` is missing.
- [ ] **Step 3: Implement.**
  - Register `NoticeBuilder` as a singleton.
  - Add `Notices = _notices.Visible()...` to `GetStatus`.
  - Add the `Dismiss` action to `TgJellyController`, with `ApiGuard.Block`.
- [ ] **Step 4: Run the class filters again.** Expected: PASS. Then run the full suite.
- [ ] **Step 5: Commit:** `feat: status notices with dismiss`

---

### Task 2: `GET Strings` and `GET Diagnostics`

**Files:**
- Create: `Api/StringsController.cs`, `Api/DiagnosticsController.cs`, `Api/DiagnosticsDto.cs`, `Status/DiagnosticsReport.cs`
- Modify: `Strings/StringTable.cs`
- Test: `tests/.../Strings/StringTableTests.cs`, `tests/.../Api/StringsControllerTests.cs`, `tests/.../Status/DiagnosticsReportTests.cs`

**Interfaces:**
- Produces:
  - `StringTable.EmbeddedLanguages: IReadOnlyList<string>`, static, read from the resource names `…Strings.<lang>.json`.
  - `static string StringTable.BestMatch(string? lang)`. It tries an exact match (case-insensitive), then the primary subtag (`pt-BR` → `pt`), then `en`.
  - `IReadOnlyDictionary<string, string> StringTable.Snapshot()`: the fallback table overlaid by the language's own table.
  - `GET TgJelly/Strings?lang=xx` → `Dictionary<string, string>`. Keys are unchanged, so `connection.Connected` stays as it is.
  - `sealed class DiagnosticsReport(...)` with `string Build()`, and `GET TgJelly/Diagnostics` → `DiagnosticsDto { Text }`.
- Consumes: the same services `TgJellyController` takes, plus `NoticeBuilder` (Task 1) and `IServerApplicationHost` (for `ApplicationVersion`; use the existing `JellyfinServerInfo` wrapper if one exposes it).

`DiagnosticsReport.Build()` writes plain `key: value` lines in sections, in this order:

1. TgJelly: version, build target, compatible, instance owner.
2. Server: Jellyfin version, OS, .NET runtime.
3. Bot: connection state, has token, bot username, base-URL override set (yes or no, never the value).
4. Proxy: mode, plus scheme, host and port only.
5. Settings: revision, public URL set and valid (yes or no), message toggles, batching values, update check.
6. Destinations: one line each with ID, kind, preset, enabled, health, photos blocked, topic set (yes or no), event count, library count. **Never the chat ID or the title.**
7. Templates: the enabled event keys and their flags. This section is added in Task 5.
8. Counters: outbox count, pending media, notices.
9. Last errors and load warnings.

The whole text goes through `SecretRedactor.Redact`.

- [ ] **Step 1: Write the failing tests.**
  - `BestMatch` theory: `("en","en")`, `("EN-us","en")`, `("pt-BR","en")` (no `pt` table), `(null,"en")`, `("","en")`.
  - `Snapshot_HasEveryEnglishKey`.
  - `StringsController_ReturnsKeysUnchanged`: `connection.Connected` is present.
  - `Diagnostics_RedactsTokenInLastErrors`: a recorded error containing `123456:ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789` gives text with `<token>` and without the token.
  - `Diagnostics_NeverContainsProxyPassword`.
  - `Diagnostics_OmitsChatIdsAndTitles`: a destination with `ChatId = -1001234567890` and `Title = "Secret Group"`; neither string appears.
  - `Diagnostics_ListsEachDestinationHealth`.
- [ ] **Step 2: Run them and see them fail.** Run the same command with the filters `*StringTableTests`, `*StringsControllerTests` and `*DiagnosticsReportTests`. Expected: a compile error.
- [ ] **Step 3: Implement.** Use the same attributes as the other controllers: `[ApiController] [Route("TgJelly")] [Authorize(Policy = Policies.RequiresElevation)]`.
- [ ] **Step 4: Run the filters again, then the full suite.** Expected: PASS.
- [ ] **Step 5: Commit:** `feat: strings and diagnostics endpoints`

---

### Task 3: Scriban template engine and the Telegram HTML validator

**Files:**
- Modify: `Jellyfin.Plugin.TgJelly.csproj` (add `<PackageReference Include="Scriban" Version="7.5.1" />`), `build/package.py` (`ASSEMBLIES` gets `"Scriban.dll"`; update the comment), `build/test_package.py`
- Create: `Templates/RawHtml.cs`, `Templates/TelegramTemplateContext.cs`, `Templates/TemplateError.cs`, `Templates/TemplateRenderResult.cs`, `Templates/TemplateEngine.cs`, `Rendering/TelegramHtmlValidator.cs`
- Test: `tests/.../Templates/TemplateEngineTests.cs`, `tests/.../Rendering/TelegramHtmlValidatorTests.cs`

**Interfaces:**
- Produces:
  - `sealed record RawHtml(string Html)`.
  - `sealed record TemplateError(string Message, int? Line, int? Column)`. `Line` and `Column` are 1-based.
  - `sealed record TemplateRenderResult(string? Output, TemplateError? Error)`, with `bool Ok => Error is null`.
  - `sealed class TelegramTemplateContext : TemplateContext`. It overrides `Write(SourceSpan, object?)`: `RawHtml` → its `Html`; `null` → nothing; anything else → `HtmlText.Escape(<Scriban's ToString of the value>)`. Literal text is written through the base path unescaped.
  - `sealed class TemplateEngine(TimeProvider time)` with:
    - `IReadOnlyList<TemplateError> Parse(string text)`: every parse message, 1-based. An empty list means it parsed;
    - `TemplateRenderResult Render(string eventKey, string text, IReadOnlyDictionary<string, object?> model, bool strict)`.

    It caches parsed templates by `(eventKey, TemplateText.Hash(text))`. `TemplateText` is created here: `static string Hash(string text)` returns the first 16 lowercase hex characters of SHA-256 over the UTF-8 text. The cache is an instance field (a bounded `ConcurrentDictionary`, at most 200 entries, cleared when full). It is never static.

    The model converts to a `ScriptObject`: strings, numbers and bools as-is; `IReadOnlyList<object?>` → `ScriptArray`; `IReadOnlyDictionary<string, object?>` → `ScriptObject`. `raw` is imported as a function returning `RawHtml`. Every limit in Global Constraints is set on each context. An exception (a limit, a timeout, a runtime error) becomes `Error` with the span's 1-based position when it has one. The method never throws.
  - `static class TelegramHtmlValidator`:
    - `IReadOnlyList<string> Validate(string html)` lists the problems: a tag that isn't allowed; a `span` without `class="tg-spoiler"`; a `blockquote` attribute other than `expandable`; an unclosed tag, a stray closing tag, or tags closed out of order.
    - Entities and plain text pass. Void tags don't exist in this subset.
- Consumes: nothing new.

- [ ] **Step 1: Write the failing tests.**
  - `Escapes_ExpressionResults`: `{{ x }}` with `x = "<b>&"` gives `&lt;b&gt;&amp;`.
  - `Raw_IsNotEscaped`: `{{ raw x }}` gives `<b>&`.
  - `Literal_IsNotEscaped`: `<b>{{ x }}</b>` gives `<b>…</b>`.
  - `ParseErrors_Are1Based`: `"a\n{{ if }}"` reports line 2.
  - `LoopLimit_Trips`: `{{ for i in 1..100000 }}x{{ end }}` gives an error, not output.
  - `OutputLimit_Trips`: `{{ for i in 1..900 }}{{ "x" | string.pad_right 100 }}{{ end }}` gives an error.
  - `Recursion_Trips`: a recursive `func` gives an error.
  - `Timeout_Trips`: a busy loop under the loop limit but over 2 s, using a fake slow function registered in the test model, gives an error within 3 s wall time.
  - `Strict_ReportsUnknownField_WithPosition`.
  - `NotStrict_UnknownFieldIsEmpty`.
  - `ArraysAndObjects_Render`: `{{ for e in episodes }}{{ e.number }}{{ end }}`.
  - `SameText_IsParsedOnce`: render twice and assert through a test hook, `internal int CachedCount`.

  `TelegramHtmlValidatorTests`:
  - a theory of allowed fragments that give no problems;
  - a theory of forbidden fragments (`<div>`, `<span>x</span>`, `<blockquote foo>`, `<b><i></b></i>`, `<b>`) that give one problem each;
  - `Entities_Pass`.

  `build/test_package.py`: assert that `ASSEMBLIES` contains `Scriban.dll` and that `verify_zip` accepts a zip with the three DLLs plus `meta.json`.
- [ ] **Step 2: Run and see them fail.** Run the C# filters `*TemplateEngineTests` and `*TelegramHtmlValidatorTests`, and `python3 -m unittest discover -s build -p 'test_*.py'`. Expected: compile errors and a Python failure.
- [ ] **Step 3: Implement.** Register `TemplateEngine` as a singleton.
- [ ] **Step 4: Run them again.** Then run the full suite, and package: `python3 build/package.py --release 0.0 --changelog "local build" --output artifacts && python3 -m zipfile -l artifacts/tgjelly_2.0.0.0.zip`. Expected: PASS, and the zip lists exactly the three DLLs and `meta.json`.
- [ ] **Step 5: Commit:** `feat: scriban template engine with telegram escaping and limits`

---

### Task 4: Template fields, models and `docs/templates.md`

**Files:**
- Create: `Templates/TemplateField.cs`, `Templates/TemplateFieldCatalog.cs`, `Templates/TemplateModelBuilder.cs`, `docs/templates.md`
- Test: `tests/.../Templates/TemplateFieldCatalogTests.cs`, `tests/.../Templates/TemplateModelBuilderTests.cs`, `tests/.../Templates/TemplateDocsTests.cs`

**Interfaces:**
- Produces:
  - `sealed record TemplateField(string Name, string Type, string Description, object? Sample, bool Optional)`. `Type` is one of `string`, `number`, `bool`, `string[]`, `episode[]`.
  - `static class TemplateFieldCatalog` with:
    - `IReadOnlyList<TemplateField> For(string eventKey)`;
    - `bool IsMedia(string eventKey)`: `media.*`;
    - `IReadOnlyDictionary<string, object?> SampleFull(string eventKey)`;
    - `IReadOnlyDictionary<string, object?> SampleSparse(string eventKey)`: optional fields null, arrays empty.

    It covers every key in `EventCatalog.AllKeys`.
  - `sealed class TemplateModelBuilder(ILibraryAccess library, LinkBuilder links, SettingsStore settings, Formatter formatter, TimeProvider time)` with `IReadOnlyDictionary<string, object?> Build(RenderModel model)`. It returns exactly the catalog's field names for `model.EventKey`. Values are read from `MediaItem`s (media) or `model.Values` (alerts). Missing data is `null`, or an empty array.
- Consumes: `RenderModel`, `MediaItem`, `EventFields`, `LinkBuilder.OpenInJellyfin`, `Formatter.Time`.

Fields. **Common to every key:** `event` (string), `at_unix` (number), `at_text` (string, from `Formatter.Time` without the `<tg-time>` tag, i.e. its UTC fallback text).

| Key | Fields (in addition to the common ones) |
|---|---|
| `media.movie`, `media.musicvideo` | `title`, `year`, `overview`, `rating`, `runtime_minutes`, `genres[]`, `library`, `imdb_id`, `tmdb_id`, `jellyfin_url`, `upgraded` |
| `media.episodes` | `series`, `season`, `count`, `episodes[]` {`number`, `title`}, `new_series`, `library`, `imdb_id`, `tmdb_id` (of the series), `jellyfin_url`, `upgraded` |
| `media.album` | `artist`, `album`, `year`, `tracks`, `library`, `jellyfin_url`, `upgraded` |
| `media.book` | `title`, `year`, `overview`, `parts` (audiobook parts; 0 for a `Book`), `library`, `jellyfin_url`, `upgraded` |
| `security.login_failed` | `user`, `attempts`, `ip_count`, `last_ip`, `client`, `device`, `last_attempt_unix`, `last_attempt_text`, `locked` |
| `security.lockout` | `user`, `attempts` |
| `security.login_succeeded` | `user`, `client`, `device`, `ip` |
| `security.user_created`, `user_deleted`, `password_changed` | `user` |
| `server.started` | `version`, `server` |
| `server.update_available` | `version`, `current`, `url` |
| `server.plugin_installed`, `plugin_uninstalled` | `plugin`, `version` |
| `server.plugin_updated` | `plugin`, `version`, `changelog` |
| `server.plugin_install_failed` | `plugin`, `version`, `error` |
| `server.task_failed` | `task`, `error` |
| `server.restart_pending` | `reasons[]` (each of `model.Lines` passed through `HtmlText.ToPlain`) |

- **Alert field sources.** Alert fields map name-for-name to the `EventFields` value keys that `AlertLayouts` reads for that event. The exceptions: `last_ip` ← `ip`; `last_attempt_*` ← `model.At`. Check each row against `AlertLayouts` and the intake producers, and drop a field that no producer sets (and record the drop in your report). Don't invent one.
- **Types.** Numbers parse with the invariant culture, and a value that won't parse becomes `null`. `locked`, `upgraded` and `new_series` are bools. `season` and `number` are numbers; Specials are 0.

`docs/templates.md` has two parts:
- an intro, covering scope, escaping and `raw()`, limits, digests, and the flags and fallbacks;
- one section per key with a field table (name, type, description, optional) and an example template.

It is generated from the catalog by `TemplateDocs.Render()`, an `internal static` method in the test project. The test compares the committed file with the generated text. Setting `TGJELLY_WRITE_DOCS=1` rewrites the file instead.

- [ ] **Step 1: Write the failing tests.**
  - `EveryCatalogKey_HasFields`.
  - `CommonFields_OnEveryKey`.
  - `Movie_FromMediaItem`: title, year, `genres` array, `runtime_minutes` 134 for 2h14m, `jellyfin_url` with a valid public URL, and `null` without one.
  - `Episodes_SortedByNumber_WithTitles`.
  - `Audiobook_PartsCount`.
  - `LoginFailed_FromValues`: `last_ip`, `ip_count` as a number, `locked` as a bool.
  - `RestartPending_ReasonsArePlainText`.
  - `MissingItems_GiveNulls_NotThrow`.
  - `SampleFull_RendersEveryTemplateFieldStrictly`: for each key, a template that references every field renders with `strict: true` and no error.
  - `SampleSparse_OptionalFieldsNull`.
  - `Docs_MatchCatalog`.
- [ ] **Step 2: Run and see them fail:** filters `*TemplateFieldCatalogTests`, `*TemplateModelBuilderTests`, `*TemplateDocsTests`.
- [ ] **Step 3: Implement.** Generate `docs/templates.md` with `TGJELLY_WRITE_DOCS=1`, then read it once for tone (plain, short sentences).
- [ ] **Step 4: Run the filters again, then the full suite.**
- [ ] **Step 5: Commit:** `feat: template fields, models and docs/templates.md`

---

### Task 5: Templates at send time, and template flags

**Files:**
- Create: `State/Documents/TemplateFlag.cs`, `Templates/TemplateFlags.cs`
- Modify: `State/Documents/StatusSection.cs` (`TemplateFlags`), `State/Documents/OutboxMessage.cs` (`TemplateKey`), `Rendering/NotificationRenderer.cs`, `Delivery/OutboxDelivery.cs` (the ParseError case), `Status/NoticeBuilder.cs` (the template row), `Status/DiagnosticsReport.cs` (the templates section), `en.json`
- Test: `tests/.../Rendering/NotificationRendererTemplateTests.cs`, `tests/.../Templates/TemplateFlagsTests.cs`, plus a case in the existing `OutboxDelivery` tests, and a case in `NoticeBuilderTests`

**Interfaces:**
- Produces:
  - `TemplateFlag { Kind: string ("error" | "truncated"), Error: string, At: DateTimeOffset, TextHash: string }`.
  - `StatusSection.TemplateFlags: Dictionary<string, TemplateFlag>` (ordinal), keyed by event key.
  - `sealed class TemplateFlags(StateStore state, SettingsStore settings, TimeProvider time)` with:
    - `void Flag(string eventKey, string kind, string error, string textHash)`: it writes only when the kind, error or hash differs from the stored flag;
    - `void Clear(string eventKey)`;
    - `TemplateFlag? Current(string eventKey)`: returns the flag only while the template is enabled and its `TemplateText.Hash` equals `TextHash`, so a flag clears by itself when the text or `Enabled` changes, even after an edit outside the API (§8.5).
  - `OutboxMessage.TemplateKey: string?`: the event key whose template made this message, or `null`.
  - `NotificationRenderer` gains the constructor parameters `TemplateEngine engine`, `TemplateModelBuilder models`, `TemplateFlags flags` and `SettingsStore settings`.
- Consumes: Tasks 3 and 4.

Send-time algorithm in `Render`:
1. Build the default message as today.
2. **When to try a template.** Only when `model.Kind != Digest` and the settings have an override for `model.EventKey` with `Enabled` and non-blank `Text`.
3. **Render.** Call `engine.Render(key, text, models.Build(model), strict: false)`.
4. **A render error** → `flags.Flag(key, "error", error text with "line L, column C" when known, hash)`. Keep the default message.
5. **Otherwise validate.** Run `TelegramHtmlValidator.Validate(output)`. If it reports problems → flag `"error"` with the first problem and keep the default message.
6. **Otherwise use the output:**
   - `Caption = Fit(output, CaptionLimit - DelayedReserve)` and `Text = Fit(output, TextLimit - DelayedReserve)`, where `Fit` is `HtmlText.Truncate(html, budget, "…")` when `HtmlText.PlainLength(html) > budget`;
   - `TemplateKey = key`;
   - **truncated flag:** if the output exceeds the budget of the type chosen now (the caption when `PosterPath` is set, otherwise the text) → flag `"truncated"`; otherwise `flags.Clear(key)`. Poster, buttons and `Silent` are unchanged.
7. **Never throw.** The whole template step is inside a catch-all that flags `"error"` and keeps the default message.

**Delivery.** In `OutboxDelivery`'s `ParseError` case, when `message.TemplateKey` is set, call `flags.Flag(key, "error", error.Description, hash of the current template text)` before switching to plain text.

**Notice row (Task 1's slot).** One row per flag where `Current(key)` is non-null:
- `Kind = "TemplateFlagged"`, `Id = template:<key>:<At unix s>`;
- text `notice.templateError` `{event}` `{error}`, or `notice.templateTruncated` `{event}`, where `{event}` is the `dest.event.<key>` label.

- [ ] **Step 1: Write the failing tests.**
  - `EnabledTemplate_ReplacesCaptionAndText_KeepsPosterAndButtons`.
  - `DisabledOrBlank_UsesDefault`.
  - `Digest_NeverUsesTemplate`.
  - `RenderError_FallsBackAndFlags`.
  - `MissingField_RendersEmpty_NoFlag`.
  - `ForbiddenTag_FallsBackAndFlags`.
  - `OverCaption_TruncatesAndFlagsTruncated`: a 2000-character output with a poster.
  - `OverCaption_NoPoster_UnderTextLimit_NotFlagged`.
  - `InfiniteLoop_FallsBackWithin3s`.
  - `Coalesced_ReRender_UsesTemplate`: a `login_failed` model with attempts 1, then 5, renders "×5" from `{{ attempts }}`.
  - `Silent_StillApplied`.

  `TemplateFlagsTests`:
  - `SameFlag_NoSecondWrite`;
  - `TextChanged_FlagNotCurrent`;
  - `Disabled_FlagNotCurrent`.

  Delivery:
  - `ParseError_WithTemplate_FlagsIt`.

  `NoticeBuilderTests`:
  - `TemplateFlag_GivesNotice_UntilTextChanges`.
- [ ] **Step 2: Run and see them fail.**
- [ ] **Step 3: Implement.** Update DI and the existing test rigs that construct `NotificationRenderer`.
- [ ] **Step 4: Run the filters, then the full suite.**
- [ ] **Step 5: Commit:** `feat: custom templates at send time, with flags and notices`

---

### Task 6: Templates API

**Files:**
- Create: `Api/TemplatesController.cs`, `Api/TemplatesDto.cs`, `Api/TemplateItemDto.cs`, `Api/TemplateFieldDto.cs`, `Api/TemplateFlagDto.cs`, `Api/TemplateUpdateRequest.cs`, `Api/TemplateValidateRequest.cs`, `Api/TemplateValidationDto.cs`
- Test: `tests/.../Api/TemplatesControllerTests.cs`

**Interfaces:**
- Produces:
  - `GET TgJelly/Templates` → `TemplatesDto { Revision, Items }`. There is one `TemplateItemDto` per `EventCatalog.AllKeys` entry, in catalog order:
    - `{ EventKey, Label (dest.event.<key>), Media (bool), Enabled, Text, Fields: TemplateFieldDto[] {Name, Type, Description, Optional}, SampleFull, SampleSparse, Flag: TemplateFlagDto? {Kind, Error, At} }`;
    - keys without an override have `Enabled = false` and `Text = ""`.
  - `PUT TgJelly/Templates/{eventKey}` with `TemplateUpdateRequest { ExpectedRevision, Enabled, Text }`:
    - an unknown key → 404;
    - `Text` longer than 16 384 characters → 400 `ErrorsDto` with field `Text`;
    - a stale revision → 409 `ConflictDto<TemplatesDto>`;
    - otherwise 200 `TemplatesDto`.

    It saves through `SettingsStore.UpdateIf`. A change of `Text` or `Enabled` calls `TemplateFlags.Clear(key)`. `Enabled = false` with empty text removes the override entry, and a request that changes nothing doesn't bump the revision.
  - `POST TgJelly/Templates/Validate` with `TemplateValidateRequest { EventKey, Text }` → `TemplateValidationDto { Errors: TemplateErrorDto[] {Message, Line?, Column?}, Warnings: string[], Preview: string, Length: int, Limit: int }`. The steps follow §8.5:
    1. **Parse.** On errors, return only them.
    2. **Strict renders.** Render with `SampleFull`, then with `SampleSparse`, both with `strict: true`, and report each error once with its position.
    3. **Whitelist.** Check the full render's output with `TelegramHtmlValidator`. Each problem is an error.
    4. **Length.** Measure `HtmlText.PlainLength`. For media keys the limit is 1024: over 1024 but at most 4096 is a warning, `templates.willShorten` ("will be shortened when sent with a poster"); over 4096 is an error. For other keys, over 4096 is an error.
    5. **Preview.** `Preview` is the full render's output, and `Limit` is 1024 for media keys or 4096 otherwise.
- Consumes: Tasks 3–5.

- [ ] **Step 1: Write the failing tests.**
  - `Get_ListsEveryKey_WithFieldsAndSamples`.
  - `Put_SavesAndBumpsRevision`.
  - `Put_SameValues_NoRevisionBump`.
  - `Put_ClearsFlag`.
  - `Put_DisableEmpty_RemovesEntry`.
  - `Put_UnknownKey_404`.
  - `Put_TooLong_400`.
  - `Put_Stale_409_WithCurrent`.
  - `Validate_ParseErrorsOnly`.
  - `Validate_UnknownField_WithPosition`.
  - `Validate_SparseModel_CatchesNullUse`: `{{ overview | string.size }}` passes, while `{{ episodes[0].title }}` fails sparse.
  - `Validate_ForbiddenTag`.
  - `Validate_MediaOver1024_Warns`.
  - `Validate_TextOver4096_Errors`.
  - `Validate_ReturnsPreview`.
- [ ] **Step 2: Run and see them fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the filters, then the full suite.**
- [ ] **Step 5: Commit:** `feat: templates api with validation`

---

### Task 7: Page foundation, the Status section and the web test harness

**Files:**
- Modify: `Web/tgjelly.html`, `Web/tgjelly.js`, `Strings/en.json` (`web.*` keys), `.github/workflows/ci.yml` (Node step), `tests/.../WebPagesTests.cs`
- Create: `tests/web/tgjelly.test.mjs`, `tests/web/fake-view.mjs`

**Interfaces:**
- Produces (named exports of `tgjelly.js`):
  - `esc(value) → string`. It escapes `& < > " '`, and `null` or `undefined` give `''`.
  - `t(strings, key, args = {}) → string`. It fills `{name}` placeholders with escaped args, and returns the key itself when it is missing.
  - `plural(strings, key, count, args = {}, lang = 'en') → string`. It picks `key.one` or `key.other` with `Intl.PluralRules(lang)` and passes `count` as an arg.
  - `mergeConflict(base, local, server) → { merged, changed }`:
    - all three are flat objects, so nested values use dotted paths: `flatten(obj)` and `unflatten(obj)` are also exported;
    - `merged` = server, with every path where `local` differs from `base` taken from `local`;
    - `changed` = the paths where `server` differs from `base` **and** `local` differs from `base`.
  - `renderStatus(status, strings) → html`. It covers:
    - the connection message;
    - the CompatGuard banner when `!Compatible`;
    - `[Take over]` (`data-tgj-act="takeover"`) when `ConnectionState === 'WebhookConflict'`;
    - last errors;
    - counts (pending media, outbox, dropped);
    - notices, each with `[Dismiss]` (`data-tgj-act="dismiss" data-tgj-id="<Id>"`).
  - `createApi(apiClient) → { get(path), post(path, body), put(path, body), del(path) }`. Each call is `apiClient.fetch({ url: apiClient.getUrl('TgJelly/' + path), type, data: JSON.stringify(body), contentType: 'application/json', dataType: 'json' })`. It resolves `{ status, body }` and treats a 4xx as a value, not a throw.
- The controller (default export) does the following:
  - **State.** `{ strings, status, settings, destinations, libraries, links, templates, ui }` in the closure.
  - **On `viewshow`.** Load the strings (if not loaded yet) and Status, render Status, then call the section loaders. Tasks 8–12 add those to a `loaders` array, so this task ships an empty array plus Status.
  - **Clicks.** One `view.addEventListener('click', …)` delegates on `closest('[data-tgj-act]')` to an `actions` map, which later tasks extend.
  - **Polling.** As in Global Constraints. It re-renders only `#tgj-status`.
  - **Fallback.** If `GET Strings` fails, show the single hard-coded line "Could not load TgJelly." and log the error.
- **HTML.** The shell holds these containers, in this order:
  - `#tgj-status`, `#tgj-connect`, `#tgj-bot`, `#tgj-destinations`, `#tgj-messages`;
  - then `<details id="tgj-advanced" class="tgj-advanced"><summary>…</summary>`, containing `#tgj-proxy`, `#tgj-batching`, `#tgj-updates`, `#tgj-templates`, `#tgj-manual` and `#tgj-diagnostics`;
  - a `<style>` scoped to `#tgjConfigPage`, with the classes `.tgj-badge`, `.tgj-changed` (highlight with `var(--jf-palette-warning-main)`), `.tgj-error` and `.tgj-notice`.

  The summary text is filled in by JS, because the HTML holds no text that needs strings. Give it a `tgj-advanced-title` span.
- **Test harness.**
  - `tests/web/fake-view.mjs` exports:
    - `makeView(ids)`: an `EventTarget` with `isConnected`, `classList`, and `querySelector('#id')` returning fake elements that record `innerHTML`/`textContent`/`hidden`;
    - `makeApiClient(routes)`: `getUrl` prefixes `/jf/`, and `fetch` records calls and answers from `routes['GET TgJelly/Status']` and so on;
    - `fakeTimers()`.
  - The tests import the module with `import page, * as m from '../../src/Jellyfin.Plugin.TgJelly/Web/tgjelly.js'`. If Node won't load the `.js` file as ESM, add `tests/web/package.json` `{ "type": "module" }` and import through a copy written to a temp `.mjs`, as a helper in `fake-view.mjs`.
  - Run with `node --test tests/web/`.
  - CI adds `actions/setup-node@v4` with `node-version: 22`, plus the steps `node --check src/Jellyfin.Plugin.TgJelly/Web/tgjelly.js` and `node --test tests/web/`.

- [ ] **Step 1: Write the failing tests** in `tgjelly.test.mjs`:
  - `esc escapes all five`.
  - `t fills and escapes args`, plus `t returns key when missing`.
  - `plural picks one/other`.
  - `mergeConflict keeps local edits, takes server elsewhere, reports both-changed paths`: base `{a:1,b:1,c:1}`, local `{a:2,b:1,c:3}`, server `{a:1,b:5,c:4}` give merged `{a:2,b:5,c:3}` and changed `['c']`.
  - `renderStatus escapes notice text`: a notice text `<img src=x onerror=alert(1)>` is absent raw and present escaped.
  - `renderStatus shows take over only on WebhookConflict`.
  - `viewshow twice binds one poller`: fake timers, two `viewshow` events, one active interval.
  - `polling stops on viewhide and when hidden`.
  - `all requests go through getUrl`: every recorded URL starts with `/jf/TgJelly/`.
  - `dismiss posts the notice id`.
  - `every t(…) key used in tgjelly.js exists in en.json`: a regex scan of `t(S, '…'` and `plural(S, '…'` call sites, where `S` is the strings variable name you pick. Keep it one name.

  `WebPagesTests`: keep the existing assertions, and add `Html_HasEverySectionContainer` (the IDs above).
- [ ] **Step 2: Run** `node --test tests/web/` and the C# `*WebPagesTests` filter. Expected: FAIL.
- [ ] **Step 3: Implement.** Move plan 1's hard-coded page strings to `web.*` keys.
- [ ] **Step 4: Run again, then the full suite.** Expected: PASS.
- [ ] **Step 5: Commit:** `feat: dashboard page foundation and status section`

---

### Task 8: Connecting a bot (first run, the Bot section, proxy)

**Files:**
- Modify: `Web/tgjelly.js`, `Strings/en.json`
- Test: `tests/web/tgjelly.test.mjs`

**Interfaces:**
- Consumes:
  - Task 7's helpers;
  - `GET Status` (`HasToken`, `BotUsername`, `ConnectionState`);
  - `POST Token` → `TokenResponse { Outcome: Connected|NeedsConfirm|FormatInvalid|Rejected|Unreachable|Failed, Message, BotUsername, WebhookDeleted }`;
  - `DELETE Token`;
  - `GET Links` (`OpenBot`, `StartAdd`, …);
  - `GET Settings` (`ProxyMode`, `ProxyUrl`, `HasProxyUsername`, `HasProxyPassword`);
  - `PUT Proxy`.
- Produces:
  - `renderConnect(ui, strings, links) → html` (§6.1). When `!HasToken`, or while `ui.changingToken`, it shows a card titled `web.connect.title` "Connect a Telegram bot", with `[Create a new bot]` and `[I already have a bot]`.
    - **Create a new bot:** three numbered steps.
      1. Open @BotFather (`https://t.me/BotFather`) and send `/newbot`.
      2. "In BotFather's Mini App, restrict access to yourself and anyone else who should manage Jellyfin (up to 10 people)."
      3. Copy the token and paste it below.

      Then the same token form.
    - **The token form:** a password-type token input; the two notes from §6.1, shown only for the existing-bot path; the `setProfile` checkbox, on by default, with the exact §6.1 label; and `[Save]`.
    - **Outcomes** (no button is enabled while a save is in flight):
      - `Connected` shows "Connected as @x" and `[Open @x]` (`Links.OpenBot`);
      - `NeedsConfirm` shows `Message`, `[Switch bot]` (re-POST with `ConfirmSwitch: true`) and `[Cancel]`;
      - `Unreachable` opens the "Can't reach Telegram?" panel with `renderProxyForm`;
      - any other outcome shows `Message`, escaped.
  - `renderBot(status, links, strings, ui) → html` (§6.6.2). It shows:
    - `@username`;
    - the reminder "Anyone who can open @bot can manage notifications. Keep it restricted in BotFather.";
    - `[Change token]`, which sets `ui.changingToken` and reuses the token form;
    - `[Disconnect bot]`, with a two-step inline confirm, then `DELETE Token`.
  - `renderProxyForm(settings, strings, ui) → html`. It has:
    - the mode select (System, None, Http, Socks5);
    - a URL field, shown for Http and Socks5;
    - username and password fields, empty with a "(saved)" placeholder when one is stored. An untouched field is omitted from the PUT; a cleared one sends `""`.

    The same function serves `#tgj-proxy` in Advanced and the Unreachable panel. Saving uses Task 7's 409 merge.

- [ ] **Step 1: Write the failing tests:**
  - `first run shows both choices`;
  - `create-new steps mention the Mini App`;
  - `token input is type=password and never pre-filled`;
  - `NeedsConfirm then Switch sends ConfirmSwitch`;
  - `Unreachable opens the proxy panel`;
  - `Connected shows open link from Links.OpenBot`;
  - `disconnect needs two clicks`;
  - `proxy untouched password is omitted, cleared sends empty`;
  - `proxy 409 keeps typed url`;
  - `bot username is escaped`.
- [ ] **Step 2: Run them and see them fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run them again.**
- [ ] **Step 5: Commit:** `feat: connect a bot from the dashboard`

---

### Task 9: The Destinations section

**Files:**
- Modify: `Web/tgjelly.js`, `Strings/en.json`, `Api/DestinationsDto.cs` and its mapper (add `Catalog`), `tests/.../Api/DestinationsControllerTests.cs`
- Create: `Api/CatalogDto.cs`
- Test: `tests/web/tgjelly.test.mjs`

**Interfaces:**
- Produces:
  - `CatalogDto { EventKeys: string[], Groups: string[], Presets: Dictionary<string, string[]> }`. `Presets` comes from `EventCatalog.Expand` for Everything, NewMedia and AdminAlerts.
  - `DestinationsDto.Catalog`.
  - `renderDestinations(dto, libraries, links, strings, ui) → html` (§6.6.3). Each destination is one row:
    - the title (escaped), the kind, and a health badge from `dest.health.<Status>` (plus `dest.health.PhotosBlocked` when set);
    - a Pause/Resume `emby-checkbox` (`data-tgj-act="enabled"`), which sends a `PUT` with only `Enabled`;
    - `[Edit]`.

    Below the list are `[Add destination]` (a link to `Links.StartAdd`, `target="_blank"`) and, when `ManualUnlocked`, the link "Telegram setup didn't work? **Set up manually**" (`data-tgj-act="open-manual"`). That link opens `#tgj-advanced` and scrolls to `#tgj-manual`.
  - `renderEditor(dest, catalog, libraries, strings, ui) → html`:
    - **Preset select.** Its options come from `dest.preset.*`.
    - **Event switches,** grouped by group and labelled by `dest.event.<key>`. Toggling one sends `Events` and the server makes it Custom. Choosing a preset sends `Preset`.
    - **Library checkboxes** from `GET Libraries`, with "all libraries" when none are checked. `RemovedLibraryIds` are listed as "(removed library)" with `[Remove]`, and `LibraryFilterBroken` shows `web.dest.librariesRemoved`.
    - **Silent groups:** checkboxes from `groups.*`.
    - **Buttons:** `[Save]`, which sends the changed fields with `ExpectedRevision` and uses the 409 merge; `[Send test]` (`POST …/Test`, which shows `Sent`/`Error` and the new health); `[Check again]` (`POST …/Check`); `[Remove]`, a two-step confirm with a "Bot leaves the chat" checkbox → `DELETE Destinations/{id}?leave=<bool>&expectedRevision=<n>`.
- Consumes: `GET Destinations`, `GET Libraries`, `GET Links`, `PUT Destinations/{id}`, and the endpoints above.

- [ ] **Step 1: Write the failing tests.**
  - C#: `Get_IncludesCatalog`.
  - JS:
    - `titles are escaped`, using `<script>`;
    - `health badge text from strings`;
    - `pause toggle sends only Enabled with revision`;
    - `preset change sends Preset`;
    - `event toggle sends Events`;
    - `removed library listed and removable`;
    - `remove needs confirm and sends leave flag`;
    - `editor 409 keeps local event choices and highlights both-changed`;
    - `set up manually link only when ManualUnlocked`.
- [ ] **Step 2: Run them and see them fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run them again, plus the C# filter.**
- [ ] **Step 5: Commit:** `feat: destinations in the dashboard`

---

### Task 10: The manual block and detected chats

**Files:**
- Modify: `Web/tgjelly.js`, `Strings/en.json`
- Test: `tests/web/tgjelly.test.mjs`

**Interfaces:**
- Produces: `renderManual(dto, strings, ui) → html` for `#tgj-manual`. It renders only when `dto.ManualUnlocked`, and otherwise returns `''`.
  - **"Add chat by ID".** One input accepts a chat ID (`-100…`) or a topic link (`https://t.me/c/…/…` or `https://t.me/<name>/<n>`). The page sends `ChatId` when the value parses as an integer and `TopicLink` otherwise. Add a preset select (default `Everything`) and `[Add]`, which calls `POST Destinations`.
  - **The add result** shows the outcome (`Added` / `AlreadyExists`), the test result, and any `Errors[]`, all escaped. On success it reloads Destinations.
  - **Detected chats.** Each entry reads "Detected: *Title*, set it up?" with a preset select, `[Set up]` (`POST DetectedChats/{chatId}` `{Preset}`) and `[Dismiss]` (`DELETE DetectedChats/{chatId}`). A non-2xx answer shows its error.
- Consumes: `DestinationsDto.DetectedChats`, `ManualUnlocked`.

- [ ] **Step 1: Write the failing tests:**
  - `manual block hidden when locked`;
  - `integer input sends ChatId, link sends TopicLink`;
  - `detected title escaped`;
  - `set up sends preset`;
  - `dismiss sends DELETE`;
  - `add errors are shown escaped`.
- [ ] **Step 2: Run them and see them fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run them again.**
- [ ] **Step 5: Commit:** `feat: manual setup and detected chats in the dashboard`

---

### Task 11: Messages, batching, update checks and diagnostics

**Files:**
- Modify: `Web/tgjelly.js`, `Strings/en.json`
- Test: `tests/web/tgjelly.test.mjs`

**Interfaces:**
- Produces:
  - `renderMessages(settings, strings, ui) → html` (§6.6.4). It contains:
    - **Public URL.** The input is followed by `PublicUrlSuggestion` with the label "verify" and `[Use suggested]`. When the value isn't empty and `!PublicUrlValid`, show `web.messages.urlInvalid`, which explains that buttons are left out and lists the rule: http(s), a host with a dot, not IPv6 (§8.3).
    - **The six §8.4 toggles.**
  - `renderBatching(settings, strings, ui) → html`. The six batching fields are numeric inputs with `min`/`max` from §10.1, and each field's range is shown in its description.
  - `renderUpdates(settings, strings, ui) → html`. The update-check enabled checkbox, and the mode select (SameMajor / AnyNewer).
  - **One settings model.** All three sections edit one model and share one `PUT Settings` (the full `SettingsUpdateRequest`), and each section has its own `[Save]`.
    - A 400 `Errors[]` is shown next to the field whose `Field` matches the input's `data-tgj-field` path, such as `Batching.QuietWindowSeconds`.
    - A 409 uses the merge.
  - `#tgj-diagnostics`: `[Copy diagnostics]` calls `GET Diagnostics`, then `navigator.clipboard.writeText`. If that fails, it shows the text in a read-only `<textarea>`, selected.
- Consumes: `GET/PUT Settings`, `GET Diagnostics`.

- [ ] **Step 1: Write the failing tests:**
  - `use suggested fills the field`;
  - `invalid url explains`;
  - `toggles saved in Message`;
  - `range error shown next to its field`;
  - `settings 409 keeps typed batching value`;
  - `copy diagnostics falls back to a textarea when clipboard rejects`.
- [ ] **Step 2: Run them and see them fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run them again.**
- [ ] **Step 5: Commit:** `feat: message, batching and update settings in the dashboard`

---

### Task 12: The template editor

**Files:**
- Modify: `Web/tgjelly.js`, `Strings/en.json`
- Test: `tests/web/tgjelly.test.mjs`

**Interfaces:**
- Produces: `renderTemplates(dto, strings, ui) → html` for `#tgj-templates` (§8.5). It contains:
  - **A short intro:** the text or caption only, plus a link to `docs/templates.md` on GitHub (`https://github.com/MrSnowball-dev/tgjelly_plugin/blob/main/docs/templates.md`).
  - **An event select** using the items' `Label`, marked with a dot when `Enabled`.
  - **For the selected key:** the flag, if any (`web.templates.flagged` plus the escaped error); the Enabled checkbox; a `<textarea class="tgj-template-text">`; the field table (name, type, optional, description); `[Insert sample]`, which fills a one-line example built from the field names; `[Validate]`; and `[Save]`.
  - **For `media.*` keys:** a note that digests always use the default layout.
  - **Validate result:** errors as "line L, column C: message", warnings, `Length / Limit`, and the preview **as escaped text** in a `<pre>`.
  - **Save** sends `PUT Templates/{key}` with the revision and uses the 409 merge over `{Enabled, Text}`.
- Consumes: Task 6's API.

- [ ] **Step 1: Write the failing tests:**
  - `preview is escaped, not markup`: a preview `<b onclick=x>` shows escaped;
  - `errors show line and column`;
  - `save sends revision`;
  - `409 keeps typed text`;
  - `flag shown for flagged key`;
  - `media keys mention digests`.
- [ ] **Step 2: Run them and see them fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run them again.**
- [ ] **Step 5: Commit:** `feat: custom template editor in the dashboard`

---

### Task 13: Smoke tests: the BaseUrl variant, templates end to end, strings (§14.3); settle Q5

**Files:**
- Modify: `tests/integration/smoke.py`, `tests/integration/test_smoke.py`, `.github/workflows/ci.yml`, `docs/superpowers/specs/2026-10-16-tgjelly-v1-design.md` (§15 Q5)

**Interfaces:**
- Produces:
  - **`smoke.py --base-url /jf`.** Before the container's first start, write `<config>/network.xml` holding `<?xml version="1.0" encoding="utf-8"?><NetworkConfiguration><BaseUrl>/jf</BaseUrl></NetworkConfiguration>` into the mounted config folder, at the path Jellyfin reads. In the official image that is `$JELLYFIN_CONFIG_DIR/network.xml` (`/config/config/network.xml`); check the image's environment. Every HTTP call is prefixed with the base URL.
    - `checks()` also asserts that `GET <base>/TgJelly/Strings?lang=en` returns JSON with `web.connect.title`.
    - Pure helper `with_base(base_url, path)`, tested in `test_smoke.py`: `("/jf","/System/Info")` → `"/jf/System/Info"` and `("","/x")` → `"/x"`.
  - **Template end to end.** In `media_and_restart_checks`, before adding the movie, call `PUT TgJelly/Templates/media.movie` with `{ExpectedRevision, Enabled: true, Text: "TGJ-TPL {{ title }}"}`. The movie's `sendPhoto` caption or `sendMessage` text must start with `TGJ-TPL`. This proves `Scriban.dll` loads in the plugin's load context on every server.
  - **CI matrix.** The four `--stub` entries each get a twin with `--stub --base-url /jf`.
- Consumes: Tasks 2, 3 and 6.

- [ ] **Step 1: Write the failing `test_smoke.py` case for `with_base`, and run** `python3 -m unittest discover -s tests/integration -p 'test_*.py'`. Expected: FAIL.
- [ ] **Step 2: Implement it, run again, then push and watch CI.** Expected: all smoke jobs green, including the four `/jf` twins.

  If Jellyfin ignores the pre-written `network.xml`, use the documented alternative: start the container once, stop it, edit `network.xml`, and start it again. Record which path worked in your report.
- [ ] **Step 3: Record Q5 in spec §15.** Take the `Content-Type` that the existing smoke check saw for `tgjelly.js` on 10.11 and 12, from the CI log; print it in `checks()` if it isn't printed already.
- [ ] **Step 4: Commit:** `ci: baseurl smoke variant and templates end to end; answer spec 15 Q5`

---

### Task 14: Release workflow, gh-pages manifest and README

**Files:**
- Create: `.github/workflows/release.yml`, `build/publish_pages.py`, `build/test_publish_pages.py`, `README.md`
- Modify: `.github/workflows/ci.yml` (adds `on: workflow_call` with inputs `release` (string, default `0.0`) and `changelog` (string)), used by the package step

**Interfaces:**
- Produces:
  - **`build/publish_pages.py`.** The command is `python3 build/publish_pages.py --pages-dir DIR --manifest artifacts/manifest.json --logo build/logo.png`. It copies the manifest into `DIR/manifest.json`, and copies the logo to `DIR/logo.png` when the logo exists and `DIR` has none.
    - It exits non-zero when the new manifest has no TgJelly entry with the GUID from §5.1.
    - Function `prepare(pages_dir: Path, manifest: Path, logo: Path | None) -> list[str]` returns the changed files.
  - **`release.yml`** runs on push of tags `v*`, with `permissions: contents: write`. Its jobs:
    1. **`ci`:** `uses: ./.github/workflows/ci.yml` with `release` = the tag without `v` and `changelog` = the annotated tag's message (`git tag -l --format='%(contents)' <tag>`, or "Release <tag>" when it is empty). The package step passes:
       - `--manifest-in pages/manifest.json`, when the `gh-pages` branch has one: the step checks out `gh-pages` into `pages/` (`continue-on-error` when the branch doesn't exist yet);
       - `--source-url-base https://github.com/MrSnowball-dev/tgjelly_plugin/releases/download/<tag>/`.
    2. **`publish`** (needs `ci`): it downloads the `packages` artifact and runs `gh release create <tag> artifacts/*.zip --title <tag> --notes "<changelog>"`. It then checks out `gh-pages` (creating an orphan branch when there is none), runs `publish_pages.py`, and commits and pushes it as `github-actions[bot]`.
  - **`README.md`**, in this order:
    1. what TgJelly does;
    2. requirements (Jellyfin 10.11 or 12);
    3. install: add the repository `https://mrsnowball-dev.github.io/tgjelly_plugin/manifest.json`, then install "TgJelly";
    4. setup: create the bot in @BotFather, then **restrict access in BotFather's Mini App** (§15 Q1), paste the token on Dashboard → Telegram, press Open @bot, and pick a chat;
    5. events and presets;
    6. custom templates (a link to `docs/templates.md`);
    7. privacy: the update check contacts `api.github.com` only while a destination subscribes; files are kept in `plugins/configurations/TgJelly/`;
    8. troubleshooting:
       - proxy;
       - a CSP or Cloudflare Rocket Loader that breaks the ES-module dashboard page (disable Rocket Loader for the Jellyfin host);
       - 409 "another app is polling" and webhook conflicts (`[Take over]`);
       - one server per config folder;
    9. building from source;
    10. license (GPL-3.0).

    Screenshots are marked `<!-- screenshot: … -->` for Task 15.
- Consumes: `build/package.py` (`--manifest-in`, `--source-url-base`), the GUID from §5.1.

- [ ] **Step 1: Write the failing tests** in `build/test_publish_pages.py`:
  - `test_copies_manifest`;
  - `test_logo_added_once`;
  - `test_rejects_manifest_without_guid`.
- [ ] **Step 2: Run** `python3 -m unittest discover -s build -p 'test_*.py'`. Expected: FAIL.
- [ ] **Step 3: Implement the script, the workflow changes and the README.** Then push. Expected: CI on the branch is still green, because `ci.yml` keeps its push and pull_request triggers.
- [ ] **Step 4: Run the tests again.** Expected: PASS.
- [ ] **Step 5: Commit:** `ci: release workflow, gh-pages manifest and readme`

---

### Task 15 (owner-gated): first release

These need the owner, or the owner's go-ahead. They are outward-facing:
- [ ] **Step 1:** The owner makes the repository public and enables GitHub Pages from `gh-pages` (§13.5 prerequisite).
- [ ] **Step 2:** The owner supplies `build/logo.png`, or approves a placeholder. Commit it as `chore: plugin logo`.
- [ ] **Step 3:** Run the §14.4 checklist with the owner on a real server: BotFather (a)–(c), the picker on phones (already checked for Q3), posters, buttons, conflicts and proxies, the chat lifecycle, the server lifecycle and late edits. Also cover §15 Q6: open the page through a deep link and record whether `emby-select` renders upgraded. Record each result in spec §14.4/§15.
- [ ] **Step 4:** README screenshots: the first-run card, the Destinations section and the bot's onboarding, taken on the owner's server. Replace the markers. Commit as `docs: readme screenshots`.
- [ ] **Step 5:** The owner picks the first version (tag `vX.Y`, which gives `1.X.Y.0` and `2.X.Y.0`) and pushes the tag, or approves pushing it. Check the release has both zips and `manifest.json` on Pages, then install from the repository URL on 10.11 and on 12.

---

## Self-check against the spec (done while writing this plan)

| Spec | Task |
|---|---|
| §6.1 first run, token outcomes, needsConfirm, "Can't reach Telegram?" panel, profile checkbox | 8 |
| §6.4 access reminder in the Bot section | 8 |
| §6.5 dashboard: "Set up manually" link, manual block, detected chats | 9, 10 |
| §6.6.1 Status with connection states, last errors, counts, notices + Dismiss | 1, 7 |
| §6.6.2 Bot section, Change token, Disconnect | 8 |
| §6.6.3 Destinations, badges, Pause/Resume, editor, test/check/remove, Add destination | 9 |
| §6.6.4 Messages: public URL suggestion and validity, toggles | 11 |
| §6.6.5 Advanced: proxy, batching, update checks, templates, manual block, Copy diagnostics | 8, 10, 11, 12 |
| §8.5 engine, escaping, `raw()`, limits, cache, model, Validate 1–5, send-time rules, flags | 3, 4, 5, 6 |
| §8.5 `docs/templates.md` | 4 |
| §10.3 `status.templateFlags`, dismissed notice IDs, `publicUrlFlag` | 1, 5 |
| §10.4 revision conflicts on the page | 7, 8, 9, 11, 12 |
| §11.1 page rules, polling, strings, network | 7 |
| §11.2 `Status/Dismiss`, `Templates`, `Templates/Validate`, `Strings`, `Diagnostics` | 1, 2, 6 |
| §12 redacted diagnostics | 2 |
| §13.1 README, `docs/templates.md` | 4, 14 |
| §13.4 Scriban.dll in the whitelist | 3 |
| §13.5 `release.yml`, manifest on `gh-pages`, the repo going public | 14, 15 |
| §14.1 Scriban wrapper, HTML validator tests | 3 |
| §14.3 BaseUrl variant | 13 |
| §14.4 manual checklist | 15 |
| §15 Q5, Q6 | 13, 15 |
