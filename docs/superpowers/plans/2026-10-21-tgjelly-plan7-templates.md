# TgJelly plan 7: every message is a template — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Render every notification (all 19 event keys and the digest) from a Scriban template. The built-in templates reproduce v0.2's messages byte for byte. The dashboard gets a template editor that shows the template in use, a live Telegram-style preview and a test send.

**Architecture:**
- **Text comes from templates.** A message's text and caption come from `TemplateRenderer`. It picks the edited override or the embedded built-in template, builds a shrinkable `TemplateModel` (toggles applied), and fits it with `TemplateFitter` in v0.2's shortening order.
- **Posters and buttons stay where they are.** They still come from `MediaLayouts`/`AlertLayouts`, whose text code is deleted once golden files recorded from it pass through the template pipeline.
- **The dashboard** calls Validate on every pause in typing. It renders the returned Telegram HTML through its own whitelist re-emitter, and sends tests through a new `Templates/Test` endpoint.

**Tech Stack:**
- C# on .NET 9 and .NET 10 (track 1.x and 2.x), with Scriban 7.5.1 (pinned) and Telegram.Bot 22.10.3.3 (pinned).
- xunit.v3 and NSubstitute, with the fakes in `tests/Jellyfin.Plugin.TgJelly.Tests/Fakes`.
- Node 22 `node --test` for `tests/web/*.test.mjs`.

**Spec:**
- `docs/superpowers/specs/2026-10-21-tgjelly-templates-design.md`: the design, binding.
- `docs/superpowers/specs/2026-10-16-tgjelly-v1-design.md`: v1, everything the design doesn't change.

## Global Constraints

- **Commands:** prefix every `dotnet` command with `export PATH="$HOME/.dotnet:$PATH"`. Build and test both TFMs: `dotnet test --solution Jellyfin.Plugin.TgJelly.slnx -c Release`. Warnings are errors.
- **Commits:** use `git -c user.name="Daniil" -c user.email=igatorr19@gmail.com commit`, and end every message with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- **Built-in output:** it must equal v0.2's output byte for byte, text and caption, for every corpus case (design §2.2, §5.5). Never edit a golden file to make a test pass. A mismatch is a template or model bug.
- **Shortening order** (design §5.3), for each of the caption (1024) and the text (4096), with budget = limit − 40:
  1. the shrinkable text: the longest rune prefix that fits, plus `…`, after `TrimEnd`; dropped when the best prefix is under 20 runes;
  2. the shrinkable list keeps K items, the most that fit, never fewer than 1, with `<list>_more` = the number removed;
  3. `HtmlText.Truncate(text, budget, "…")`.
- **Escaping:** model values are escaped by `TelegramTemplateContext`. HTML values are `RawHtml`: today only `at_html`. Literal template text is not escaped.
- **Model field names:** every field in today's `TemplateFieldCatalog` keeps its name and meaning (design §5.2), so v0.2 templates keep working. New fields are added to the catalog with a description and a sample.
- **Event keys:** the 19 in `EventCatalog.AllKeys`, plus the digest's `media.digest`. The list of template keys is `TemplateKeys.All` (Task 3). `EventCatalog` itself does not change.
- **Built-in templates:** embedded resources named `Jellyfin.Plugin.TgJelly.Templates.BuiltIn.en.<event key>.sbn`, for example `…BuiltIn.en.media.movie.sbn`. A key missing in the configured language falls back to `en`.
- **Words in built-in templates:** the visible words are written in the template text (design §5.2). The exceptions are `runtime_text`'s h/m, the `tg-time` "UTC" fallback inside `at_html`, and the `reasons[]` lines.
- **Plurals:** `{{ plural count "one-form" "other-form" }}`. Forms are positional, in the language's CLDR category order; English is `one other`. The helper returns the form only. With fewer forms than categories, the last form fills the rest.
- **Dashboard:** no browser `alert`/`confirm`/`prompt` dialogs. Reset asks with an inline second button.
- **No user-facing behavior change** for an admin who never touches templates, other than the editor itself.
- **Strings:** new dashboard and API strings go in `Strings/en.json` with `web.templates.*` / `templates.*` keys. A layout string is removed from `en.json` only when `grep -rn '"<key>"' src/` shows no other user.

## Review Focus

1. **A huge batch:** a season of 1500 episodes, or a digest of 1200 entries after a first library scan. It must still produce a fitted message with "+N more" or "…and N more". It must not hit Scriban's `LoopLimit = 1000` and fall back to the last-resort text. The fitter caps the list at 1000 before the first render (Task 2 test `Fit_ListOver1000_CapsBeforeRendering`).
2. **Typing fast in the editor:**
   - Validate responses that arrive out of order must not replace a newer preview with an older one.
   - The textarea must keep focus and caret while the preview refreshes.
   - Covered by Task 8 tests `preview_ignoresStaleResponse` and `typing_doesNotRerenderTextarea`.
3. **A template that emits `<a href="javascript:alert(1)">`, or text with `<img onerror>`:** the preview shows it as text and never as a live link or element (Task 8 test `previewHtml_neutralizesUnsafeMarkup`).
4. **Send test to a destination where photos are blocked, the bot is disconnected, or the destination is lost:** the admin gets a clear error, or a text-only send when photos are blocked, never an exception (Task 7 tests).
5. **A v0.2 edited template using `rating`, `genres`, `episodes[].number`, `last_ip`:** it still renders after the upgrade. It follows the toggles now, which is the agreed change (Task 3 test `Build_V02FieldNamesStillPresent`).

---

### Task 1: Golden corpus recorded from the v0.2 layouts

Only tests change; no product code. This freezes today's output before anything changes.

**Files:**
- Create: `tests/Jellyfin.Plugin.TgJelly.Tests/Rendering/Corpus/LayoutCorpus.cs`
- Create: `tests/Jellyfin.Plugin.TgJelly.Tests/Rendering/Corpus/LayoutCorpusTests.cs`
- Create: `tests/Jellyfin.Plugin.TgJelly.Tests/Rendering/Golden/Corpus/*.txt` (generated)
- Modify: `tests/Jellyfin.Plugin.TgJelly.Tests/Jellyfin.Plugin.TgJelly.Tests.csproj` (copy `Rendering/Golden/Corpus/**` to the output, the way `Rendering/Golden/*.txt` is copied today)

**Interfaces:**
- Produces:
  - `internal sealed record CorpusCase(string Name, RenderModel Model, IReadOnlyList<MediaItem> Items, MessageOptions Options, DateTimeOffset Now)`
  - `internal static class LayoutCorpus { public static IReadOnlyList<CorpusCase> Cases { get; } public static string GoldenPath(string caseName, string part); }`, where part is `"caption"` or `"text"`.
  - `internal static class CorpusRig { public static (string Caption, string Text) RenderV02(CorpusCase c); }`. It builds a `BotTestHarness`, a `FakeLibrary` holding `c.Items`, settings with `c.Options` and public URL `https://jf.example.com`, a `FakeTimeProvider` at `c.Now`, and the current `NotificationRenderer` with no templates. Tasks 4–6 add `RenderTemplates(CorpusCase c)` next to it.
  - Golden files `Rendering/Golden/Corpus/<case>.caption.txt` and `<case>.text.txt`.

- [ ] **Step 1: Write `LayoutCorpus.Cases`.** Each case has a distinct kebab-case `Name`. Cover, at least:
  - **Movie:** full; no year; no rating; no genres; no runtime; each of ShowOverview, ShowRatingGenres and ShowPoster off; an overview of 3000 runes (forces step 1 under 1024 and fits 4096); an overview of 5000 runes (forces step 1 under both); an overview that leaves under 20 runes (dropped); `Upgraded`; empty library name; a title with `&<>`.
  - **Music video:** full.
  - **Book:** full, and with no overview.
  - **Audiobook:** three parts, with a `ContainerId` folder, and with no folder item (falls back to `ParentName`, then the item name).
  - **Episodes:** 3 episodes; `NewSeries`; season 0 (Specials); null season number; an unnumbered episode; 300 episodes with long titles (forces "+N more" under 1024 and under 4096); 1500 episodes; unsorted input order.
  - **Album:** with a year, without a year, 1 track (singular).
  - **Digest:** 3 groups in 2 libraries, with a group lacking a library ("Other"); one single episode; one multi-episode group; an album; an audiobook; a title with a year; one that overflows 4096 (60 groups with long titles); 1200 groups.
  - **Login failed:** 1 attempt; 5 attempts from 2 IPs with locked = true; at today versus another day.
  - **Lockout:** 1 and 5 attempts.
  - **Login succeeded:** with and without an IP.
  - **User created, user deleted, password changed.**
  - **Server started.**
  - **Update available:** with and without a URL.
  - **Plugin installed and uninstalled.**
  - **Plugin updated:** with a 6000-rune changelog, and with a whitespace-only changelog.
  - **Plugin install failed:** with an error.
  - **Task failed:** with and without an error.
  - **Restart pending:** 2 reasons, and 400 reasons.

- [ ] **Step 2: Write `LayoutCorpusTests`.**
  - `[Fact] Write_WhenRequested`: when env var `TGJELLY_WRITE_GOLDEN=1`, writes `RenderV02` output for every case into the **source** folder. Find it by walking up from `AppContext.BaseDirectory` to the directory containing `Jellyfin.Plugin.TgJelly.Tests.csproj`. Write with `\n` line endings and no trailing newline. Otherwise it returns immediately.
  - `[Theory] V02_MatchesGolden(string name)` (MemberData over the case names): asserts that both parts equal the files. This proves the corpus is stable and the files are current.

- [ ] **Step 3: Generate and check the files.**
  - Run: `TGJELLY_WRITE_GOLDEN=1 dotnet test --solution Jellyfin.Plugin.TgJelly.slnx -c Release --filter-method "*Write_WhenRequested"`, then the full suite.
  - Expected: one caption and one text file per case under `Rendering/Golden/Corpus/`, and `V02_MatchesGolden` passes for all cases.
  - Spot-check by eye: `movie-overview-5000.caption.txt` ends with `…` before `📁`, and `episodes-300.text.txt` has a `+N more` line.

- [ ] **Step 4: Commit.** `test: golden corpus of the v0.2 message layouts`

---

### Task 2: Plural rules, the `plural` function and `TemplateFitter`

**Files:**
- Create: `src/Jellyfin.Plugin.TgJelly/Templates/PluralRules.cs`
- Create: `src/Jellyfin.Plugin.TgJelly/Templates/TemplateModel.cs`
- Create: `src/Jellyfin.Plugin.TgJelly/Templates/TemplateFitter.cs`
- Modify: `src/Jellyfin.Plugin.TgJelly/Templates/TemplateEngine.cs` (import `plural`; `Render` gains `string language = "en"`)
- Test: `tests/.../Templates/PluralRulesTests.cs`, `TemplateFitterTests.cs`, `TemplateEngineTests.cs`

**Interfaces:**
- Produces:
  - `public static class PluralRules { public static string Pick(string language, long count, IReadOnlyList<string> forms); }`
    - English: category `one` when count == 1, else `other`.
    - An unknown language uses English.
    - With no forms it returns `""`.
  - Scriban function `plural(count, form1, form2, …)`, imported after the model the way `raw` is today, so a model key can't replace it. It calls `PluralRules.Pick` with the render's language.
  - `public sealed class TemplateModel`:
    - `public IReadOnlyDictionary<string, object?> Values { get; }`
    - `public string? ShrinkText { get; }`: the full value of the shrinkable text field, or null.
    - `public int ListCount { get; }`: 0 when the key has no shrinkable list.
    - `public IReadOnlyDictionary<string, object?> With(string? text, int listKept)`: a copy where the shrinkable text field holds `text` (null drops it), and the list keeps its first `listKept` items with the `_more` field set to the number removed.
    - `public static TemplateModel Of(IReadOnlyDictionary<string, object?> values, string? textField, string? listField, string? moreField)`: the generic rule. The list field is a top-level array.
    - `public static TemplateModel Custom(IReadOnlyDictionary<string, object?> values, string? textField, int listCount, Func<IReadOnlyDictionary<string, object?>, string?, int, IReadOnlyDictionary<string, object?>> with)`: for the digest (Task 6).
  - `public sealed record FitResult(string? Html, bool Truncated, TemplateError? Error)`
  - `public static class TemplateFitter { public const int MaxListItems = 1000; public static FitResult Fit(TemplateModel model, Func<IReadOnlyDictionary<string, object?>, TemplateRenderResult> render, int limit); }`
    - budget = `limit - MessageFitter.DelayedReserve`.
    - Steps 1–3 as in Global Constraints.
    - Before the first render, a list over `MaxListItems` is cut to `MaxListItems`, with `_more` = the rest.
    - **Step 1** binary-searches the prefix length; the predicate is "renders and fits".
    - **Step 2** binary-searches K in [1, ListCount − 1] for the largest K that fits. Length falls monotonically as K shrinks, so this equals v0.2's linear scan.
    - **Step 3** truncates the K = 1 render, or the full one when there is no list.
    - `Truncated` is true only when step 3 ran.
    - Any render error returns `Error` and a null `Html`.

- [ ] **Step 1: Write the failing tests.**
  - `PluralRulesTests`:
    - `En_OneAndOther`: (1, ["episode","episodes"]) → "episode"; 0 → "episodes"; 2 → "episodes".
    - `MissingForms_UseLast`: (2, ["x"]) → "x".
    - `UnknownLanguage_UsesEnglish`.
  - `TemplateEngineTests`:
    - `Plural_Function`: `{{ plural 3 "item" "items" }}` → `items`.
    - `Plural_CannotBeReplacedByModelKey`: model `{ plural: "x" }` still calls the function.
  - `TemplateFitterTests`, rendering with a real `TemplateEngine`:
    - `Fit_ShortText_Unchanged`.
    - `Fit_LongOverview_PrefixWithEllipsis`: template `{{ title }}\n\n{{ overview }}`, a 2000-rune overview, limit 1024 → plain length ≤ 984; the output ends with `…`; no trailing space before `…`; `Truncated` false.
    - `Fit_OverviewUnder20Runes_Dropped`.
    - `Fit_List_KeepsMostAndSetsMore`: 200 lines plus a `{{ if items_more > 0 }}+{{ items_more }} more{{ end }}` line.
      - The kept count equals what a linear scan from the full list finds. Compute that in the test with the same render function.
    - `Fit_ListOver1000_CapsBeforeRendering`: 1500 items → no `LoopLimit` error; the output has `+N more`.
    - `Fit_StillTooLong_TruncatesAndFlags`: `Truncated` true; plain length = budget.
    - `Fit_RenderError_ReturnsError`.

- [ ] **Step 2: Run the tests and confirm they fail** (types missing).

- [ ] **Step 3: Implement** `PluralRules`, `TemplateModel`, `TemplateFitter`, and the engine changes. The prefix is built as `MessageFitter.Prefix` does it: runes, `TrimEnd()`, then `…`.

- [ ] **Step 4: Run the full suite and confirm it passes.**

- [ ] **Step 5: Commit.** `feat: plural helper and a fitter that shortens template output in v0.2's order`

---

### Task 3: Template model v2: new fields, toggles and shrinkable fields

**Files:**
- Create: `src/Jellyfin.Plugin.TgJelly/Templates/TemplateKeys.cs`
- Modify: `src/Jellyfin.Plugin.TgJelly/Templates/TemplateFieldCatalog.cs`
- Modify: `src/Jellyfin.Plugin.TgJelly/Templates/TemplateModelBuilder.cs`
- Test: `tests/.../Templates/TemplateModelBuilderTests.cs`, `TemplateFieldCatalogTests.cs`

**Interfaces:**
- Consumes: `TemplateModel.Of` (Task 2).
- Produces:
  - `public static class TemplateKeys { public const string Digest = "media.digest"; public static IReadOnlyList<string> All { get; } }`: `EventCatalog.AllKeys` followed by `Digest`.
  - `TemplateFieldCatalog.For("media.digest")` returns the digest fields. `TemplateFieldCatalog`'s completeness check covers `TemplateKeys.All`.
  - `public static (string? Text, string? List, string? More) TemplateFieldCatalog.Shrink(string eventKey)`, the shrinkable fields per key:

    | Key | Text | List | More |
    |---|---|---|---|
    | `media.movie`, `media.musicvideo`, `media.book` | `overview` | — | — |
    | `media.episodes` | — | `episodes` | `episodes_more` |
    | `server.plugin_updated` | `changelog` | — | — |
    | `server.plugin_install_failed`, `server.task_failed` | `error` | — | — |
    | `server.restart_pending` | — | `reasons` | `reasons_more` |
    | `media.digest` | — | `libraries` (custom, Task 6) | `more` |
    | every other key | — | — | — |

  - `public TemplateModel TemplateModelBuilder.Build(RenderModel model)`: the return type changes from a dictionary. Existing callers use `.Values` until Task 4 replaces them.
  - `public TemplateModel TemplateModelBuilder.Sample(string eventKey)`: `SampleFull(eventKey)` with the current toggles applied, as `TemplateModel.Of`. For `media.digest` it uses the generic rule on the sample.
  - New fields, all in the catalog with samples. Number texts use `Formatter.Number` (en-US `N0`); ratings use `Formatter.Rating`; runtimes use `Formatter.Runtime`.
    - **Every key:** `at_html` (`RawHtml`: `Formatter.Time(model.At, now)`).
    - **`media.movie`, `media.musicvideo`:** `rating_text`, `runtime_text`.
    - **`media.book`:** `rating`, `rating_text`, `genres`, and `parts_text`.
      - A Book has no runtime field.
      - An audiobook has `parts` > 0.
    - **`media.episodes`:**
      - `count_text` and `episodes_more` (0);
      - each episode gains `number_text`: two digits, invariant `"00"`, or null without a number.
    - **`media.album`:** `tracks_text`.
    - **`security.login_failed`:** `attempts_text`, `ip_count_text`, and `ip` (same as `last_ip`).
    - **`security.lockout`:** `attempts_text`.
    - **`server.restart_pending`:** `reasons_more` (0).
    - **`server.plugin_updated` / `server.plugin_install_failed` / `server.task_failed`:** `changelog`/`error` are trimmed, and null when whitespace-only. This matches v0.2's `quote.Trim()`.
    - **`media.digest`** (built in Task 6):
      - `count` and `count_text`: the number of entries;
      - `libraries[]`, each `{ name, entries[] }`:
        - `name` is null for "no library";
        - each entry is `{ kind, title, year, series, season_text, episode_text, count, count_text, artist, album }`;
        - `kind` is one of `movie`, `episode`, `season`, `album`, `audiobook`, `other`;
        - `season_text` and `episode_text` are two digits, or `"?"`;
      - `more` (0) and `more_text`.
  - **Toggles** (design §5.2):
    - `Message.ShowRatingGenres` off → `rating`, `rating_text` null and `genres` empty;
    - `Message.ShowOverview` off → `overview` null.

- [ ] **Step 1: Write the failing tests.**
  - `Build_V02FieldNamesStillPresent`: for every key, every field name of the v0.2 catalog is still present. Copy the v0.2 name lists into the test as literals.
  - `Build_ShowRatingGenresOff_EmptiesRatingAndGenres`.
  - `Build_ShowOverviewOff_NullOverview`.
  - `Build_AtHtml_IsRawTgTime`.
  - `Build_EpisodeNumberText_TwoDigitsOrNull`.
  - `Build_PluginUpdated_WhitespaceChangelog_IsNull`.
  - `Build_Book_HasRatingGenresNoRuntime`.
  - `Sample_AppliesToggles`.
  - `Catalog_CoversTemplateKeysAll`.
  - `Shrink_Table`: asserts the table above.

- [ ] **Step 2: Run the tests and confirm they fail.**

- [ ] **Step 3: Implement.** `NotificationRenderer.ApplyTemplate` keeps working, using `.Values`.

- [ ] **Step 4: Run the full suite and confirm it passes**, including the existing `TemplateDocsTests`. Regenerate `docs/templates.md` the way that test says, if it compares the file.

- [ ] **Step 5: Commit.** `feat: template model fields for built-in templates, toggles applied, shrinkable fields declared`

---

### Task 4: `BuiltInTemplates`, `TemplateRenderer` and the media built-ins

**Files:**
- Create: `src/Jellyfin.Plugin.TgJelly/Templates/BuiltInTemplates.cs`
- Create: `src/Jellyfin.Plugin.TgJelly/Templates/TemplateRenderer.cs`
- Create: `src/Jellyfin.Plugin.TgJelly/Templates/BuiltIn/en/media.movie.sbn`, `media.musicvideo.sbn`, `media.book.sbn`, `media.episodes.sbn`, `media.album.sbn`
- Modify: `src/Jellyfin.Plugin.TgJelly/Jellyfin.Plugin.TgJelly.csproj` (`<EmbeddedResource Include="Templates/BuiltIn/**/*.sbn" />`)
- Modify: `src/Jellyfin.Plugin.TgJelly/Rendering/NotificationRenderer.cs` (replace `ApplyTemplate`)
- Modify: `src/Jellyfin.Plugin.TgJelly/Delivery/OutboxDelivery.cs` (the ParseError flag no longer requires `Enabled`)
- Modify: `src/Jellyfin.Plugin.TgJelly/PluginServiceRegistrator.cs` (register the new services)
- Test: `tests/.../Templates/BuiltInTemplatesTests.cs`, `TemplateRendererTests.cs`, and `Rendering/Corpus/LayoutCorpusTests.cs` (add `Templates_MatchGolden`)

**Interfaces:**
- Consumes: `TemplateFitter`, `TemplateModel`, `TemplateModelBuilder.Build`, `TemplateKeys` (Tasks 2 and 3).
- Produces:
  - `public static class BuiltInTemplates`:
    - `public static string? Get(string eventKey, string language)` falls back to `en` and returns null when no file exists;
    - `public static IReadOnlyList<string> Keys(string language)`.
  - `public sealed class TemplateRenderer`. Constructor: `(TemplateEngine, TemplateModelBuilder, TemplateFlags, SettingsStore, StringTable, ILogger<TemplateRenderer>)`.
    - `public bool Covers(string eventKey)`: true when a built-in template exists for the key in `en`.
    - `public RenderedText? Render(RenderModel model)`: null when the key isn't covered.
    - Every render passes `SettingsStore.Current.Language` to `TemplateEngine.Render`, for `plural`. Built-in text comes from `BuiltInTemplates.Get(key, language)`.
  - `public sealed record RenderedText(string Caption, string Text, string? TemplateKey)`. `TemplateKey` is set only when an **edited** template produced the text.
  - **Render order:**
    1. **The edited override, when one exists:**
       - fit the caption (1024) and the text (4096) with `strict: false`;
       - on any error, a whitelist problem from `TelegramHtmlValidator`, or empty plain output in either part: flag `"error"` with v0.2's messages (`templates.empty` for empty output) and go to step 2;
       - if the part matching the message's poster state hit fitter step 3, flag `"truncated"`.
    2. **The built-in template:** the same fit, never flagged.
    3. **When the built-in fails** (a bug): log a warning with the key and the error. Return the last-resort text for both parts: `<b>` + escaped event label (`dest.event.<key>`, or `templates.digestLabel` for the digest) + `</b>`, then `\n` + the escaped first non-empty value of `title`, `series`, `album`, `user`, `plugin` or `task`, when one exists.
  - `NotificationRenderer.Render`:
    - posters and buttons still come from the layouts;
    - when `TemplateRenderer.Covers(key)`, the message's `Caption`/`Text`/`TemplateKey` come from `TemplateRenderer.Render`;
    - otherwise the v0.2 layout text and v0.2's `ApplyTemplate` path stay, so edited alert and digest templates keep working until Tasks 5–6. Task 6 deletes that path.
  - **Overrides:** `TemplateOverride.Enabled` is ignored from this task on. Any override with non-empty text is the template in use. Task 7 migrates stored overrides.

- [ ] **Step 1: Write the failing tests.**
  - `LayoutCorpusTests.Templates_MatchGolden(string name)`: `CorpusRig.RenderTemplates` (the real `NotificationRenderer` with `TemplateRenderer`, no overrides) equals both golden parts. The MemberData is filtered to keys `TemplateRenderer.Covers`, so Tasks 5–6 widen it automatically.
  - `BuiltInTemplatesTests`:
    - `MediaKeys_Exist`;
    - `UnknownLanguage_FallsBackToEn`;
    - `Validate_EachBuiltIn_NoErrors`: strict renders with `SampleFull` and `SampleSparse` give no error and no whitelist problem, for every key `Keys("en")` lists.
  - `TemplateRendererTests`:
    - `Edited_UsedAndKeyed`;
    - `EditedFails_FallsBackToBuiltInAndFlags`;
    - `EditedEmptyOutput_FallsBack`;
    - `EditedTruncated_FlagsTruncated`;
    - `BuiltInTruncated_NotFlagged`;
    - `BuiltInFails_LastResortText`: use an engine whose parser throws for the built-in's text.

- [ ] **Step 2: Run the tests and confirm they fail.**

- [ ] **Step 3: Write the five media templates and the renderer.**
  - Scriban whitespace control (`{{~` / `~}}`) is how blank lines between segments come out right.
  - A segment that is empty in v0.2 must leave no blank line.
  - The `+N more` line is `+{{ episodes_more }} more`; the number is under 1000 after capping.
  - Iterate until every media corpus case matches.

- [ ] **Step 4: Run the full suite and confirm it passes.**

- [ ] **Step 5: Commit.** `feat: media messages come from built-in templates`

---

### Task 5: Alert built-ins (security and server)

**Files:**
- Create: `src/Jellyfin.Plugin.TgJelly/Templates/BuiltIn/en/<key>.sbn`, one for each of the 14 `security.*` and `server.*` keys.
- Test: `Templates_MatchGolden` and `BuiltInTemplatesTests` widen on their own; add `AlertKeys_Exist`.

**Interfaces:**
- Consumes: Tasks 2–4.
- The quoted text goes in `<blockquote expandable>{{ changelog }}</blockquote>` (or `error`), after a blank line, only when the field isn't null.
- `server.restart_pending` lists `reasons` one per line, then `+{{ reasons_more }} more` when it's over 0.
- Times use `{{ at_html }}`.

- [ ] **Step 1: Add `AlertKeys_Exist` and run it.** Confirm it fails, along with the alert cases now included in `Templates_MatchGolden`.
- [ ] **Step 2: Write the 14 templates** until every alert corpus case matches.
- [ ] **Step 3: Run the full suite and confirm it passes.**
- [ ] **Step 4: Commit.** `feat: security and server messages come from built-in templates`

---

### Task 6: The digest built-in, and removing the layout text code

**Files:**
- Create: `src/Jellyfin.Plugin.TgJelly/Templates/BuiltIn/en/media.digest.sbn`
- Modify: `src/Jellyfin.Plugin.TgJelly/Templates/TemplateModelBuilder.cs` (the digest model, `TemplateModel.Custom`)
- Modify: `src/Jellyfin.Plugin.TgJelly/Rendering/NotificationRenderer.cs` (the digest through `TemplateRenderer`; the `Covers` fallback path removed)
- Modify: `src/Jellyfin.Plugin.TgJelly/Rendering/MediaLayouts.cs`, `AlertLayouts.cs` (delete the text code, keep posters and buttons)
- Delete: `src/Jellyfin.Plugin.TgJelly/Rendering/MessageFitter.cs` (move `CaptionLimit`, `TextLimit` and `DelayedReserve` to `TemplateFitter`), and `MessageParts.cs`
- Modify: `src/Jellyfin.Plugin.TgJelly/Strings/en.json` (remove the layout-only keys; see Global Constraints)
- Test: the digest corpus cases. Update the layout tests to assert posters and buttons only. Delete `MessageFitterTests.cs`, moving any case not covered by `TemplateFitterTests` there.

**Interfaces:**
- Consumes: Tasks 2–5.
- Produces:
  - `TemplateModelBuilder.Build` for `media.digest`. It builds today's flat entry list from `RenderModel.Groups`, in order, and returns a `TemplateModel.Custom`, where `with(values, _, kept)`:
    - regroups the first `kept` flat entries by library, in order of first appearance, as v0.2's `GroupBy` does;
    - sets `more` and `more_text` to the number dropped.
  - `ListCount` is the number of entries.
  - The digest's `count` stays the full number of entries.
  - Layouts after this task: `MediaLayouts.Render(RenderModel)` returns an `OutboxMessage` with `PosterPath`, `PosterCacheKey` and `Buttons` only, and the same null when no item exists. `AlertLayouts.Render` returns buttons only. Their names stay.

- [ ] **Step 1: Run the full suite and confirm it fails.** The digest corpus cases now join `Templates_MatchGolden` and fail.
- [ ] **Step 2: Write the digest model and template** until the digest cases match. "Other" is literal text in the template, used when `name` is null.
- [ ] **Step 3: Delete the text code and the unused strings.**
  - Delete `V02_MatchesGolden`, `Write_WhenRequested` and `CorpusRig.RenderV02`, since the v0.2 code no longer exists. The golden files stay, and `Templates_MatchGolden` now covers every case.
  - Check with `grep -rn "MessageFitter\|MessageParts" src tests`: no hits outside the history.
- [ ] **Step 4: Run the full suite and confirm it passes.**
- [ ] **Step 5: Commit.** `feat: the digest is a template; the C# layout text is gone`

---

### Task 7: Overrides, the templates API and the test send

**Files:**
- Modify: `src/Jellyfin.Plugin.TgJelly/Configuration/PluginConfiguration.cs` (the migration in its normalize step, around line 83–100)
- Modify: `src/Jellyfin.Plugin.TgJelly/Configuration/TemplateOverride.cs` (document `Enabled` as ignored, kept for compatibility)
- Modify: `src/Jellyfin.Plugin.TgJelly/Api/TemplatesController.cs`, `TemplateItemDto.cs`, `TemplateUpdateRequest.cs`, `TemplateValidationDto.cs`
- Create: `src/Jellyfin.Plugin.TgJelly/Api/TemplateTestRequest.cs`, `TemplateButtonDto.cs`
- Create: `src/Jellyfin.Plugin.TgJelly/Templates/TemplateSample.cs`
- Modify: `src/Jellyfin.Plugin.TgJelly/Destinations/DestinationTester.cs` (`SendSampleAsync`)
- Create: `src/Jellyfin.Plugin.TgJelly/Web/sample-poster.png`, embedded: copy `build/logo.png`.
- Test: `tests/.../Api/TemplatesControllerTests.cs`, `Destinations/DestinationTesterTests.cs`, `Configuration/PluginConfigurationTests.cs`, `Templates/TemplateSampleTests.cs`

**Interfaces:**
- Consumes: `TemplateRenderer`, `TemplateFitter`, `TemplateModelBuilder.Sample`, `BuiltInTemplates`, `TemplateKeys.All`.
- Produces:
  - **Migration:** on load, an override with `Enabled == false` or whitespace-only `Text` is removed. The others keep their text, and `Enabled` is set to true.
  - `public sealed class TemplateSample`, with `public SampleMessage Build(string eventKey, string text)`.
    - `public sealed record SampleMessage(string? Html, bool Caption, bool Poster, IReadOnlyList<IReadOnlyList<OutboxButton>> Buttons, int Length, int Limit, TemplateError? Error)`.
    - `Caption` and `Poster` are true for keys with a poster (media keys except `media.digest`) when `ShowPoster` is on.
    - `Html` is fitted against `Limit` (1024 for a caption, 4096 for text).
    - `Length` is the plain length of the unfitted render, for the length line.
    - Sample buttons, from the toggles and the public URL:
      - **Open in Jellyfin** (`button.open`) to `<public URL web root>/web/` when `ShowOpenInJellyfin` is on and the URL is valid (`LinkBuilder`'s rule);
      - **IMDb/TMDb** from the sample IDs for `media.movie` and `media.episodes`, when `ShowProviderLinks` is on;
      - **Release notes** for `server.update_available` with the sample URL.
  - **API:**
    - `GET Templates` → `TemplatesDto`, whose items are now `TemplateItemDto { EventKey, Label, Media, Text (in use), BuiltIn, Edited, Fields, SampleFull, SampleSparse, Flag }`. The items cover `TemplateKeys.All`; the digest's label is `templates.digestLabel` = "Digest". `Enabled` is gone.
    - `PUT Templates/{key}` `{ ExpectedRevision, Text }`. A text equal to the built-in after `\r\n`→`\n` removes the override. Saving clears the flag as today.
    - `DELETE Templates/{key}?expectedRevision=N` removes the override (Reset). It returns `TemplatesDto`, with 409 + `ConflictDto` on a stale revision and 404 for an unknown key.
    - `POST Templates/Validate` returns today's fields plus `Html`, `Caption`, `Poster`, `Buttons` (`TemplateButtonDto { Text, Url }` rows), all from `TemplateSample`. `Preview` stays as the raw unfitted output for compatibility.
    - `POST Templates/Test` `{ EventKey, Text, DestinationId }` → `TestResultDto`. It answers 404 for an unknown key or destination, and `Sent = false` with the first error message when the text has validation errors.
  - `DestinationTester.SendSampleAsync(Destination destination, SampleMessage sample, CancellationToken ct) → TestSendResult`. It does the same not-connected check, health check and error classification as `SendTestAsync`, then:
    - with `sample.Poster` and photos not blocked for the destination: `SendPhotoAsync` with `InputFile.FromStream(sample-poster stream, "poster.png")`, the fitted caption and the buttons;
    - otherwise: `SendTextAsync` with the text fitted to 4096, rebuilt with `Caption = false`.
    - Both use `ParseMode.Html`, the destination's thread, and `Silent` from its `SilentGroups` for the key's group (the digest counts as media).

- [ ] **Step 1: Write the failing tests.**
  - `PluginConfigurationTests`: `Migrate_DropsDisabledAndEmptyOverrides`.
  - `TemplatesControllerTests`:
    - `Get_ItemHasBuiltInAndTextInUse`;
    - `Get_IncludesDigest`;
    - `Put_TextEqualToBuiltIn_RemovesOverride`;
    - `Put_CrLfEqualToBuiltIn_RemovesOverride`;
    - `Delete_ResetsAndClearsFlag`;
    - `Delete_StaleRevision_Conflict`;
    - `Validate_ReturnsFittedHtmlButtonsAndPosterFlag`;
    - `Validate_ShowPosterOff_TextLimit`;
    - `Test_InvalidTemplate_NotSent`;
    - `Test_UnknownDestination_404`.
  - `DestinationTesterTests`:
    - `SendSample_Poster_SendsPhoto`;
    - `SendSample_PhotosBlocked_SendsText`;
    - `SendSample_NotConnected_Error`;
    - `SendSample_Lost_MarksHealth`.
  - `TemplateSampleTests`:
    - `Buttons_FollowToggles`;
    - `NoPublicUrl_NoOpenButton`.
- [ ] **Step 2: Run the tests and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the full suite and confirm it passes.**
- [ ] **Step 5: Commit.** `feat: templates API with built-in text, reset, fitted preview and test send`

---

### Task 8: The dashboard's Message templates section

**Files:**
- Modify: `src/Jellyfin.Plugin.TgJelly/Web/tgjelly.html`: move `<div id="tgj-templates">` out of `#tgj-advanced`, placing it right after `#tgj-messages`, with a `<h2 class="sectionTitle">` the renderer fills.
- Modify: `src/Jellyfin.Plugin.TgJelly/Web/tgjelly.js` (`renderTemplates`, the template state and actions; new exports)
- Modify: `src/Jellyfin.Plugin.TgJelly/Web/tgjelly.css`, or the page's style block, wherever the page keeps its CSS
- Modify: `src/Jellyfin.Plugin.TgJelly/Strings/en.json` (`web.templates.*`)
- Test: `tests/web/tgjelly.test.mjs`

**Interfaces:**
- Consumes: the Task 7 API.
- Produces:
  - `export function previewHtml(telegramHtml: string): string`. It tokenizes the Telegram HTML and re-emits only:
    - `b strong i em u ins s strike del code pre` as the same tags;
    - `tg-spoiler` and `span class="tg-spoiler"` as `<span class="tgj-spoiler">` (revealed on click by a delegated handler);
    - `blockquote` as `<blockquote>`; with `expandable`, as `<details class="tgj-quote"><summary>…</summary>…</details>`, where the summary is the first line;
    - `a` as `<a target="_blank" rel="noopener noreferrer" href="…">` only for `http:`, `https:` or `tg:` hrefs, and otherwise its text alone;
    - `tg-emoji` and `tg-time` as their inner text.
    Everything else is shown as escaped text. Entities in the input are decoded once and escaped again on output.
  - `export function renderTemplatePreview(result, S, lang): string`: the preview pane. It shows:
    - the error and warning lines;
    - a poster placeholder when `result.Poster`;
    - the bubble with `previewHtml(result.Html)`;
    - the button rows;
    - the length line `{length} / {limit}`.
    When `result` has errors, the last good preview is shown dimmed (`tgj-stale`).
  - **`renderTemplates(dto, S, ui)` layout:**
    - **Header:** the title, the intro and the guide link, then the event select (every item, the label marked `●` when Edited), then the badge: **Built-in** (`web.templates.builtIn`) or **Edited** (`web.templates.edited`).
    - **Body:** a `.tgj-tpl-grid` with a left column (the textarea, then the fields `<details>`) and a right column `<div id="tgj-tpl-preview">`.
    - **Footer:**
      - `Send test to` (`web.templates.testTo`): a select of the destinations by title, then a **Send test** button (`tplTest`), disabled when there are no destinations or the last validation had errors, with the result text;
      - **Reset to built-in** (`tplReset`), only when Edited. The first click shows "Reset? Your edits to this template are lost." with **Reset** (`tplResetConfirm`) and **Cancel** (`tplResetCancel`);
      - **Save** (`tplSave`).
    - **Removed:** the Enabled checkbox, the Sample button, the Validate button and the digest note.
    - **Under the test row:** a note, `web.templates.testNote`: "The test message goes to that chat for real, and its members see it."
  - **CSS:** `.tgj-tpl-grid { display:grid; grid-template-columns: 1fr 1fr; gap: 1em }`, with `@media (max-width: 700px) { .tgj-tpl-grid { grid-template-columns: 1fr } }`. The bubble styles use Jellyfin theme variables where they exist, and look right in its dark and light themes.
  - **Live preview:**
    - The `tplText` input handler marks the form dirty, but does **not** re-render the section.
    - It starts a 500 ms debounce, then calls Validate with a sequence number.
    - A response whose sequence number is older than the newest one sent is ignored.
    - Only `#tgj-tpl-preview` is replaced (`innerHTML = renderTemplatePreview(…)`).
    - Opening the section or switching events validates immediately.

- [ ] **Step 1: Write the failing tests** in `tests/web/tgjelly.test.mjs`.
  - `previewHtml_keepsWhitelist`: `<b>a</b><i>b</i>` round-trips.
  - `previewHtml_neutralizesUnsafeMarkup`:
    - `<a href="javascript:alert(1)">x</a>` → contains no `href`, and `x` is present;
    - `<img src=x onerror=alert(1)>` → appears escaped (`&lt;img`);
    - `<b onclick="x">y</b>` → `<b>y</b>`.
  - `previewHtml_expandableQuote_isDetails`.
  - `previewHtml_tgTime_showsFallbackText`.
  - `renderTemplates_builtInBadge_noResetButton`.
  - `renderTemplates_editedBadge_resetAsksInline`.
  - `renderTemplates_noEnabledCheckbox`.
  - `renderTemplates_gridHasPreviewPane`.
  - `renderTemplatePreview_posterPlaceholderAndButtons`.
  - `renderTemplatePreview_errorsDimLastGood`.
  - `preview_ignoresStaleResponse`: drive the page controller with a fake API that resolves two Validate calls in reverse order. The preview shows the newer result. Use whatever controller test harness the file already has.
  - `typing_doesNotRerenderTextarea`: after a `tplText` input event, the same textarea element is still in the DOM.
  - `sendTest_disabledWithErrors`.
- [ ] **Step 2: Run the tests and confirm they fail:** `node --test 'tests/web/*.test.mjs'`.
- [ ] **Step 3: Implement.** Also run `node --check src/Jellyfin.Plugin.TgJelly/Web/tgjelly.js`.
- [ ] **Step 4: Run node, then the full dotnet suite, and confirm both pass.** `WebPagesTests` checks the page; it must still contain `data-role="page"` and no `${`.
- [ ] **Step 5: Commit.** `feat: Message templates section with live Telegram preview and test send`

---

### Task 9: Docs and spec

**Files:**
- Modify: `tests/Jellyfin.Plugin.TgJelly.Tests/Templates/TemplateDocs.cs`, then regenerate `docs/templates.md` the way `TemplateDocsTests` does
- Modify: `README.md` (the custom templates paragraph)
- Modify: `docs/superpowers/specs/2026-10-16-tgjelly-v1-design.md`: §6.6 item 5, §8.1, §8.5, §8.6, §11, §14.4

**Interfaces:**
- **`TemplateDocs` intro:**
  - Every message is a template.
  - The dashboard's Message templates section shows the built-in template; "start there".
  - The `plural` helper, with an example.
  - The shrinkable fields and the `_more` counts, and the shortening order.
  - The fallback order: edited, then built-in.
  - `Validate` and `Sample` buttons are gone, replaced by live preview and Send test.
- **Generated docs:** the field tables now include every new field and `media.digest`. A new section, "Built-in templates", prints each key's `BuiltInTemplates.Get(key, "en")` in a fenced block, generated from the embedded files.
- **v1 spec:**
  - §8.1: the default layouts are now the built-in templates (point to the design doc).
  - §8.5:
    - drop "Digests always use the default layout";
    - "an enabled template" becomes "an edited template";
    - the fallback is to the built-in.
  - §8.6: shortening applies to template models (design §5.3).
  - §6.6 item 5: Message templates is its own section after Messages.
  - §11: the new endpoints (`DELETE Templates/{key}`, `POST Templates/Test`) and the changed DTOs.
  - §14.4: the design's §12 manual checks.

- [ ] **Step 1: Update `TemplateDocs` and add a test** `Docs_ListBuiltInTemplateOfEveryKey`. Run it and confirm it fails.
- [ ] **Step 2: Regenerate the docs.** Update the README and the spec.
- [ ] **Step 3: Run the full suite and confirm it passes.**
- [ ] **Step 4: Commit.** `docs: templates guide with built-in templates; spec amended for template-driven messages`

---

### Task 10: Verify and open the PR

- [ ] **Step 1: Run the full local verification:**
  - `dotnet build -c Release` (0 warnings) and `dotnet test` on both TFMs;
  - `node --test 'tests/web/*.test.mjs'`;
  - both Python suites (`build/` and `tests/integration/`).
- [ ] **Step 2: Run the constraint greps:**
  - `grep -rn "MessageFitter\|MessageParts\|\.Enabled" src/Jellyfin.Plugin.TgJelly/Templates src/Jellyfin.Plugin.TgJelly/Rendering src/Jellyfin.Plugin.TgJelly/Api/Templates*` → no stale uses;
  - `grep -n "confirm(\|alert(\|prompt(" src/Jellyfin.Plugin.TgJelly/Web/tgjelly.js` → none.
- [ ] **Step 3: Push `feat/templates` and open the PR into `main`.** The body ends with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`. Wait for CI. Every check must be green, including the 5 smoke runs.
- [ ] **Step 4: Hand over to the owner.** Ask them to:
  - review and merge the PR;
  - run the design's §12 manual checks;
  - approve a `v0.3` tag.

  Do not push a tag without that approval.
