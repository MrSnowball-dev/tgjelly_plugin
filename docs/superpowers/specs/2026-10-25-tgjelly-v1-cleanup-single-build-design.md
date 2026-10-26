# TgJelly v1: cleanup and one build for Jellyfin 10.11 and 12

Date: 2026-10-25. Owner: MrSnowball-dev. Follows the v1 spec (`2026-10-16-tgjelly-v1-design.md`) and plans 1–12 (v0.1–v0.9).

## 1. Why

TgJelly is ready for its first real release. Two things stand in the way.

1. **Duplicate versions in Jellyfin 12.** Every release ships two zips: 1.0.N.0 for 10.11 (net9.0) and 2.0.N.0 for 12 (net10.0). Jellyfin lists every version whose `targetAbi` is at or below the server's, with no upper bound, so a 12 server lists both. The owner doesn't want anyone to install the wrong one by picking whichever looks newest.
2. **Code that grew by layers.** Nine pre-releases added features and removed others (presets, the bot profile setup, the old message switches, the first topic model). Nothing was taken away systematically. The owner wants v1 to have no dead code, no overcommenting, and compact code that is easy to follow. Performance problems and race conditions found along the way get fixed.

## 2. Two parts, in this order

| Part | Ships as | What |
|---|---|---|
| A. Cleanup | v0.10 (two builds, as today) | Remove pre-release support and dead code, cut comments, compact code, read settings once, check threading, drop useless tests |
| B. One build | v1.0 | One net9.0 build for 10.11 and 12, a new version scheme, a clean manifest |

Order is my call. The owner tests only through releases ("a tag after every merge"). Cleanup first means its risk is tested on the owner's 12 server with today's build setup, and v1.0 is only the build change on top of code already tried. Each part is its own branch and PR into `main`. After the owner merges, the tag follows (`v0.10`, then `v1.0`).

## 3. Part A: cleanup (v0.10)

### 3.1 Behaviour stays the same

The bot, the dashboard, the messages, the API and the files on disk behave exactly as in v0.9. The only exceptions are fixes for races or bugs the cleanup finds, and each one is named in the PR description. This is the anti-goal: a cleanup that changes what the owner sees, apart from such named fixes, has failed even if it is smaller.

### 3.2 Pre-release support goes

The owner approved this: only the owner ever ran a 0.x release, and the dashboard always saves the whole settings file, so the owner's file is already in the current format.

- **Settings conversions removed:** `MigrateTemplates` (plan 7), `MigrateMessageOptions` and the v0.4 switch fields (`ShowPoster`, `ShowOverview`, `ShowRatingGenres`, `ShowOpenInJellyfin`, `ShowProviderLinks` with their `…Xml` twins and `ShouldSerialize…`), `UpdateCheck.MigrateMode` and its legacy mode field, `MigrateTopics` and the schema-2 step. The same goes for any other conversion of a 0.x settings or state format the builder finds, and for the tests of all of these.
- **What "current format" means:** a settings XML and the state files (`secrets.json`, `state.json`, `queue.json`, the Log) written by v0.9 load without change and mean the same. Fields that only exist so an older file can be read are removed. XmlSerializer ignores elements it doesn't know, so a left-over element in a file is harmless.
- **No rollback (owner, 2026-10-25: "no plans to downgrade ever").** A file written by v0.10 or v1 doesn't have to read correctly in an older version. So `TemplateOverride.Enabled` goes too, even though v0.9 would drop overrides without it.
- **Kept, because they are features, not pre-release support:**
  - Telegram's group→supergroup migration (`DestinationMigration`, `MigrateToChatId`);
  - lenient reading of unknown enum values from a newer version (`EnumText.Fallback`, `LenientEnumConverter`);
  - the settings backup.
- **`SchemaVersion`:** the field stays. Jellyfin settings may need a real migration after v1. Its value written by v0.9 is 2; v1 keeps writing 2 and doesn't act on it. Today the default in code is 1, and the 2 only comes from `MigrateTopics`, so the default becomes 2 when that migration goes. Otherwise a fresh install would write 1. (My call.)

### 3.3 Presets become one flag

Presets are no longer offered (since v0.6). The one behaviour left is the `PresetUpgrader`: a destination with every event on gets new events a later version adds. That behaviour stays. The preset machinery around it goes: `PresetKind` with New media and Admin alerts, `EventCatalog.Expand`/`PresetFor`, and preset parameters in creation code and test helpers.

- In code it becomes a plain yes/no on the destination, "follows new events". The name is the builder's choice.
- On disk it stays exactly as v0.9 writes it: the same XML element and values (`Everything` means yes, `Custom` means no). That way §3.2's "current format" holds without a conversion. Mapping the stored text to the flag is part of the field, not a migration. (My call.)

### 3.4 Dead code and strings

Survey on 2026-10-25, using the IDE0051/0052/0059/0060/0005 analyzers and reference searches:

- **Private members never read:** `OnboardingFlow._checker`, `OnboardingFlow.Wizard`, `DetectedChatsController._settings`, `NoticeBuilder._time`, `BotSecrets.PrintMembers`.
- **Unused assignments:** in `EventLog` (`dirty`) and in a Templates file (`last`). There are also three unused `ct` parameters and 13 unnecessary `using`s.
- **Unused strings:** `token.webhookDeleted` and `settings.messages.pickMessage`. Plural keys (`.one`/`.other`) and `….about` keys are looked up by convention and are in use.
- **Not dead:** every API endpoint is called by the dashboard. Every dashboard export is used, many only so tests can reach them.
- Anything left over from removed features goes too: the bot profile setup, `CompatGuard` remnants in Part B, and the first topic model. This list is a starting point, not the whole job.

The analyzers above may be turned on permanently in the project if they stay quiet after the cleanup. That's the builder's call.

### 3.5 Comments

Comments explain why, briefly. What the code does should be readable from the code itself.

- **Out:** references to the spec, design and plans (`spec §…`, `design §…`, `plan N`, `F12`), version history (`v0.4 …`, "since 2026-10-…"), and comments that restate the code.
- **Stays:** the reason for a non-obvious choice, Telegram or Jellyfin quirks, and thread-safety contracts.
- **XML doc comments:** the build requires them (`GenerateDocumentationFile` plus warnings as errors). They stay, but shrink to one line where one line says it.
- The specs and plans stay in `docs/` as the record. The README is checked for anything the cleanup made untrue.

### 3.6 Compact code

- Merge repeated code, for example the "learn/record a topic name" pattern that appears in `TopicNames`, `DestinationsController.AddTopic`, `DestinationScreens` and `DestinationCreator`, and other repeats the builder finds.
- Untangle long methods.
- Split `Web/tgjelly.js` (3,227 lines) into modules where a section stands on its own, for example Destinations, Overview/Status and Advanced. It follows the existing `tgjelly-*.js` pattern, and every module stays an embedded resource.
- Nothing is reshaped only to look different. A file that is long but clear stays.

### 3.7 Settings are read once

Today `SettingsStore.Current` serializes the whole configuration to JSON, parses it back, and runs every conversion, under the host lock, on every read. There are 65 call sites, including every send and every render.

- After the cleanup, the store keeps one copy of the current settings, rebuilt when settings change (a save through the store, or an outside save Jellyfin reports). `Current` returns that copy without copying.
- Callers must treat it as read-only. `UpdateIf` keeps working on its own private copy. How to enforce or document read-only use is left to the builder. At minimum, a test fails if a caller's change to `Current` leaks into the next read.
- Removing the conversions (§3.2) removes the rest of the per-read work.

### 3.8 Threading

There's locking in about 30 files. These get a careful read: `StateStore`, `EventLog`, `IntakeQueues`, `BotConnection`, `BotSupervisor`, `SecretsStore`, `InstanceGate`, `PacingGate`, `HealthService` and `SettingsStore`. The survey found no `async void`, no blocking waits, and one deliberate background task (`EventLog`'s writer).

- A race is fixed only when it can be shown: a sequence of steps that gives a wrong result. Each fix comes with a test that fails without it.
- Suspicions that can't be shown are listed in the PR, not "fixed".

### 3.9 Tests

The owner's words: tests that do nothing useful go. The survey found the suite in reasonable shape: 1,595 C# tests (about 12 s per framework) and 300 dashboard tests (about 2 s).

- Tests for removed code go with it: conversions, presets, legacy fields.
- Also going: tests that only repeat another test, and tests that only check a constant or a trivial getter.
- Tests aren't rewritten for style.
- Owner preference (2026-10-25): go easy on tests. New tests only where a change needs one, such as a race fix or the read-once settings.

### 3.10 Done when

- Both frameworks build with no warnings, all tests pass, and CI including the smoke tests passes.
- The PR lists the user-visible fixes (if any), the suspected but unshown races, and the total lines removed.
- One review pass before the PR (owner's preference: one final review).

## 4. Part B: one build (v1.0)

### 4.1 Evidence that one build works

On 2026-10-25 I checked the v0.9 net9.0 build against Jellyfin's 12.0.0 and 12.3.0 libraries on .NET 10. The check covered every referenced type, every method, property and field (443 types and 2,253 members, with signatures), and every abstract member of Jellyfin base types and interfaces TgJelly implements. It found exactly one break: `Video.PrimaryVersionId` changed from `string` to `Guid?`.

- The same check against 10.11 was clean, so it doesn't raise false alarms.
- The 12 build checked against 10.11 failed as expected, so it does catch real breaks.
- Everything else is the same code in both builds, and the 12 build already passes the 12.2 smoke tests.
- The checker was a throwaway; it isn't part of the repo.

### 4.2 The build

- **One target framework, `net9.0`,** compiled against `Jellyfin.Controller`/`Jellyfin.Model` **10.11.0** (`ExcludeAssets=runtime`), as today's track 1. `net10.0`, the `JF12` constant and the 12.0.0 package references go.
- **`PrimaryVersionId`:** `JellyfinItems.IsAlternateVersion` works on both, without a compile-time switch. It reads the property at runtime and treats a non-empty string, or a non-empty `Guid`, as "is an alternate version". The lookup is cached once, not reflected per item. There's a unit test for both shapes.
- **`CompatGuard` goes:** `BuildInfo`, `CompatResult`, its registration, the Status/StatusDto fields (`Compatible`, the banner text), the bot status screen and dashboard parts that show it, and the Diagnostics entry. Its strings and tests go too.
- The zip still contains exactly `Jellyfin.Plugin.TgJelly.dll`, `Telegram.Bot.dll`, `Scriban.dll` and `meta.json`. Its `targetAbi` is `10.11.0.0`.

### 4.3 Versions

- **Tag `vX.Y` gives version `X.Y.0.0`; tag `vX.Y.Z` gives `X.Y.Z.0`.** v1.0 is `1.0.0.0`, and the zip is `tgjelly_1.0.0.0.zip`.
- `build/package.py` (`track_version`, `TRACKS`) builds one zip.
- The dashboard's `releaseName` turns `1.0.0.0` into `v1.0` and `1.2.3.0` into `v1.2.3`. `releaseUrl` links to that tag.
- The Jellyfin-update checker (`Updates/`) is about Jellyfin server releases and isn't affected. The builder confirms this.

### 4.4 Manifest

- The v1.0 release removes every pre-release entry from `manifest.json` on `gh-pages`, so 10.11 and 12 each list only 1.0.0.0. The rule is permanent and simple: an entry whose `sourceUrl` points at a `v0.*` release is dropped. Later releases keep appending, as today.
- This matters beyond tidiness: the old 10.11 entries are 1.0.N.0, which are *higher* than 1.0.0.0, so leaving them would make v0.9 look newest on 10.11.
- The 0.x GitHub Releases stay as history. (My call. With no rollback planned, they are kept only as a record.) The v1.0 notes have no "going back" section.

### 4.5 CI and release

- **CI** builds and tests once (net9.0 only), and packages one zip.
- **Smoke matrix:** `jellyfin/jellyfin:10.11.11` and `jellyfin/jellyfin:12.2`, each with `--stub` and with `--stub --base-url /jf`, all using the same zip. The `--expect-incompatible` entry and its smoke-script code go. `--track` selection becomes "the one zip".
- **Release:** `release.yml` reuses `ci.yml` as today and publishes one asset plus the manifest. The CI .NET install (from builds.dotnet.microsoft.com, added in PR #16) installs only .NET 9 if nothing needs 10. Smoke tests run in Docker images and don't need it.

### 4.6 Upgrading the owner's server

Jellyfin runs the highest installed version, and the owner's 12 server has 2.0.9.0. So the v1.0 release notes say:

1. uninstall TgJelly in Jellyfin and restart;
2. then install 1.0.0.0 from the catalog.

Settings, the bot token, destinations and the Log stay, because they live under Jellyfin's plugin configurations folder, not in the plugin's folder.

### 4.7 Spec and docs

- The v1 spec's §13.2–13.5 are updated in place to describe one build, one version scheme and the new smoke matrix.
- F7 and F8 (two tracks, CompatGuard) are marked as replaced by this design.
- The README's install section says one build works on 10.11 and 12.

## 5. Not in scope

- New features or UI changes.
- Support for Jellyfin versions below 10.11.0.
- A permanent ABI check in CI (see §6).
- Renaming settings fields on disk.

## 6. Left to the builder

- How to split `tgjelly.js`, how to make read-only settings safe, and which repeated code to merge.
- Which analyzers to keep on after the cleanup.
- The order of work inside Part A. One branch for Part A. Commits by area are preferred, so a reviewer can follow along.
