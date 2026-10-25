# TgJelly v1: cleanup and one build — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship v0.10, a cleaned-up TgJelly that behaves the same, and then v1.0, one build that runs on Jellyfin 10.11 and 12.

**Architecture:**
- Part A (branch `feat/v1-cleanup`, PR, tag `v0.10`) is a series of area-by-area cleanups. Each one is behaviour-preserving and checked by the existing suites.
- Part B (branch `feat/v1-single-build`, from `main` after `v0.10`, PR, tag `v1.0`) drops the net10.0 track, CompatGuard and the two-zip release.

**Tech Stack:** C# net9.0/net10.0 (StyleCop, warnings as errors), xunit.v3, plain ES modules tested with `node --test`, Python build scripts with unittest, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-10-25-tgjelly-v1-cleanup-single-build-design.md`

## Global Constraints

- **Behaviour doesn't change** for the bot, dashboard, messages, API or files on disk, except for named race or bug fixes (spec §3.1).
- **Settings on disk:** v0.9's settings XML and the state files (`secrets.json`, `state.json`, `queue.json`, Log) load unchanged and mean the same. There is no rollback: a file written by the new version doesn't need to work in an older version (spec §3.2).
- **`SchemaVersion`:** the field stays and defaults to `2`. Nothing acts on it.
- **Kept features:**
  - `DestinationMigration` and `MigrateToChatId`;
  - `EnumText.Fallback` and `LenientEnumConverter`;
  - the settings backup.
- **Zip contents:** exactly `Jellyfin.Plugin.TgJelly.dll`, `Telegram.Bot.dll`, `Scriban.dll` and `meta.json`.
- **Version mapping (Part B):** tag `vX.Y` gives `X.Y.0.0`, tag `vX.Y.Z` gives `X.Y.Z.0`, and the zip is `tgjelly_<version>.zip`. `targetAbi` is `10.11.0.0`.
- **Owner workflow:**
  - tests are light: focused new tests only where a change needs one;
  - the build is done inline, with one final review per PR;
  - commits use `-c user.name="Daniil" -c user.email=igatorr19@gmail.com`;
  - every branch ends as a PR into `main`; a tag follows the owner's merge once main CI passes.
- **Verification commands** (each task ends green on all of them):
  - C#: `export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"; dotnet build Jellyfin.Plugin.TgJelly.slnx -c Release && dotnet test --solution Jellyfin.Plugin.TgJelly.slnx -c Release --no-build` gives `0 Warning(s)` and `failed: 0`.
  - Web: `node --import ./tests/web/jellyfin-resolve.mjs --test 'tests/web/*.test.mjs'` gives `# fail 0`.
  - Python: `python3 -m unittest discover -s build -p 'test_*.py' && python3 -m unittest discover -s tests/integration -p 'test_*.py'` gives `OK`.

## Review Focus

1. **The owner's v0.9 settings file:** a real v0.9 settings XML (destinations with topics and routes, templates, per-message switches, update-check kinds) must load with every value intact after the legacy code is gone. This is pinned by Task A1's fixture test.
2. **A caller that changes `SettingsStore.Current`:** once reads share one copy, an in-place change would leak to every later reader. The test-time snapshot check in Task A3 turns any such caller into a failing test.
3. **An outside save** (Jellyfin's generic configuration endpoint, or a backup restore) must replace the shared copy. Task A3's test pins this.
4. **Jellyfin 12's `Guid?` and 10.11's `string` alternate-version IDs:** empty, missing and set values must all give the right answer from one build. Task B1's test pins this.
5. **Old manifest entries:** after v1.0, the pre-release entries 1.0.1.0–1.0.9.0 and 2.0.x must be gone, or 10.11 would offer v0.9 as newest. Task B2's test pins this.

---

## Part A — Cleanup (v0.10), branch `feat/v1-cleanup`

The branch already holds the design commit (`1fab050`).

### Task 1 (A1): Remove pre-release settings support

**Files:**
- Modify:
  - `src/Jellyfin.Plugin.TgJelly/Configuration/PluginConfiguration.cs`;
  - `MessageOptions.cs`, `UpdateCheckOptions.cs`, `TemplateOverride.cs` and `SettingsStore.cs` (`Clone`), all in `Configuration/`;
  - any other file that calls the removed members.
- Delete tests:
  - `tests/Jellyfin.Plugin.TgJelly.Tests/Configuration/MessageOptionsMigrationTests.cs`;
  - `TopicMigrationTests.cs` and `UpdateCheckMigrationTests.cs` in the same folder;
  - the template-migration cases in other test files.
- Create:
  - `tests/Jellyfin.Plugin.TgJelly.Tests/Fixtures/Settings/v0.9.xml`;
  - the test `PluginConfigurationXmlTests.AV09File_LoadsWithEveryValue`.

**Interfaces:**
- Produces: `PluginConfiguration` without `MigrateTemplates`, `MigrateMessageOptions` or `MigrateTopics`. `SchemaVersion` defaults to 2. `SettingsStore.Clone` only copies.

- [ ] **Step 1: Write the fixture before touching code.** Use today's code (v0.9) to build a configuration with two destinations:
  - one forum with `ThreadId = 5`, `SeparateMediaTopics = true`, routes `media.movie=7` and `security=8`, known topics `5 "Jellyfin"` and `7 "Films"`, `Preset = Custom`, a trimmed event list, one library ID and silent group `security`;
  - one channel with `Preset = Everything`.

  Add one template override (`media.movie`, any text), one per-message switch entry with `ShowPoster = false`, a `PublicUrl`, update-check kinds, and non-default batching values. Serialize it with Jellyfin's `XmlSerializer` and save it as `Fixtures/Settings/v0.9.xml`. The existing XML tests show how (`PluginConfigurationXmlTests`).
- [ ] **Step 2: Write `AV09File_LoadsWithEveryValue`.** It deserializes the fixture and calls `Normalize()`. It asserts every value from Step 1, `SchemaVersion == 2`, and that the override is still in `Templates`. Run it: PASS on the current code.
- [ ] **Step 3: Remove the code.** That is:
  - the three `Migrate…` methods and their calls in `Normalize` and `SettingsStore.Clone`;
  - `UpdateCheckOptions.MigrateMode` and its legacy field and XML twin;
  - the five v0.4 `Show…` fields with their `…Xml` twins and `ShouldSerialize…` methods;
  - `TemplateOverride.Enabled`.

  Set `SchemaVersion` to default `2`. Delete the migration tests listed above, plus any test that only covers a removed member.
- [ ] **Step 4: Verify.** Run all three verification commands. `AV09File_LoadsWithEveryValue` still passes.
- [ ] **Step 5: Commit** `refactor: drop pre-release settings conversions and legacy fields`.

### Task 2 (A2): Presets become "follows new events"

**Files:**
- Modify:
  - `Configuration/Destination.cs` and `EventCatalog.cs`;
  - `Configuration/EnumText.cs` (drop the `PresetKind` special case);
  - `Pipeline/PresetUpgrader.cs`, renamed to `Pipeline/NewEventsUpgrader.cs`;
  - `Destinations/DestinationCreator.cs` and `DestinationMigration.cs`;
  - `Api/DestinationsController.cs` and `DetectedChatsController.cs`;
  - `Bot/Screens/DestinationScreens.cs` and `Bot/Flows/OnboardingFlow.cs`;
  - `Hosting/PersistenceService.cs` and `Status/DiagnosticsReport.cs`;
  - `tests/…/Fakes/BotTestHarness.cs` and its callers.
- Delete: `Configuration/PresetKind.cs`.
- Test: `tests/…/Pipeline/NewEventsUpgraderTests.cs` (replaces `PresetUpgraderTests.cs`).

**Interfaces:**
- Produces:
  - `Destination.FollowsNewEvents` (`bool`, default `true`, `[XmlIgnore]`, `[JsonIgnore]`);
  - `Destination.PresetText` (`string`, `[XmlElement("Preset")]`, `[JsonPropertyName("Preset")]`): `"Everything"` when the flag is on and `"Custom"` when it's off. Reading sets the flag to true only for `"Everything"`, and anything else gives false;
  - `EventCatalog.HasAll(IReadOnlyCollection<string> events) -> bool`, with `Expand` and `PresetFor` removed (callers that expanded Everything use `EventCatalog.AllKeys`);
  - `NewEventsUpgrader.Apply(PluginConfiguration) -> bool`;
  - `DestinationCreator.CreateAsync(...)` without the preset parameter, which sets `FollowsNewEvents = EventCatalog.HasAll(events)`;
  - `BotTestHarness.AddDestination(long chatId, ChatKind kind = ChatKind.Supergroup, int threadId = 0, bool followsNewEvents = true, string? id = null)`.

- [ ] **Step 1: Write `NewEventsUpgraderTests`.**
  - `AFollowingDestination_GetsEventsItLacks`: the flag is on and the events are `AllKeys` minus `server.restart_pending`. `Apply` returns true and the events equal `AllKeys`.
  - `ANonFollowingDestination_IsLeftAlone`: the flag is off with the same events. `Apply` returns false.
  - `PresetText_RoundTripsThroughXmlAndJson`: `Everything` and `Custom` survive both serializers, and `"NewMedia"` and `"bogus"` read as false.

  Run: FAIL, since nothing is defined yet.
- [ ] **Step 2: Implement.** Everywhere code used to set `d.Preset = EventCatalog.PresetFor(d.Events)` after an edit, it now sets `d.FollowsNewEvents = EventCatalog.HasAll(d.Events)`. That's in `DestinationsController`, `DestinationScreens` and `DestinationMigration`. The diagnostics line says `all events: yes/no` from the flag.
- [ ] **Step 3: Verify.** Run all three suites. A1's fixture test still passes, with its expectations changed from `Preset` to the flag.
- [ ] **Step 4: Commit** `refactor: presets become one "follows new events" flag`.

### Task 3 (A3): Settings are read once

**Files:**
- Modify: `Configuration/SettingsStore.cs`.
- Test: `tests/…/Configuration/SettingsStoreTests.cs`, and `tests/…/Fakes/BotTestHarness.cs` (switch the check on).

**Interfaces:**
- Produces:
  - `SettingsStore.Current`, which returns the shared snapshot, read-only by contract (stated in its doc comment);
  - `internal static bool VerifySnapshots`: when true, every `Current` read first checks that the snapshot still serializes to the JSON taken when it was built, and throws `InvalidOperationException("A caller changed SettingsStore.Current in place.")` if not;
  - snapshot rebuilds happen after a saved `UpdateIf`, after `OnExternalConfigurationSaved` and after `RestoreBackupIfWiped`. `UpdateIf` still works on a private clone.

- [ ] **Step 1: Write the tests.**
  - `Current_ReturnsTheSameCopy_UntilASave`: two reads are the same reference. After an `UpdateIf` that saves, the read is a new reference carrying the new value.
  - `AnOutsideSave_ReplacesTheCopy`: raise the fake host's outside save with a changed `PublicUrl`. `Current.PublicUrl` shows it.
  - `ChangingCurrentInPlace_IsCaughtInTests`: with `VerifySnapshots = true`, do `Current.PublicUrl = "x"`. The next `Current` read throws.

  Run: FAIL.
- [ ] **Step 2: Implement** the snapshot in `SettingsStore`. Turn on `VerifySnapshots` in the test harness's static constructor, so the whole suite checks every read.
- [ ] **Step 3: Run the full C# suite.** Every caller that changed `Current` in place now fails: about 8 sites assign `var x = _settings.Current;`, so check each one. Fix each by doing the change inside `UpdateIf`, or on a copy where the result is never saved.
- [ ] **Step 4: Verify.** Run all three suites.
- [ ] **Step 5: Commit** `perf: settings are copied once per change, not on every read`.

### Task 4 (A4): Dead code and analyzers

**Files:** whatever the analyzers flag in `src/`, plus `Strings/en.json`, plus `src/Jellyfin.Plugin.TgJelly/Jellyfin.Plugin.TgJelly.csproj` (or `.editorconfig`).

- [ ] **Step 1: Turn on the unused-code analyzers.** Turn on IDE0005, IDE0051, IDE0052, IDE0059 and IDE0060 as warnings, with `EnforceCodeStyleInBuild=true`. The build now lists the survey's findings (spec §3.4).
- [ ] **Step 2: Remove every flagged item.**
  - Drop the unused `ct` parameters only from non-public methods; public API and interface implementations keep theirs.
  - Remove the strings `token.webhookDeleted` and `settings.messages.pickMessage`.
  - Remove code left over from removed features (the bot profile setup, the first topic model) that the analyzers can't see. A reference search showing no callers outside tests is the evidence. A test of such code goes with it.
- [ ] **Step 3:** if the build is quiet, keep the analyzers on. Otherwise turn back off those that flag deliberate code, and note which ones in the commit.
- [ ] **Step 4: Verify.** Run all three suites.
- [ ] **Step 5: Commit** `refactor: remove dead code and unused strings`.

### Task 5 (A5): Threading

**Files:** read these:
- `State/StateStore.cs`, `State/SecretsStore.cs` and `State/InstanceGate.cs`;
- `Log/EventLog.cs`;
- `Intake/IntakeQueues.cs`;
- `Bot/BotConnection.cs` and `Bot/BotSupervisor.cs`;
- `Delivery/PacingGate.cs`;
- `Destinations/HealthService.cs`;
- `Configuration/SettingsStore.cs`;
- every other file the survey listed with `lock`, `Interlocked`, `Concurrent*` or timers.

The tests for these live under `tests/…`, mirroring the source folders.

- [ ] **Step 1:** for each file, write down its shared fields, which threads touch them, and which lock guards each one.
- [ ] **Step 2: For each race you can show, test it first.**
  - Write a test that reproduces the wrong result. Use a deterministic interleaving: the fakes' `TimeProvider`, gates, or `TaskCompletionSource` hooks. No sleeps.
  - Run it and watch it fail.
  - Fix it, and run it to watch it pass.
  - Commit each fix on its own as `fix: <race in plain words>`.
- [ ] **Step 3:** suspicions that can't be shown go in a "Suspected, not shown" list kept for the PR description. They get no code change.
- [ ] **Step 4: Verify.** Run all three suites.

### Task 6 (A6): Compact code and the dashboard split

**Files:**
- Modify: `src/Jellyfin.Plugin.TgJelly/Web/tgjelly.js` and the C# files with the topic-name repeat (below).
- Create: new `Web/tgjelly-*.js` modules.
- Also modify:
  - `Plugin.cs` (`GetPages`);
  - the csproj `EmbeddedResource` list;
  - the `tests/web/*.test.mjs` imports;
  - `tests/web/jellyfin-resolve.mjs`, if it lists modules.

**Interfaces:**
- Produces:
  - `Destination.RememberTopic(int thread, string name)`: it adds or renames a `KnownTopic`, keeping the old name when the new one is empty;
  - `tgjelly.js` stays the entry module, and every module it imports stays loadable as `./configurationpage?name=<file>`.

- [ ] **Step 1: Merge the topic-name repeat.** Use `Destination.RememberTopic` in `TopicNames`, `DestinationsController.AddTopic`, `DestinationScreens` (the "n" choice) and `DestinationCreator`. Add one test, `RememberTopic_AddsRenamesAndKeepsANameWhenGivenEmpty`.
- [ ] **Step 2: Split `tgjelly.js` along its existing sections.** A suggested split; the builder may adjust it where a seam is cleaner:
  - `tgjelly-api.js`: form flatten/merge/sync, `createApi`, the error texts (lines ~29–268);
  - `tgjelly-destinations.js`: the destination form, editor, topics, list and manual add (~513–896);
  - `tgjelly-advanced.js`: settings form, batching, updates, token and diagnostics (~897–1132);
  - `tgjelly-shell.js`: sections, nav, version, connection label, hash and `keepFocus` (~1133–1440).

  The controller stays in `tgjelly.js`. Register each module in `GetPages` and as an embedded resource, then point the tests' imports at the new modules.
- [ ] **Step 3: Add `Plugin_ServesEveryWebModule`.** It runs in the C# tests: every `Web/*.js` file is a page in `GetPages` and an embedded resource. Without that registration a split module would 404 in Jellyfin, even though node tests pass.
- [ ] **Step 4:** merge other clear repeats found while reading, and untangle methods over ~60 lines where that helps. Leave clear code alone.
- [ ] **Step 5: Verify.** Run all three suites.
- [ ] **Step 6: Commit,** by area: `refactor: one way to remember a topic name`, `refactor: split the dashboard script into modules`, and so on.

### Task 7 (A7): Comments

**Files:** every file in `src/`. In tests, only comments that cite the spec or plans.

- [ ] **Step 1: Remove history references and restating comments.**
  - History references: `spec §`, `design §`, `plan N`, `F<n>`, `v0.x`, "since 2026-…" and "(2026-10-…)".
  - Comments that restate the code.
  - Keep the why, Telegram and Jellyfin quirks, and thread-safety contracts.
  - Shorten XML doc comments to one line wherever one line says it.

  A check that should print nothing outside `docs/`: `grep -rn -E "spec §|design §|[Pp]lan [0-9]|\bF[0-9]+\b|v0\.[0-9]" src/Jellyfin.Plugin.TgJelly --include=*.cs --include=*.js`.
- [ ] **Step 2: Verify.** Run all three suites. The documentation file must still build: CS1591 is an error.
- [ ] **Step 3: Commit** by area, `docs: comments say why, not history`.

### Task 8 (A8): Tests sweep, docs, PR

- [ ] **Step 1: Sweep the tests.** Delete tests that duplicate another test's assertions for the same input, and tests that only check a constant or a plain getter. Keep a list of the deleted test names, with one reason each.
- [ ] **Step 2: Docs.**
  - Fix any README line the cleanup made untrue.
  - In the v1 spec, mark §3 presets and the 0.x migrations as removed by this design, with one line each.
- [ ] **Step 3: Verify.** Run all three suites. Record the line counts with `git diff --stat main...HEAD | tail -1`.
- [ ] **Step 4: Review.** Run one final review subagent (opus) over `main...HEAD`. It focuses on behaviour changes against spec §3.1, read-only settings, and the threading fixes. Fix its Important findings.
- [ ] **Step 5: Open the PR.** Push, then open a PR into `main` titled "Cleanup for v1". The body lists:
  - named fixes;
  - "Suspected, not shown" races;
  - deleted tests, with reasons;
  - lines removed.

  End it with the attribution lines. After the owner merges and main CI passes, tag `v0.10` with an annotated, user-facing changelog, then check the release, its assets and the manifest.

## Part B — One build (v1.0), branch `feat/v1-single-build` from `main` after `v0.10`

### Task 9 (B1): One net9.0 build, no CompatGuard

**Files:**
- Modify:
  - `src/Jellyfin.Plugin.TgJelly/Jellyfin.Plugin.TgJelly.csproj` (`TargetFrameworks` becomes `net9.0`; drop the `net10.0` group and `JF12`);
  - `Compat/JellyfinItems.cs`;
  - `PluginServiceRegistrator.cs`;
  - `Api/StatusDto.cs` and `Api/TgJellyController.cs`;
  - `Bot/Screens/StatusScreen.cs`;
  - `Status/DiagnosticsReport.cs`;
  - `Web/` (the incompatible banner, and the header's build target in `versionHtml`/`renderShell`);
  - `Strings/en.json`;
  - the test project's `TargetFrameworks`.
- Delete:
  - `Compat/BuildInfo.cs`, `Compat/CompatGuard.cs` and `Compat/CompatResult.cs`;
  - their tests;
  - the web tests for the banner.
- Test: `tests/…/Compat/JellyfinItemsTests.cs`.

**Interfaces:**
- Produces:
  - `JellyfinItems.IsAlternateVersion(Video video) -> bool`. It reads the `PrimaryVersionId` property through a `PropertyInfo` looked up once, in a static field. It gives true for a non-empty `string` or a non-`Guid.Empty` `Guid`, and false for null, empty or missing.
  - `JellyfinItems.IsAlternateVersionValue(object? value) -> bool` (internal), which holds the rule so it can be tested without a Jellyfin 12 `Video`.

- [ ] **Step 1: Write the test.** `IsAlternateVersionValue` on these cases:
  - `null` gives false;
  - `""` gives false;
  - `"abc"` gives true;
  - `Guid.Empty` gives false;
  - `(Guid?)null` gives false;
  - `Guid.NewGuid()` gives true.

  Add `IsAlternateVersion` on a 10.11 `Video` with and without `PrimaryVersionId`. Run: FAIL.
- [ ] **Step 2: Implement it.** Remove CompatGuard and everything that only exists for it: `StatusDto` fields, the bot status line, the dashboard banner, the diagnostics entry and its strings.
- [ ] **Step 3: Verify.** Run all three suites, which now build only net9.0.
- [ ] **Step 4: Commit** `feat: one build for Jellyfin 10.11 and 12`.

### Task 10 (B2): Packaging, version scheme, manifest

**Files:**
- Modify: `build/package.py`, `build/publish_pages.py` and `Web/tgjelly-shell.js` (or wherever `releaseName`/`releaseUrl` sit after A6).
- Tests: `build/test_package.py`, `build/test_publish_pages.py` and the web test for `releaseName`.

**Interfaces:**
- Produces:
  - `package.py`: `version_for(release: str) -> str`, where `"1.0"` gives `"1.0.0.0"` and `"1.2.3"` gives `"1.2.3.0"`. It replaces `track_version` and `TRACKS`. There's one publish, `-f net9.0`, giving one zip, `tgjelly_<version>.zip`, with `targetAbi` `10.11.0.0`.
  - `update_manifest` drops every version whose `sourceUrl` contains `/download/v0.`, then appends as before, newest first.
  - `releaseName("1.0.0.0")` gives `"v1.0"`, and `releaseName("1.2.3.0")` gives `"v1.2.3"`. A non-four-part version shows as `v<version>`, as today. `releaseUrl` links `releases/tag/<name>`.

- [ ] **Step 1: Write the tests.**
  - `version_for` on `1.0`, `1.2` and `1.2.3`.
  - `update_manifest` starting from a manifest holding `2.0.9.0`, `1.0.9.0` and `1.0.1.0`, with `v0.*` URLs, plus adding `1.0.0.0`. It must end with only `1.0.0.0`.
  - A later `1.1.0.0` keeps `1.0.0.0`.
  - The `releaseName` cases above.

  Run: FAIL.
- [ ] **Step 2: Implement.** Update `release.yml`'s asset globs if they name tracks.
- [ ] **Step 3: Verify.** Run all three suites.
- [ ] **Step 4: Commit** `build: one zip per release, version X.Y.Z.0, pre-release entries leave the manifest`.

### Task 11 (B3): CI and smoke

**Files:**
- Modify:
  - `.github/smoke-matrix.json`;
  - `tests/integration/smoke.py`, `tests/integration/test_smoke.py`;
  - `.github/workflows/ci.yml`, `.github/workflows/release.yml`.

- [ ] **Step 1: Matrix.** Four entries: `jellyfin/jellyfin:10.11.11` and `jellyfin/jellyfin:12.2`, each with `--stub` (marked `"release": true`) and with `--stub --base-url /jf`. All four use the one zip.
- [ ] **Step 2: `smoke.py`.**
  - Remove `--expect-incompatible` and its checks.
  - `--artifacts DIR` picks the single `tgjelly_*.zip`.
  - Update `test_smoke.py`.
- [ ] **Step 3: `ci.yml`.**
  - Install .NET as today: the SDK pinned by `global.json` is 10 and the tests run on the .NET 9 runtime, so both stay.
  - Drop anything that loops over two zips.
- [ ] **Step 4: Verify** with the Python suite. CI on the PR is the real check: the smoke tests run in Docker there.
- [ ] **Step 5: Commit** `ci: one zip, smoke-tested on 10.11 and 12`.

### Task 12 (B4): Docs, PR, v1.0

- [ ] **Step 1: Docs.**
  - v1 spec §13.2–13.5: one build, the version scheme and the matrix.
  - Mark F7 and F8 as replaced by this design.
  - README install: one build works on 10.11 and 12.
- [ ] **Step 2: Review.** Run one final review subagent (opus) over `main...HEAD`, then open the PR "One build for Jellyfin 10.11 and 12".
- [ ] **Step 3: After the owner merges and main CI passes,** tag `v1.0`, annotated. The notes cover what v1 is and the one build. They also give the upgrade step for the owner's 12 server: uninstall TgJelly, restart, then install 1.0.0.0; settings, the token, destinations and the Log stay.
- [ ] **Step 4: Check the release.**
  - Exactly one asset, `tgjelly_1.0.0.0.zip`.
  - The live manifest lists only `1.0.0.0`.
  - Update memory.
