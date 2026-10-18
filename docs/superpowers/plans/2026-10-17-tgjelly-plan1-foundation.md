# TgJelly Plan 1: Foundation — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A TgJelly plugin that builds for Jellyfin 10.11 (net9.0) and 12 (net10.0) from one codebase, loads as **Active** on real 10.11 and 12 servers, serves its dashboard page and a `GET /TgJelly/Status` endpoint, and contains the persistence foundation (settings, secrets, state, single-instance lock), packaged and smoke-tested in CI.

**Architecture:** One plugin assembly, `Jellyfin.Plugin.TgJelly`, multi-targeted `net9.0;net10.0`, with a `JF12` define on the 12 build. Settings stay in Jellyfin's XML config but change only through `SettingsStore` (revision check + backup). Secrets and runtime state live in TgJelly's own files, written atomically. Two background services — `InstanceGate` (owns `state.lock`) and `PersistenceService` (loads and flushes state) — are wrapped by a `Supervisor` so no exception can ever reach Jellyfin's host. A Python packaging script builds one zip per track; CI installs each zip into real Jellyfin Docker images.

**Tech Stack:** C# / .NET 9 + .NET 10, Jellyfin.Controller 10.11.0 / 12.0.0, xunit.v3 4.0.2 on Microsoft.Testing.Platform, NSubstitute 6.2.0, Microsoft.Extensions.TimeProvider.Testing 10.11.0, Python 3 (standard library only) for packaging and smoke tests, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-10-16-tgjelly-v1-design.md` (rev. 2, approved). This plan implements §5.1, §5.3–§5.4 (host glue, CompatGuard, rules), §10 (persistence), the Status part of §11, and §13 (build, packaging, CI). Section numbers below (§x, Fx) refer to the spec.

**This is plan 1 of 5.** Plans 2–5 are listed at the end and are written after this one is executed, against the real code.

**Deliberate deviations from the spec (small, noted here so reviewers don't flag them):**
- Spec §13.1 names the integration tests `tests/Jellyfin.Plugin.TgJelly.IntegrationTests/`. This plan uses `tests/integration/` with stdlib-only Python scripts, because they drive Docker and HTTP, not .NET code.
- Spec §13.4 "CI fails if `tgjelly.html` lacks `data-role="page"` or contains `${`" is implemented as unit tests (`WebPagesTests`), which run in CI.
- The BaseUrl variant of the smoke test (§14.3) moves to plan 5. The smoke script is HTTP-only and could run it now, but plan 1's CI is the first time any of this meets real Docker, so it starts with the plain matrix; plan 5 adds the variant together with the full page.
- The solution file is `Jellyfin.Plugin.TgJelly.slnx` (the SDK 10 format), not `.sln` as §13.1 writes it; background-service code lives in a `Hosting/` folder that §13.1 doesn't list.
- `status.loadWarnings` (quarantine notices for unreadable files) is added to the §10.3 `status` section, capped at 20 like `lastErrors`. Plan 5 adds dismissing it with the other notices.
- `InstanceGate` has a third Status text besides the two in §10.4: "TgJelly can't write to its data folder …" when the folder isn't writable at all, so a permission problem isn't reported as "waiting for the previous instance".
- Persisted enums (`Proxy.Mode`, `UpdateCheck.Mode`, `Destination.Kind`, `Destination.Preset`) are read leniently: a name written by a newer version reads as a safe fallback (`Custom` for presets) instead of failing the whole load (F9). The XML stays byte-identical (`<Mode>Socks5</Mode>`) through string "shadow" properties.
- §10.4's settings "lock" is the host's private lock, reached through `IConfigurationHost.RunExclusive`; outside saves take the same lock. (The template's multithreading analyzer forbids locking on a publicly reachable object.)
- The dashboard page's few English strings are hard-coded in `tgjelly.js` until plan 5 moves them to `GET TgJelly/Strings` (§11.1).

## Global Constraints

Every task's requirements implicitly include these (values copied from the spec):

- **Fixed forever (§5.1):** plugin name `TgJelly`, GUID `e547b901-0dc4-42ae-8999-3871dd141270`, assembly/DLL `Jellyfin.Plugin.TgJelly` / `Jellyfin.Plugin.TgJelly.dll`. Changing any of them loses every user's configuration.
- **Data folder (§5.1):** `plugins/configurations/TgJelly/` holding `secrets.json`, `state.json`, `queue.json`, `settings.bak.json`, `state.lock`. Never use `BasePlugin.DataFolderPath` (F10).
- **Two builds (§13.2–§13.3):** `net9.0` against `Jellyfin.Controller`/`Jellyfin.Model` **10.11.0** (`ExcludeAssets=runtime`), targetAbi `10.11.0.0`, version `1.X.Y.0`; `net10.0` against **12.0.0**, targetAbi `12.0.0.0`, version `2.X.Y.0`, `DefineConstants` `JF12`.
- **Analyzers (§13.2):** `TreatWarningsAsErrors`, `AnalysisMode=AllEnabledByDefault`, Nullable enabled, `StyleCop.Analyzers 1.2.0-beta.556`, `SerilogAnalyzer 0.15.0`, `SmartAnalyzers.MultithreadingAnalyzer 1.1.31`, and the official template's `.editorconfig`. The build must have **zero warnings**. StyleCop requires **one type per file**.
- **Host safety (§5.4 rule 2, F3, F4):** every background service returns from `StartAsync` at once, runs its work through `Supervisor.RunAsync` (catch-all, restart with backoff 1 s → 60 s), honours its stopping token, and **never throws from `StopAsync`**.
- **Static state (§5.4 rule 4):** none, except the two documented ones: `Plugin.Instance` (Jellyfin's pattern) and the `AsyncLocal<bool>` save flag in `Plugin`.
- **Settings (§10.1):** fields are only ever added, never renamed or retyped; arrays are initialised to `Array.Empty<T>()`; enums are written by name.
- **Dashboard page (§11.1, F25–F29):** the HTML contains the literal `data-role="page"` (double quotes), no `${`, no `<script>`; all JS lives in the `tgjelly.js` ES module; URLs come from `ApiClient.getUrl('TgJelly/…')`; Jellyfin's API JSON is **PascalCase**.
- **Zip contents (§13.4, F5):** exactly the whitelisted DLLs plus `meta.json` (category `Administration`, `imagePath` empty, `assemblies` = the whitelist); the manifest carries `imageUrl`.
- **Owner approval (§13.5):** ask the owner before installing anything, setting a git identity, or pushing.
- **`dotnet` is not on the default PATH** on the owner's machine (the SDKs are in `~/.dotnet`), and shell state does not carry over between commands. Every `Run:` line that needs `dotnet` therefore starts with `export PATH="$HOME/.dotnet:$PATH" &&`; keep that prefix in any command you add. A "command not found" is never the expected failure of a FAIL step.
- **Commits:** one commit per task, with the message given in the task. Append the attribution trailer lines your harness requires as extra paragraphs.

## Review Focus

The five inputs the spec implies but its happy paths don't exercise, most likely first. Each one has a test in the task that owns the code:

1. **The data folder is not writable** (a Docker volume with the wrong owner, a read-only config mount). Expected: Jellyfin keeps running, Status names the real problem, no change to either state file is lost, and everything is written once the folder is writable again. Tests: `InstanceGateTests.UnwritableFolder_ReportsAPermissionProblem_ThenAcquiresOnceFixed` (Task 5), `StateStoreTests.FailedWrite_KeepsTheChange_AndWritesItOnceTheFolderIsWritableAgain` and `StateStoreTests.OnlyTheStateWriteFails_TheQueueIsStillWritten_AndStateFollowsLater` (Task 6), `SettingsStoreTests.BackupWriteFailure_DoesNotTurnASaveIntoAnError` (Task 7), `PersistenceServiceTests.UnwritableDataFolder_DoesNotStopTheService_AndStateIsWrittenWhenItRecovers` (Task 8).
2. **The dashboard page is opened while a previous instance still holds `state.lock`** (in-process restart, F4), before state is loaded. Expected: `GET /TgJelly/Status` answers with "Waiting for the previous instance to stop", not a 500. Test: `TgJellyControllerTests.Status_Answers_BeforeOwnershipAndBeforeStateIsLoaded` (Task 8).
3. **Settings written by a newer TgJelly** (the user downgrades): unknown elements and unknown enum names. Expected: the settings load, unknown enum names read as safe fallbacks (never a preset that silently subscribes to everything), and the JSON backup still restores; a throw would make Jellyfin wipe the config (F9). Tests: `PluginConfigurationXmlTests.XmlFromANewerVersion_WithUnknownElements_StillLoads` and `…_WithUnknownEnumNames_ReadsSafeFallbacks` (Task 3), `SettingsStoreTests.BackupFromANewerVersion_WithUnknownEnumNames_IsStillRestored` (Task 7).
4. **Two saves at the same moment** — the bot and the dashboard, or a SettingsStore save racing a POST to Jellyfin's generic configuration endpoint. Expected: no lost update, no revision used twice, and `SettingsChanged` never goes back to an older revision. Tests: `SettingsStoreTests.ConcurrentUpdates_LoseNothing_AndRevisionsStayStrictlyIncreasing` and `…AStaleRevision_IsNeverDeliveredAfterANewerOne`, `PluginConfigurationHostTests.OutsideAndStoreSaves_NeverShareARevision` (Task 7).
5. **A Jellyfin data path with spaces or non-ASCII characters** (`C:\ProgramData\Jellyfin\Server`, a localized home folder). Expected: files are written and read back. Test: `SecretsStoreTests.Works_InAFolderWithSpacesAndNonAsciiCharacters` (Task 4).

---

## File Structure

```
.gitignore                         ignore build output, artifacts, KDE .directory
.editorconfig                      the official plugin template's analyzer settings (downloaded, checksum-verified)
Directory.Build.props              version properties (overridden by the packaging script)
global.json                        SDK 10 + Microsoft.Testing.Platform for `dotnet test`
Jellyfin.Plugin.TgJelly.slnx       solution
src/Jellyfin.Plugin.TgJelly/
  Jellyfin.Plugin.TgJelly.csproj   net9.0;net10.0, conditional Jellyfin packages, JF12 define
  Plugin.cs                        identity, IConfigurationHost (outside-save detection), dashboard pages
  PluginServiceRegistrator.cs      DI registrations; hosted-service order
  Compat/BuildInfo.cs              compile-time target line (10 / 12)
  Compat/CompatGuard.cs            build vs server major check + banner text
  Compat/CompatResult.cs
  Configuration/                   PluginConfiguration and its parts (one type per file), EnumText, SettingsValidator,
                                   SettingsStore, IConfigurationHost, DetachedConfigurationHost, event args, results
  State/                           AtomicFile, JsonDefaults, LenientEnumConverterFactory, DataPaths, BotSecrets,
                                   SecretsStore, InstanceLock, InstanceGate, StateStore
  State/Documents/                 StateDocument, StatusSection, QueueDocument, GlobalSuppressions
  Hosting/Supervisor.cs            never-throwing restart loop
  Hosting/PersistenceService.cs    load state, restore wiped settings, flush every second
  Api/TgJellyController.cs         GET /TgJelly/Status
  Api/StatusDto.cs
  Web/tgjelly.html                 dashboard page shell
  Web/tgjelly.js                   dashboard page controller (ES module)
tests/Jellyfin.Plugin.TgJelly.Tests/   xunit.v3 tests, run on net9.0 and net10.0
  Fakes/                           TestXmlSerializer, TestPlugin, FakeConfigurationHost
  TempDir.cs, Eventually.cs        test helpers
build/package.py                   zips + meta.json + manifest
build/test_package.py
tests/integration/smoke.py         Docker smoke test against real Jellyfin
tests/integration/test_smoke.py
.github/workflows/ci.yml           build, test, package, smoke matrix
```

## Prerequisites (before Task 1)

- [ ] **Check the SDKs.** Run `dotnet --list-sdks` (with `~/.dotnet` on `PATH`: `export PATH="$HOME/.dotnet:$PATH"`). Both a `9.0.x` and a `10.0.x` SDK must be listed. If not, **ask the owner**, then install user-locally:

```bash
curl -sSfL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh --channel 9.0 --install-dir "$HOME/.dotnet" --no-path
bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$HOME/.dotnet" --no-path
```

- [ ] **Check git.** `git config user.name` and `git config user.email` must print values. If they don't, **ask the owner** which identity to use (repo-local `git config user.name …` / `user.email …`). Do not invent one.
- [ ] **Create the work branch** from the branch that holds the spec and this plan:

```bash
cd ~/tgjelly_plugin
git checkout spec/v1-design
git checkout -b feat/plan1-foundation
```

All commands below run from the repository root.

---

### Task 1: Solution skeleton that builds for both Jellyfin lines

**Files:**
- Create: `.gitignore`, `.editorconfig`, `Directory.Build.props`, `global.json`, `Jellyfin.Plugin.TgJelly.slnx`
- Create: `src/Jellyfin.Plugin.TgJelly/Jellyfin.Plugin.TgJelly.csproj`
- Create: `src/Jellyfin.Plugin.TgJelly/Plugin.cs`, `src/Jellyfin.Plugin.TgJelly/Compat/BuildInfo.cs`, `src/Jellyfin.Plugin.TgJelly/Configuration/PluginConfiguration.cs`
- Create: `tests/Jellyfin.Plugin.TgJelly.Tests/Jellyfin.Plugin.TgJelly.Tests.csproj`
- Test: `tests/Jellyfin.Plugin.TgJelly.Tests/PluginIdentityTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Plugin.PluginName` (`const string`), `Plugin.PluginId` (`static readonly Guid`), `Plugin.Instance` (`static Plugin?`); `BuildInfo.TargetJellyfinMajor` (`const int`: 10 or 12), `BuildInfo.TargetJellyfinLabel` (`const string`: "10.11" or "12"); `PluginConfiguration` with `SchemaVersion` (int) and `Revision` (long).

- [ ] **Step 1: Create the repository scaffolding**

Create `.gitignore`:

```text
bin/
obj/
.vs/
.idea/
artifacts/
__pycache__/
*.pyc
.directory
```

Download the official plugin template's `.editorconfig` from a pinned commit and verify it:

Run: `curl -fsSL https://raw.githubusercontent.com/jellyfin/jellyfin-plugin-template/c93225a0a5a76d3843db05b4b5b77fcfc482fba3/.editorconfig -o .editorconfig && echo '752878465995b074d65c45cdea4489370c70bb88c28a8cf9e9103f88f4bee8c8  .editorconfig' | sha256sum -c -`

Expected: PASS — prints `.editorconfig: OK`.

Create `Directory.Build.props` (the packaging script overrides these per track):

```xml
<Project>
    <PropertyGroup>
        <Version>0.0.0.0</Version>
        <AssemblyVersion>0.0.0.0</AssemblyVersion>
        <FileVersion>0.0.0.0</FileVersion>
    </PropertyGroup>
</Project>
```

Create `global.json`. With the .NET 10 SDK, `dotnet test` only runs xunit.v3 (Microsoft.Testing.Platform) projects when this opt-in is present:

```json
{
  "sdk": {
    "version": "10.0.100",
    "rollForward": "latestFeature"
  },
  "test": {
    "runner": "Microsoft.Testing.Platform"
  }
}
```

Create `Jellyfin.Plugin.TgJelly.slnx`:

```xml
<Solution>
  <Folder Name="/src/">
    <Project Path="src/Jellyfin.Plugin.TgJelly/Jellyfin.Plugin.TgJelly.csproj" />
  </Folder>
  <Folder Name="/tests/">
    <Project Path="tests/Jellyfin.Plugin.TgJelly.Tests/Jellyfin.Plugin.TgJelly.Tests.csproj" />
  </Folder>
</Solution>
```

Create `src/Jellyfin.Plugin.TgJelly/Jellyfin.Plugin.TgJelly.csproj` (Task 8 adds the embedded web files):

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFrameworks>net9.0;net10.0</TargetFrameworks>
    <RootNamespace>Jellyfin.Plugin.TgJelly</RootNamespace>
    <AssemblyName>Jellyfin.Plugin.TgJelly</AssemblyName>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Nullable>enable</Nullable>
    <AnalysisMode>AllEnabledByDefault</AnalysisMode>
    <!-- XmlSerializer-friendly config uses arrays (spec §10.1), like jellyfin-plugin-webhook. -->
    <NoWarn>$(NoWarn);CA1819</NoWarn>
  </PropertyGroup>

  <!-- Jellyfin 12 build: .NET 10, compiled against the lowest 12.x packages (spec §13.2). -->
  <PropertyGroup Condition="'$(TargetFramework)' == 'net10.0'">
    <DefineConstants>$(DefineConstants);JF12</DefineConstants>
  </PropertyGroup>

  <ItemGroup Condition="'$(TargetFramework)' == 'net9.0'">
    <PackageReference Include="Jellyfin.Controller" Version="10.11.0" ExcludeAssets="runtime" />
    <PackageReference Include="Jellyfin.Model" Version="10.11.0" ExcludeAssets="runtime" />
  </ItemGroup>

  <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'">
    <PackageReference Include="Jellyfin.Controller" Version="12.0.0" ExcludeAssets="runtime" />
    <PackageReference Include="Jellyfin.Model" Version="12.0.0" ExcludeAssets="runtime" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="SerilogAnalyzer" Version="0.15.0" PrivateAssets="All" />
    <PackageReference Include="StyleCop.Analyzers" Version="1.2.0-beta.556" PrivateAssets="All" />
    <PackageReference Include="SmartAnalyzers.MultithreadingAnalyzer" Version="1.1.31" PrivateAssets="All" />
  </ItemGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="Jellyfin.Plugin.TgJelly.Tests" />
  </ItemGroup>

</Project>
```

Create `tests/Jellyfin.Plugin.TgJelly.Tests/Jellyfin.Plugin.TgJelly.Tests.csproj`. The test project references the Jellyfin packages *with* runtime assets, so the code under test can load:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFrameworks>net9.0;net10.0</TargetFrameworks>
    <OutputType>Exe</OutputType>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <RootNamespace>Jellyfin.Plugin.TgJelly.Tests</RootNamespace>
  </PropertyGroup>

  <!-- Tests need Jellyfin's runtime assemblies, so no ExcludeAssets here. -->
  <ItemGroup Condition="'$(TargetFramework)' == 'net9.0'">
    <PackageReference Include="Jellyfin.Controller" Version="10.11.0" />
    <PackageReference Include="Jellyfin.Model" Version="10.11.0" />
  </ItemGroup>

  <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'">
    <PackageReference Include="Jellyfin.Controller" Version="12.0.0" />
    <PackageReference Include="Jellyfin.Model" Version="12.0.0" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="xunit.v3" Version="4.0.2" />
    <PackageReference Include="NSubstitute" Version="6.2.0" />
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" Version="10.11.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Jellyfin.Plugin.TgJelly\Jellyfin.Plugin.TgJelly.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Write the failing test**

Create `tests/Jellyfin.Plugin.TgJelly.Tests/PluginIdentityTests.cs`:

```csharp
using System;
using Jellyfin.Plugin.TgJelly.Compat;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests;

public class PluginIdentityTests
{
    [Fact]
    public void Identity_IsFixedForever()
    {
        Assert.Equal("TgJelly", Plugin.PluginName);
        Assert.Equal(new Guid("e547b901-0dc4-42ae-8999-3871dd141270"), Plugin.PluginId);
        Assert.Equal("Jellyfin.Plugin.TgJelly", typeof(Plugin).Assembly.GetName().Name);
    }

    [Fact]
    public void BuildInfo_MatchesTargetFramework()
    {
        // The net9.0 build is the Jellyfin 10.11 track, the net10.0 build is the Jellyfin 12 track.
        var expectedMajor = Environment.Version.Major >= 10 ? 12 : 10;
        int actualMajor = BuildInfo.TargetJellyfinMajor;
        Assert.Equal(expectedMajor, actualMajor);
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet test --project tests/Jellyfin.Plugin.TgJelly.Tests -c Release --filter-class '*.PluginIdentityTests'`

Expected: FAIL — build error `CS0234: The type or namespace name 'Compat' does not exist in the namespace 'Jellyfin.Plugin.TgJelly'` (nothing in `src` yet).

- [ ] **Step 4: Write the implementation**

Create `src/Jellyfin.Plugin.TgJelly/Compat/BuildInfo.cs`:

```csharp
namespace Jellyfin.Plugin.TgJelly.Compat;

/// <summary>
/// Compile-time facts about which Jellyfin line this build targets (spec §5.4 rule 6, §13.3).
/// </summary>
public static class BuildInfo
{
#if JF12
    /// <summary>
    /// The Jellyfin major version this build is compiled for.
    /// </summary>
    public const int TargetJellyfinMajor = 12;

    /// <summary>
    /// Human-readable Jellyfin line this build is compiled for.
    /// </summary>
    public const string TargetJellyfinLabel = "12";
#else
    /// <summary>
    /// The Jellyfin major version this build is compiled for.
    /// </summary>
    public const int TargetJellyfinMajor = 10;

    /// <summary>
    /// Human-readable Jellyfin line this build is compiled for.
    /// </summary>
    public const string TargetJellyfinLabel = "10.11";
#endif
}
```

Create `src/Jellyfin.Plugin.TgJelly/Configuration/PluginConfiguration.cs` (Task 3 replaces it with the full model):

```csharp
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// TgJelly settings, stored by Jellyfin as XML (spec §10.1). Task 3 adds the remaining fields.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the settings schema version.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// Gets or sets the settings revision, increased on every save.
    /// </summary>
    public long Revision { get; set; }
}
```

Create `src/Jellyfin.Plugin.TgJelly/Plugin.cs` (Tasks 7 and 8 extend it):

```csharp
using System;
using Jellyfin.Plugin.TgJelly.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.TgJelly;

/// <summary>
/// The TgJelly plugin entry point.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>
{
    /// <summary>
    /// The plugin name. Fixed forever (spec §5.1).
    /// </summary>
    public const string PluginName = "TgJelly";

    /// <summary>
    /// The plugin GUID. Fixed forever (spec §5.1).
    /// </summary>
    public static readonly Guid PluginId = new("e547b901-0dc4-42ae-8999-3871dd141270");

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance. Set by Jellyfin when it creates the plugin.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => PluginName;

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public override string Description => "Telegram notifications for Jellyfin.";
}
```

- [ ] **Step 5: Build and run all tests**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet build Jellyfin.Plugin.TgJelly.slnx -c Release && dotnet test --solution Jellyfin.Plugin.TgJelly.slnx -c Release --no-build`

Expected: PASS — `Build succeeded.` with `0 Warning(s)`, then `total: 4`, `failed: 0` (every test runs once on net9.0 and once on net10.0).

- [ ] **Step 6: Commit**

```bash
git add .gitignore .editorconfig Directory.Build.props global.json Jellyfin.Plugin.TgJelly.slnx src tests
git commit -m "build: dual-target plugin skeleton for Jellyfin 10.11 and 12"
```

---

### Task 2: CompatGuard

Decides whether this build may run on the server it was loaded into. After a 10.11 → 12 upgrade Jellyfin keeps loading the old build until the plugin update task replaces it (F8); later plans keep intake, the bot and the outbox off while `IsCompatible` is false.

**Files:**
- Create: `src/Jellyfin.Plugin.TgJelly/Compat/CompatGuard.cs`, `src/Jellyfin.Plugin.TgJelly/Compat/CompatResult.cs`
- Test: `tests/Jellyfin.Plugin.TgJelly.Tests/Compat/CompatGuardTests.cs`

**Interfaces:**
- Consumes: `BuildInfo.TargetJellyfinMajor`, `BuildInfo.TargetJellyfinLabel` (Task 1).
- Produces: `CompatGuard.Evaluate(Version serverVersion) → CompatResult` and `CompatGuard.Evaluate(Version serverVersion, int buildMajor, string buildLabel) → CompatResult`; `record CompatResult(bool IsCompatible, string BuildLabel, Version ServerVersion, string? Message)`.

- [ ] **Step 1: Write the failing test**

Create `tests/Jellyfin.Plugin.TgJelly.Tests/Compat/CompatGuardTests.cs`:

```csharp
using System;
using Jellyfin.Plugin.TgJelly.Compat;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Compat;

public class CompatGuardTests
{
    [Theory]
    [InlineData("10.11.0.0")]
    [InlineData("10.11.11.0")]
    public void Build1011_OnServer1011_IsCompatible(string server)
    {
        var result = CompatGuard.Evaluate(Version.Parse(server), 10, "10.11");

        Assert.True(result.IsCompatible);
        Assert.Null(result.Message);
    }

    [Theory]
    [InlineData("12.0.0.0")]
    [InlineData("12.2.0.0")]
    public void Build12_OnServer12_IsCompatible(string server)
    {
        var result = CompatGuard.Evaluate(Version.Parse(server), 12, "12");

        Assert.True(result.IsCompatible);
    }

    [Fact]
    public void Build1011_AfterUpgradeTo12_IsIncompatible_WithSpecBanner()
    {
        var result = CompatGuard.Evaluate(new Version(12, 2, 0, 0), 10, "10.11");

        Assert.False(result.IsCompatible);
        Assert.Equal(
            "This TgJelly build is for Jellyfin 10.11. Update the plugin (Dashboard → Plugins) to get the Jellyfin 12 build.",
            result.Message);
    }

    [Fact]
    public void Build12_OnOlderServer_NamesTheOlderLine()
    {
        var result = CompatGuard.Evaluate(new Version(10, 11, 5, 0), 12, "12");

        Assert.False(result.IsCompatible);
        Assert.Contains("get the Jellyfin 10.11 build", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_UsesThisBuildsTarget()
    {
        var server = new Version(BuildInfo.TargetJellyfinMajor, 0, 0, 0);

        Assert.True(CompatGuard.Evaluate(server).IsCompatible);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet test --project tests/Jellyfin.Plugin.TgJelly.Tests -c Release --filter-class '*.CompatGuardTests'`

Expected: FAIL — build error `CS0103: The name 'CompatGuard' does not exist in the current context`.

- [ ] **Step 3: Write the implementation**

Create `src/Jellyfin.Plugin.TgJelly/Compat/CompatResult.cs`:

```csharp
using System;

namespace Jellyfin.Plugin.TgJelly.Compat;

/// <summary>
/// The outcome of <see cref="CompatGuard.Evaluate(Version)"/>.
/// </summary>
/// <param name="IsCompatible">Whether intake, the bot and the outbox may run.</param>
/// <param name="BuildLabel">The Jellyfin line this build targets, e.g. "10.11" or "12".</param>
/// <param name="ServerVersion">The running server version.</param>
/// <param name="Message">The dashboard banner text when incompatible; otherwise <c>null</c>.</param>
public sealed record CompatResult(bool IsCompatible, string BuildLabel, Version ServerVersion, string? Message);
```

Create `src/Jellyfin.Plugin.TgJelly/Compat/CompatGuard.cs`. The banner text is the spec's (§12), word for word:

```csharp
using System;
using System.Globalization;

namespace Jellyfin.Plugin.TgJelly.Compat;

/// <summary>
/// Decides whether this build may run on the server it was loaded into (spec §5.3, F8).
/// </summary>
public static class CompatGuard
{
    /// <summary>
    /// Evaluates this build against the running server version.
    /// </summary>
    /// <param name="serverVersion">The Jellyfin server version (<c>IServerApplicationHost.ApplicationVersion</c>).</param>
    /// <returns>The compatibility result.</returns>
    public static CompatResult Evaluate(Version serverVersion)
        => Evaluate(serverVersion, BuildInfo.TargetJellyfinMajor, BuildInfo.TargetJellyfinLabel);

    /// <summary>
    /// Evaluates a build target against a server version.
    /// </summary>
    /// <param name="serverVersion">The Jellyfin server version.</param>
    /// <param name="buildMajor">The Jellyfin major version the build targets.</param>
    /// <param name="buildLabel">The human-readable Jellyfin line the build targets.</param>
    /// <returns>The compatibility result.</returns>
    public static CompatResult Evaluate(Version serverVersion, int buildMajor, string buildLabel)
    {
        ArgumentNullException.ThrowIfNull(serverVersion);

        if (serverVersion.Major == buildMajor)
        {
            return new CompatResult(true, buildLabel, serverVersion, null);
        }

        var wanted = serverVersion.Major >= 12
            ? serverVersion.Major.ToString(CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{serverVersion.Major}.{serverVersion.Minor}");
        var message = string.Create(
            CultureInfo.InvariantCulture,
            $"This TgJelly build is for Jellyfin {buildLabel}. Update the plugin (Dashboard → Plugins) to get the Jellyfin {wanted} build.");
        return new CompatResult(false, buildLabel, serverVersion, message);
    }
}
```

- [ ] **Step 4: Build and run all tests**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet build Jellyfin.Plugin.TgJelly.slnx -c Release && dotnet test --solution Jellyfin.Plugin.TgJelly.slnx -c Release --no-build`

Expected: PASS — `0 Warning(s)`, `total: 18`, `failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add src/Jellyfin.Plugin.TgJelly/Compat tests/Jellyfin.Plugin.TgJelly.Tests/Compat
git commit -m "feat: CompatGuard checks build line against the server version"
```

---

### Task 3: Settings model and validator

The full §10.1 settings model, stored by Jellyfin as XML, plus the range and uniqueness rules that `SettingsStore` (Task 7) and later the API enforce.

**Files:**
- Replace: `src/Jellyfin.Plugin.TgJelly/Configuration/PluginConfiguration.cs`
- Create (one type per file, in `src/Jellyfin.Plugin.TgJelly/Configuration/`): `ProxyMode.cs`, `UpdateCheckMode.cs`, `ChatKind.cs`, `PresetKind.cs`, `EnumText.cs`, `ProxySettings.cs`, `MessageOptions.cs`, `BatchingOptions.cs`, `UpdateCheckOptions.cs`, `Destination.cs`, `TemplateOverride.cs`, `SettingsError.cs`, `SettingsValidator.cs`
- Test: `tests/Jellyfin.Plugin.TgJelly.Tests/Configuration/PluginConfigurationXmlTests.cs`, `tests/Jellyfin.Plugin.TgJelly.Tests/Configuration/SettingsValidatorTests.cs`

**Interfaces:**
- Consumes: `PluginConfiguration` (Task 1).
- Produces: `PluginConfiguration` properties `SchemaVersion`, `Revision`, `Language`, `PublicUrl`, `Proxy` (`ProxySettings`), `Message` (`MessageOptions`), `Batching` (`BatchingOptions`), `UpdateCheck` (`UpdateCheckOptions`), `Destinations` (`Destination[]`), `Templates` (`TemplateOverride[]`); enums `ProxyMode`, `UpdateCheckMode`, `ChatKind`, `PresetKind` (each persisted enum property has a string `…Xml` shadow for XML); `EnumText.Parse<T>(string?) → T` and `EnumText.Fallback<T>() → T` (unknown names → `PresetKind.Custom` / the first member); `PluginConfiguration.Normalize()` (nulls → defaults); `SettingsValidator.Validate(PluginConfiguration) → IReadOnlyList<SettingsError>` (also reports `null` sections and arrays); `SettingsValidator.IsValidId(string?) → bool`; `record SettingsError(string Field, string Message)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Jellyfin.Plugin.TgJelly.Tests/Configuration/PluginConfigurationXmlTests.cs`. Jellyfin's `IXmlSerializer` is a thin wrapper over `XmlSerializer` (F9), so the tests use `XmlSerializer` directly:

```csharp
using System.IO;
using System.Text;
using System.Xml.Serialization;
using Jellyfin.Plugin.TgJelly.Configuration;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Configuration;

public class PluginConfigurationXmlTests
{
    // Jellyfin's IXmlSerializer is a thin wrapper over System.Xml.Serialization.XmlSerializer (F9).
    private static readonly XmlSerializer Serializer = new(typeof(PluginConfiguration));

    [Fact]
    public void Defaults_MatchSpec()
    {
        var c = new PluginConfiguration();

        Assert.Equal(1, c.SchemaVersion);
        Assert.Equal(0, c.Revision);
        Assert.Equal("en", c.Language);
        Assert.Equal(ProxyMode.System, c.Proxy.Mode);
        Assert.True(c.Message.ShowPoster);
        Assert.False(c.Message.AnnounceUpgrades);
        Assert.Equal(120, c.Batching.QuietWindowSeconds);
        Assert.Equal(30, c.Batching.MaxHoldMinutes);
        Assert.Equal(20, c.Batching.ReleaseQuietSeconds);
        Assert.Equal(10, c.Batching.DigestThreshold);
        Assert.Equal(6, c.Batching.LateEditHours);
        Assert.Equal(15, c.Batching.ReadyTimeoutMinutes);
        Assert.True(c.UpdateCheck.Enabled);
        Assert.Equal(UpdateCheckMode.SameMajor, c.UpdateCheck.Mode);
        Assert.Empty(c.Destinations);
        Assert.Empty(c.Templates);
    }

    [Fact]
    public void RoundTrip_KeepsEverything()
    {
        var original = new PluginConfiguration
        {
            Revision = 7,
            PublicUrl = "https://jf.example.com",
            Proxy = new ProxySettings { Mode = ProxyMode.Socks5, Url = "socks5://10.0.0.2:1080" },
            Destinations =
            [
                new Destination
                {
                    Id = "abcd1234",
                    ChatId = -1001234567890123,
                    ThreadId = 42,
                    Title = "Movie Club",
                    Kind = ChatKind.Supergroup,
                    Preset = PresetKind.Custom,
                    Events = ["media.movie", "server.started"],
                    LibraryIds = ["0123456789abcdef0123456789abcdef"],
                    SilentGroups = ["media"],
                },
            ],
            Templates = [new TemplateOverride { EventKey = "media.movie", Enabled = true, Text = "{{ title }}" }],
        };

        var copy = RoundTrip(original);

        Assert.Equal(7, copy.Revision);
        Assert.Equal(ProxyMode.Socks5, copy.Proxy.Mode);
        var d = Assert.Single(copy.Destinations);
        Assert.Equal(-1001234567890123, d.ChatId);
        Assert.Equal(42, d.ThreadId);
        Assert.Equal(ChatKind.Supergroup, d.Kind);
        Assert.Equal(PresetKind.Custom, d.Preset);
        Assert.Equal(["media.movie", "server.started"], d.Events);
        Assert.Equal(["media"], d.SilentGroups);
        Assert.Equal("{{ title }}", Assert.Single(copy.Templates).Text);
    }

    [Fact]
    public void Enums_AreWrittenByName()
    {
        var xml = Serialize(new PluginConfiguration { Proxy = new ProxySettings { Mode = ProxyMode.Socks5 } });

        Assert.Contains("<Mode>Socks5</Mode>", xml, System.StringComparison.Ordinal);
    }

    [Fact]
    public void OldXml_WithMissingFields_LoadsDefaults()
    {
        const string Xml = "<?xml version=\"1.0\"?><PluginConfiguration><Revision>5</Revision></PluginConfiguration>";

        using var reader = new StringReader(Xml);
        var c = (PluginConfiguration)Serializer.Deserialize(reader)!;

        Assert.Equal(5, c.Revision);
        Assert.Equal(120, c.Batching.QuietWindowSeconds);
        Assert.Empty(c.Destinations);
    }

    [Fact]
    public void XmlFromANewerVersion_WithUnknownElements_StillLoads()
    {
        // After a downgrade the XML may contain fields this version does not know. If loading threw,
        // Jellyfin would replace the whole config with defaults (F9).
        const string Xml = "<?xml version=\"1.0\"?><PluginConfiguration><Revision>9</Revision><FutureSetting>x</FutureSetting>"
            + "<Batching><QuietWindowSeconds>60</QuietWindowSeconds><FutureNested>1</FutureNested></Batching></PluginConfiguration>";

        using var reader = new StringReader(Xml);
        var c = (PluginConfiguration)Serializer.Deserialize(reader)!;

        Assert.Equal(9, c.Revision);
        Assert.Equal(60, c.Batching.QuietWindowSeconds);
    }

    [Fact]
    public void XmlFromANewerVersion_WithUnknownEnumNames_ReadsSafeFallbacks()
    {
        // An enum member added by a newer version must not make the whole XML fail to load (F9).
        const string Xml = "<?xml version=\"1.0\"?><PluginConfiguration><Revision>3</Revision>"
            + "<Proxy><Mode>Mtproto</Mode></Proxy><UpdateCheck><Mode>Nightly</Mode></UpdateCheck>"
            + "<Destinations><Destination><Id>abcd1234</Id><ChatId>42</ChatId><Kind>Bogus</Kind><Preset>Playback</Preset></Destination></Destinations>"
            + "</PluginConfiguration>";

        using var reader = new StringReader(Xml);
        var c = (PluginConfiguration)Serializer.Deserialize(reader)!;

        Assert.Equal(3, c.Revision);
        Assert.Equal(ProxyMode.System, c.Proxy.Mode);
        Assert.Equal(UpdateCheckMode.SameMajor, c.UpdateCheck.Mode);
        var d = Assert.Single(c.Destinations);
        Assert.Equal(ChatKind.Private, d.Kind);
        Assert.Equal(PresetKind.Custom, d.Preset); // never silently "Everything"
    }

    private static PluginConfiguration RoundTrip(PluginConfiguration c)
    {
        using var reader = new StringReader(Serialize(c));
        return (PluginConfiguration)Serializer.Deserialize(reader)!;
    }

    private static string Serialize(PluginConfiguration c)
    {
        var sb = new StringBuilder();
        using (var writer = new StringWriter(sb, System.Globalization.CultureInfo.InvariantCulture))
        {
            Serializer.Serialize(writer, c);
        }

        return sb.ToString();
    }
}
```

Create `tests/Jellyfin.Plugin.TgJelly.Tests/Configuration/SettingsValidatorTests.cs`:

```csharp
using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Configuration;

public class SettingsValidatorTests
{
    [Fact]
    public void Defaults_AreValid()
    {
        Assert.Empty(SettingsValidator.Validate(new PluginConfiguration()));
    }

    [Theory]
    [InlineData(nameof(BatchingOptions.QuietWindowSeconds), 4)]
    [InlineData(nameof(BatchingOptions.QuietWindowSeconds), 3601)]
    [InlineData(nameof(BatchingOptions.MaxHoldMinutes), 0)]
    [InlineData(nameof(BatchingOptions.MaxHoldMinutes), 241)]
    [InlineData(nameof(BatchingOptions.ReleaseQuietSeconds), 4)]
    [InlineData(nameof(BatchingOptions.ReleaseQuietSeconds), 601)]
    [InlineData(nameof(BatchingOptions.DigestThreshold), 1)]
    [InlineData(nameof(BatchingOptions.DigestThreshold), 101)]
    [InlineData(nameof(BatchingOptions.LateEditHours), -1)]
    [InlineData(nameof(BatchingOptions.LateEditHours), 25)]
    [InlineData(nameof(BatchingOptions.ReadyTimeoutMinutes), 0)]
    [InlineData(nameof(BatchingOptions.ReadyTimeoutMinutes), 121)]
    public void OutOfRange_IsReportedWithFieldPath(string field, int value)
    {
        var c = new PluginConfiguration();
        typeof(BatchingOptions).GetProperty(field)!.SetValue(c.Batching, value);

        var errors = SettingsValidator.Validate(c);

        Assert.Contains(errors, e => e.Field == "Batching." + field);
    }

    [Theory]
    [InlineData(nameof(BatchingOptions.QuietWindowSeconds), 5)]
    [InlineData(nameof(BatchingOptions.QuietWindowSeconds), 60)]
    [InlineData(nameof(BatchingOptions.LateEditHours), 0)]
    [InlineData(nameof(BatchingOptions.LateEditHours), 24)]
    [InlineData(nameof(BatchingOptions.DigestThreshold), 2)]
    public void Boundaries_AreAccepted(string field, int value)
    {
        var c = new PluginConfiguration();
        typeof(BatchingOptions).GetProperty(field)!.SetValue(c.Batching, value);

        Assert.Empty(SettingsValidator.Validate(c));
    }

    [Fact]
    public void MaxHold_ShorterThanQuietWindow_IsRejected()
    {
        var c = new PluginConfiguration();
        c.Batching.QuietWindowSeconds = 3600;
        c.Batching.MaxHoldMinutes = 30;

        Assert.Contains(SettingsValidator.Validate(c), e => e.Field == "Batching.MaxHoldMinutes");
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("ABCD1234")]
    [InlineData("abcd-234")]
    [InlineData("abcd12345")]
    public void BadDestinationId_IsRejected(string id)
    {
        var c = new PluginConfiguration { Destinations = [new Destination { Id = id, ChatId = 1 }] };

        Assert.Contains(SettingsValidator.Validate(c), e => e.Field == "Destinations[0].Id");
    }

    [Fact]
    public void DuplicateChatAndThread_IsRejected_ButSameChatOtherTopicIsFine()
    {
        var c = new PluginConfiguration
        {
            Destinations =
            [
                new Destination { Id = "aaaa0001", ChatId = -100, ThreadId = 0 },
                new Destination { Id = "aaaa0002", ChatId = -100, ThreadId = 7 },
                new Destination { Id = "aaaa0003", ChatId = -100, ThreadId = 0 },
            ],
        };

        var errors = SettingsValidator.Validate(c);

        Assert.Equal(["Destinations[2].ChatId"], errors.Select(e => e.Field));
    }

    [Fact]
    public void DuplicateId_IsRejected()
    {
        var c = new PluginConfiguration
        {
            Destinations =
            [
                new Destination { Id = "aaaa0001", ChatId = 1 },
                new Destination { Id = "aaaa0001", ChatId = 2 },
            ],
        };

        Assert.Equal(["Destinations[1].Id"], SettingsValidator.Validate(c).Select(e => e.Field));
    }

    [Fact]
    public void NullSectionsAndArrays_AreReported()
    {
        var c = new PluginConfiguration { Batching = null!, Destinations = [new Destination { Id = "abcd1234", Events = null! }] };

        var fields = SettingsValidator.Validate(c).Select(e => e.Field).ToList();

        Assert.Contains("Batching", fields);
        Assert.Contains("Destinations[0].Events", fields);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet test --project tests/Jellyfin.Plugin.TgJelly.Tests -c Release --filter-class '*.SettingsValidatorTests'`

Expected: FAIL — build errors `CS0103: The name 'BatchingOptions' does not exist in the current context` (and similar for the other new types).

- [ ] **Step 3: Write the implementation**

Replace the whole file `src/Jellyfin.Plugin.TgJelly/Configuration/PluginConfiguration.cs` with:

```csharp
using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// TgJelly settings, stored by Jellyfin as XML (spec §10.1).
/// Fields are only ever added, never renamed or retyped.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the settings schema version.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// Gets or sets the settings revision, increased on every save.
    /// </summary>
    public long Revision { get; set; }

    /// <summary>
    /// Gets or sets the UI and message language (hidden in v1).
    /// </summary>
    public string Language { get; set; } = "en";

    /// <summary>
    /// Gets or sets the public server URL used for "Open in Jellyfin" links.
    /// </summary>
    public string PublicUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the proxy settings. Credentials live in secrets.json.
    /// </summary>
    public ProxySettings Proxy { get; set; } = new();

    /// <summary>
    /// Gets or sets the message toggles.
    /// </summary>
    public MessageOptions Message { get; set; } = new();

    /// <summary>
    /// Gets or sets the batching values.
    /// </summary>
    public BatchingOptions Batching { get; set; } = new();

    /// <summary>
    /// Gets or sets the update-check settings.
    /// </summary>
    public UpdateCheckOptions UpdateCheck { get; set; } = new();

    /// <summary>
    /// Gets or sets the notification destinations.
    /// </summary>
    public Destination[] Destinations { get; set; } = Array.Empty<Destination>();

    /// <summary>
    /// Gets or sets the custom template overrides.
    /// </summary>
    public TemplateOverride[] Templates { get; set; } = Array.Empty<TemplateOverride>();

    /// <summary>
    /// Replaces <c>null</c> sections, arrays and strings with their defaults. Used for settings that arrive from
    /// outside <c>SettingsStore</c>, e.g. a POST to Jellyfin's generic configuration endpoint with
    /// <c>"Batching": null</c>.
    /// </summary>
    public void Normalize()
    {
        Language ??= "en";
        PublicUrl ??= string.Empty;
        Proxy ??= new ProxySettings();
        Proxy.Url ??= string.Empty;
        Message ??= new MessageOptions();
        Batching ??= new BatchingOptions();
        UpdateCheck ??= new UpdateCheckOptions();
        Destinations ??= Array.Empty<Destination>();
        Templates ??= Array.Empty<TemplateOverride>();
        for (var i = 0; i < Destinations.Length; i++)
        {
            var d = Destinations[i] ??= new Destination();
            d.Id ??= string.Empty;
            d.Title ??= string.Empty;
            d.Events ??= Array.Empty<string>();
            d.LibraryIds ??= Array.Empty<string>();
            d.SilentGroups ??= Array.Empty<string>();
        }

        for (var i = 0; i < Templates.Length; i++)
        {
            var t = Templates[i] ??= new TemplateOverride();
            t.EventKey ??= string.Empty;
            t.Text ??= string.Empty;
        }
    }
}
```

Create `src/Jellyfin.Plugin.TgJelly/Configuration/ProxyMode.cs`:

```csharp
namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// How the bot and the update checker reach the internet (spec §9.1).
/// </summary>
public enum ProxyMode
{
    /// <summary>.NET default: follows HTTP_PROXY, HTTPS_PROXY and ALL_PROXY.</summary>
    System,

    /// <summary>No proxy at all.</summary>
    None,

    /// <summary>An HTTP(S) proxy.</summary>
    Http,

    /// <summary>A SOCKS5 proxy.</summary>
    Socks5,
}
```

Create `src/Jellyfin.Plugin.TgJelly/Configuration/UpdateCheckMode.cs`:

```csharp
namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// Which newer Jellyfin releases the update checker reports (spec §7.6).
/// </summary>
public enum UpdateCheckMode
{
    /// <summary>Only newer releases with the same major version.</summary>
    SameMajor,

    /// <summary>Any newer release.</summary>
    AnyNewer,
}
```

Create `src/Jellyfin.Plugin.TgJelly/Configuration/ChatKind.cs`:

```csharp
namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// The Telegram chat type of a destination.
/// </summary>
public enum ChatKind
{
    /// <summary>A private chat with the bot.</summary>
    Private,

    /// <summary>A basic group.</summary>
    Group,

    /// <summary>A supergroup (possibly a forum).</summary>
    Supergroup,

    /// <summary>A channel.</summary>
    Channel,
}
```

Create `src/Jellyfin.Plugin.TgJelly/Configuration/PresetKind.cs`:

```csharp
namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// The event preset a destination was set up with (spec §7.2).
/// </summary>
public enum PresetKind
{
    /// <summary>All event keys.</summary>
    Everything,

    /// <summary>All media.* keys.</summary>
    NewMedia,

    /// <summary>security.* without login_succeeded, plus server.*.</summary>
    AdminAlerts,

    /// <summary>Individually chosen keys.</summary>
    Custom,
}
```

Create `src/Jellyfin.Plugin.TgJelly/Configuration/EnumText.cs`:

```csharp
using System;

namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// Lenient enum parsing for persisted settings and state. After a downgrade, files may contain enum names this
/// version does not know; they read as a safe fallback instead of failing the whole load (spec F9: a failed XML
/// load makes Jellyfin replace the config with defaults).
/// </summary>
public static class EnumText
{
    /// <summary>
    /// Parses an enum name, or returns the fallback for unknown or missing names.
    /// </summary>
    /// <typeparam name="T">The enum type.</typeparam>
    /// <param name="text">The persisted name.</param>
    /// <returns>The parsed value, or <see cref="Fallback{T}"/>.</returns>
    public static T Parse<T>(string? text)
        where T : struct, Enum
        => Enum.TryParse<T>(text, ignoreCase: false, out var value) && Enum.IsDefined(value) ? value : Fallback<T>();

    /// <summary>
    /// The value an unknown name reads as: <see cref="PresetKind.Custom"/> for presets (so a destination never
    /// silently gains every event key), otherwise the enum's default (first) member.
    /// </summary>
    /// <typeparam name="T">The enum type.</typeparam>
    /// <returns>The fallback value.</returns>
    public static T Fallback<T>()
        where T : struct, Enum
        => typeof(T) == typeof(PresetKind) ? (T)(object)PresetKind.Custom : default;
}
```

Create `src/Jellyfin.Plugin.TgJelly/Configuration/ProxySettings.cs`. The `[XmlIgnore]` enum plus a string `ModeXml` element named `Mode` keeps the XML unchanged while reading unknown names leniently:

```csharp
using System.ComponentModel;
using System.Text.Json.Serialization;
using System.Xml.Serialization;

namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// Proxy settings without credentials (spec §10.1).
/// </summary>
public class ProxySettings
{
    /// <summary>
    /// Gets or sets the proxy mode.
    /// </summary>
    [XmlIgnore]
    public ProxyMode Mode { get; set; } = ProxyMode.System;

    /// <summary>Gets or sets <see cref="Mode"/> as XML text; an unknown name (from a newer version) reads as <see cref="EnumText.Fallback{T}"/>.</summary>
    [XmlElement(nameof(Mode))]
    [JsonIgnore]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public string ModeXml
    {
        get => Mode.ToString();
        set => Mode = EnumText.Parse<ProxyMode>(value);
    }

    /// <summary>
    /// Gets or sets the proxy URL for <see cref="ProxyMode.Http"/> and <see cref="ProxyMode.Socks5"/>.
    /// </summary>
    public string Url { get; set; } = string.Empty;
}
```

Create `src/Jellyfin.Plugin.TgJelly/Configuration/MessageOptions.cs`:

```csharp
namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// Global message toggles (spec §8.4).
/// </summary>
public class MessageOptions
{
    /// <summary>Gets or sets a value indicating whether posters are sent.</summary>
    public bool ShowPoster { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether overviews are shown.</summary>
    public bool ShowOverview { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether rating and genres are shown.</summary>
    public bool ShowRatingGenres { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the "Open in Jellyfin" button is shown.</summary>
    public bool ShowOpenInJellyfin { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether IMDb/TMDb buttons are shown.</summary>
    public bool ShowProviderLinks { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether quality upgrades are announced.</summary>
    public bool AnnounceUpgrades { get; set; }
}
```

Create `src/Jellyfin.Plugin.TgJelly/Configuration/BatchingOptions.cs`:

```csharp
namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// New-media batching values (spec §7.4, ranges in §10.1).
/// </summary>
public class BatchingOptions
{
    /// <summary>Gets or sets the quiet window that closes a group, in seconds (5–3600).</summary>
    public int QuietWindowSeconds { get; set; } = 120;

    /// <summary>Gets or sets the maximum time a group or buffer is held, in minutes (1–240).</summary>
    public int MaxHoldMinutes { get; set; } = 30;

    /// <summary>Gets or sets the quiet time that releases a buffer, in seconds (5–600).</summary>
    public int ReleaseQuietSeconds { get; set; } = 20;

    /// <summary>Gets or sets how many groups turn a release into a digest (2–100).</summary>
    public int DigestThreshold { get; set; } = 10;

    /// <summary>Gets or sets how long late items edit the sent message, in hours (0–24; 0 = never).</summary>
    public int LateEditHours { get; set; } = 6;

    /// <summary>Gets or sets how long an item waits for metadata, in minutes (1–120).</summary>
    public int ReadyTimeoutMinutes { get; set; } = 15;
}
```

Create `src/Jellyfin.Plugin.TgJelly/Configuration/UpdateCheckOptions.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json.Serialization;
using System.Xml.Serialization;

namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// Update-check settings (spec §7.6).
/// </summary>
public class UpdateCheckOptions
{
    /// <summary>Gets or sets a value indicating whether the update checker may run.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets which releases are reported.</summary>
    [XmlIgnore]
    public UpdateCheckMode Mode { get; set; } = UpdateCheckMode.SameMajor;

    /// <summary>Gets or sets <see cref="Mode"/> as XML text; an unknown name (from a newer version) reads as <see cref="EnumText.Fallback{T}"/>.</summary>
    [XmlElement(nameof(Mode))]
    [JsonIgnore]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public string ModeXml
    {
        get => Mode.ToString();
        set => Mode = EnumText.Parse<UpdateCheckMode>(value);
    }
}
```

Create `src/Jellyfin.Plugin.TgJelly/Configuration/Destination.cs`:

```csharp
using System;
using System.ComponentModel;
using System.Text.Json.Serialization;
using System.Xml.Serialization;

namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// A chat (or forum topic) that receives notifications (spec §10.1).
/// </summary>
public class Destination
{
    /// <summary>Gets or sets the 8-character id ([a-z0-9]).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the Telegram chat id.</summary>
    public long ChatId { get; set; }

    /// <summary>Gets or sets the forum topic id; 0 means none or the General topic.</summary>
    public int ThreadId { get; set; }

    /// <summary>Gets or sets the chat title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the chat type.</summary>
    [XmlIgnore]
    public ChatKind Kind { get; set; }

    /// <summary>Gets or sets <see cref="Kind"/> as XML text; an unknown name (from a newer version) reads as <see cref="EnumText.Fallback{T}"/>.</summary>
    [XmlElement(nameof(Kind))]
    [JsonIgnore]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public string KindXml
    {
        get => Kind.ToString();
        set => Kind = EnumText.Parse<ChatKind>(value);
    }

    /// <summary>Gets or sets a value indicating whether the destination is enabled (the user's Pause/Resume).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the preset the event keys were expanded from.</summary>
    [XmlIgnore]
    public PresetKind Preset { get; set; } = PresetKind.Everything;

    /// <summary>Gets or sets <see cref="Preset"/> as XML text; an unknown name (from a newer version) reads as <see cref="EnumText.Fallback{T}"/>.</summary>
    [XmlElement(nameof(Preset))]
    [JsonIgnore]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public string PresetXml
    {
        get => Preset.ToString();
        set => Preset = EnumText.Parse<PresetKind>(value);
    }

    /// <summary>Gets or sets the concrete event keys.</summary>
    public string[] Events { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the library ids (Guid "N" format); empty means all.</summary>
    public string[] LibraryIds { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the groups sent without sound (media, security, server).</summary>
    public string[] SilentGroups { get; set; } = Array.Empty<string>();
}
```

Create `src/Jellyfin.Plugin.TgJelly/Configuration/TemplateOverride.cs`:

```csharp
namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// A custom template for one event key (spec §8.5).
/// </summary>
public class TemplateOverride
{
    /// <summary>Gets or sets the event key.</summary>
    public string EventKey { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the template is used.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the Scriban template text.</summary>
    public string Text { get; set; } = string.Empty;
}
```

Create `src/Jellyfin.Plugin.TgJelly/Configuration/SettingsError.cs`:

```csharp
namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// One validation problem in a <see cref="PluginConfiguration"/>.
/// </summary>
/// <param name="Field">The field path, e.g. "Batching.QuietWindowSeconds" or "Destinations[1].Id".</param>
/// <param name="Message">What is wrong.</param>
public sealed record SettingsError(string Field, string Message);
```

Create `src/Jellyfin.Plugin.TgJelly/Configuration/SettingsValidator.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// Checks the ranges and uniqueness rules of spec §10.1.
/// </summary>
public static class SettingsValidator
{
    /// <summary>
    /// Validates a configuration.
    /// </summary>
    /// <param name="configuration">The configuration to check.</param>
    /// <returns>All problems found; empty when valid.</returns>
    public static IReadOnlyList<SettingsError> Validate(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var errors = new List<SettingsError>();
        RequireNotNull(errors, nameof(PluginConfiguration.Proxy), configuration.Proxy);
        RequireNotNull(errors, nameof(PluginConfiguration.Message), configuration.Message);
        RequireNotNull(errors, nameof(PluginConfiguration.Batching), configuration.Batching);
        RequireNotNull(errors, nameof(PluginConfiguration.UpdateCheck), configuration.UpdateCheck);
        RequireNotNull(errors, nameof(PluginConfiguration.Destinations), configuration.Destinations);
        RequireNotNull(errors, nameof(PluginConfiguration.Templates), configuration.Templates);

        var b = configuration.Batching ?? new BatchingOptions();

        CheckRange(errors, "Batching.QuietWindowSeconds", b.QuietWindowSeconds, 5, 3600);
        CheckRange(errors, "Batching.MaxHoldMinutes", b.MaxHoldMinutes, 1, 240);
        CheckRange(errors, "Batching.ReleaseQuietSeconds", b.ReleaseQuietSeconds, 5, 600);
        CheckRange(errors, "Batching.DigestThreshold", b.DigestThreshold, 2, 100);
        CheckRange(errors, "Batching.LateEditHours", b.LateEditHours, 0, 24);
        CheckRange(errors, "Batching.ReadyTimeoutMinutes", b.ReadyTimeoutMinutes, 1, 120);
        if (b.MaxHoldMinutes * 60 < b.QuietWindowSeconds)
        {
            errors.Add(new SettingsError("Batching.MaxHoldMinutes", "Must be at least as long as the quiet window."));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var chats = new HashSet<(long ChatId, int ThreadId)>();
        var destinations = configuration.Destinations ?? Array.Empty<Destination>();
        for (var i = 0; i < destinations.Length; i++)
        {
            var d = destinations[i];
            var prefix = string.Create(CultureInfo.InvariantCulture, $"Destinations[{i}]");
            if (d is null)
            {
                errors.Add(new SettingsError(prefix, "Required."));
                continue;
            }

            RequireNotNull(errors, prefix + ".Events", d.Events);
            RequireNotNull(errors, prefix + ".LibraryIds", d.LibraryIds);
            RequireNotNull(errors, prefix + ".SilentGroups", d.SilentGroups);
            if (!IsValidId(d.Id))
            {
                errors.Add(new SettingsError(prefix + ".Id", "Must be 8 characters from a-z and 0-9."));
            }
            else if (!ids.Add(d.Id))
            {
                errors.Add(new SettingsError(prefix + ".Id", "Is used by another destination."));
            }

            if (!chats.Add((d.ChatId, d.ThreadId)))
            {
                errors.Add(new SettingsError(prefix + ".ChatId", "This chat and topic is already a destination."));
            }
        }

        return errors;
    }

    /// <summary>
    /// Checks a destination id: exactly 8 characters from [a-z0-9].
    /// </summary>
    /// <param name="id">The id to check.</param>
    /// <returns><c>true</c> when valid.</returns>
    public static bool IsValidId(string? id)
    {
        if (id is null || id.Length != 8)
        {
            return false;
        }

        foreach (var c in id)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9')))
            {
                return false;
            }
        }

        return true;
    }

    private static void RequireNotNull(List<SettingsError> errors, string field, object? value)
    {
        if (value is null)
        {
            errors.Add(new SettingsError(field, "Required."));
        }
    }

    private static void CheckRange(List<SettingsError> errors, string field, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            errors.Add(new SettingsError(field, string.Create(CultureInfo.InvariantCulture, $"Must be between {min} and {max}.")));
        }
    }
}
```

- [ ] **Step 4: Build and run all tests**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet build Jellyfin.Plugin.TgJelly.slnx -c Release && dotnet test --solution Jellyfin.Plugin.TgJelly.slnx -c Release --no-build`

Expected: PASS — `0 Warning(s)`, `total: 84`, `failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add src/Jellyfin.Plugin.TgJelly/Configuration tests/Jellyfin.Plugin.TgJelly.Tests/Configuration
git commit -m "feat: settings model and validator (spec 10.1)"
```

---

### Task 4: Atomic files, data paths and the secrets store

Crash-safe writes for every TgJelly file, the data-folder layout, and `secrets.json` (bot token, proxy credentials) with mode 600 on Unix (§10.2, D9).

**Files:**
- Create (in `src/Jellyfin.Plugin.TgJelly/State/`): `JsonDefaults.cs`, `LenientEnumConverterFactory.cs`, `AtomicFile.cs`, `DataPaths.cs`, `BotSecrets.cs`, `SecretsStore.cs`
- Create: `tests/Jellyfin.Plugin.TgJelly.Tests/TempDir.cs`
- Test: `tests/Jellyfin.Plugin.TgJelly.Tests/State/AtomicFileTests.cs`, `tests/Jellyfin.Plugin.TgJelly.Tests/State/SecretsStoreTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `JsonDefaults.Options` (`JsonSerializerOptions`: camelCase, indented, enums by name with lenient reading, `RespectNullableAnnotations` so `null` in a non-nullable member is a `JsonException`); `AtomicFile.WriteAllText(string path, string contents, bool ownerOnly = false)`, `AtomicFile.Quarantine(string path, DateTimeOffset now) → string`; `DataPaths(string pluginConfigurationsPath)` with `Root`, `Secrets`, `State`, `Queue`, `SettingsBackup`, `Lock`; `record BotSecrets { BotToken, ProxyUsername, ProxyPassword }` with `BotSecrets.Empty` (its `ToString()` shows only `HasBotToken = …` etc., never the values); `SecretsStore(DataPaths, TimeProvider, ILogger<SecretsStore>)` with `Load() → BotSecrets`, `Save(BotSecrets)`, `Update(Func<BotSecrets, BotSecrets>)`, `LoadWarning` (string?, set when an unreadable file was moved aside). Test helper `TempDir` (`Path`, disposable).

- [ ] **Step 1: Write the failing tests**

Create `tests/Jellyfin.Plugin.TgJelly.Tests/TempDir.cs`:

```csharp
using System;
using System.IO;

namespace Jellyfin.Plugin.TgJelly.Tests;

/// <summary>
/// A temporary folder that is deleted when the test ends, even if a test left part of it read-only.
/// </summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tgjelly-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                foreach (var dir in Directory.GetDirectories(Path, "*", SearchOption.AllDirectories))
                {
                    File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }

            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: cleanup problems must not hide the real test result.
        }
    }
}
```

Create `tests/Jellyfin.Plugin.TgJelly.Tests/State/AtomicFileTests.cs`:

```csharp
using System;
using System.IO;
using Jellyfin.Plugin.TgJelly.State;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.State;

public class AtomicFileTests
{
    [Fact]
    public void WriteAllText_CreatesFolderAndFile_AndLeavesNoTempFiles()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "sub", "file.json");

        AtomicFile.WriteAllText(path, "one");
        AtomicFile.WriteAllText(path, "two");

        Assert.Equal("two", File.ReadAllText(path));
        Assert.Equal(["file.json"], Array.ConvertAll(Directory.GetFiles(Path.GetDirectoryName(path)!), Path.GetFileName));
    }

    [Fact]
    public void WriteAllText_OwnerOnly_UsesMode600OnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "secrets.json");

        AtomicFile.WriteAllText(path, "{}", ownerOnly: true);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void Quarantine_MovesFileAsideWithTimestamp()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "state.json");
        File.WriteAllText(path, "{not json");

        var moved = AtomicFile.Quarantine(path, new DateTimeOffset(2026, 10, 16, 12, 30, 5, TimeSpan.Zero));

        Assert.False(File.Exists(path));
        Assert.Equal(Path.Combine(dir.Path, "state.bad-20261016123005.json"), moved);
        Assert.Equal("{not json", File.ReadAllText(moved));
    }
}
```

Create `tests/Jellyfin.Plugin.TgJelly.Tests/State/SecretsStoreTests.cs`. The last test is Review Focus item 5:

```csharp
using System;
using System.IO;
using Jellyfin.Plugin.TgJelly.State;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.State;

public class SecretsStoreTests
{
    [Fact]
    public void Load_WithoutFile_ReturnsEmpty()
    {
        using var dir = new TempDir();

        Assert.Equal(BotSecrets.Empty, NewStore(dir).Load());
    }

    [Fact]
    public void Save_ThenNewStore_ReadsSameValues_AsCamelCaseJson()
    {
        using var dir = new TempDir();
        NewStore(dir).Save(new BotSecrets { BotToken = "123456:ABC", ProxyUsername = "u", ProxyPassword = "p" });

        var loaded = NewStore(dir).Load();

        Assert.Equal("123456:ABC", loaded.BotToken);
        Assert.Equal("p", loaded.ProxyPassword);
        Assert.Contains("\"botToken\"", File.ReadAllText(new DataPaths(dir.Path).Secrets), StringComparison.Ordinal);
    }

    [Fact]
    public void Update_ChangesOneField_KeepsOthers()
    {
        using var dir = new TempDir();
        var store = NewStore(dir);
        store.Save(new BotSecrets { BotToken = "t", ProxyPassword = "keep" });

        store.Update(s => s with { BotToken = string.Empty });

        Assert.Equal(string.Empty, NewStore(dir).Load().BotToken);
        Assert.Equal("keep", NewStore(dir).Load().ProxyPassword);
    }

    [Fact]
    public void CorruptFile_IsQuarantined_AndTreatedAsEmpty()
    {
        using var dir = new TempDir();
        var paths = new DataPaths(dir.Path);
        Directory.CreateDirectory(paths.Root);
        File.WriteAllText(paths.Secrets, "{oops");

        var store = NewStore(dir);
        var loaded = store.Load();

        Assert.Equal(BotSecrets.Empty, loaded);
        Assert.StartsWith("secrets.json was unreadable", store.LoadWarning, StringComparison.Ordinal);
        Assert.False(File.Exists(paths.Secrets));
        Assert.Single(Directory.GetFiles(paths.Root, "secrets.bad-*.json"));
    }

    [Fact]
    public void Works_InAFolderWithSpacesAndNonAsciiCharacters()
    {
        // E.g. "C:\ProgramData\Jellyfin\Server" on Windows, or a localized home folder.
        using var dir = new TempDir();
        var root = Path.Combine(dir.Path, "Jellyfin Данные ü");
        var store = new SecretsStore(new DataPaths(root), new FakeTimeProvider(), NullLogger<SecretsStore>.Instance);

        store.Save(new BotSecrets { BotToken = "123:abc" });

        Assert.Equal("123:abc", new SecretsStore(new DataPaths(root), new FakeTimeProvider(), NullLogger<SecretsStore>.Instance).Load().BotToken);
    }

    private static SecretsStore NewStore(TempDir dir)
        => new(new DataPaths(dir.Path), new FakeTimeProvider(), NullLogger<SecretsStore>.Instance);

    [Fact]
    public void ToString_NeverShowsTheSecrets()
    {
        var text = new BotSecrets { BotToken = "123456:ABCDEFsecret", ProxyPassword = "hunter2" }.ToString();

        Assert.DoesNotContain("ABCDEFsecret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        Assert.Contains("HasBotToken = True", text, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet test --project tests/Jellyfin.Plugin.TgJelly.Tests -c Release --filter-class '*.SecretsStoreTests'`

Expected: FAIL — build errors `CS0234: The type or namespace name 'State' does not exist in the namespace 'Jellyfin.Plugin.TgJelly'`.

- [ ] **Step 3: Write the implementation**

Create `src/Jellyfin.Plugin.TgJelly/State/JsonDefaults.cs`:

```csharp
using System.Text.Json;

namespace Jellyfin.Plugin.TgJelly.State;

/// <summary>
/// Shared JSON options for every file TgJelly writes: camelCase, indented, enums by name (unknown names read
/// leniently), and <c>null</c> rejected for non-nullable members so a damaged file is quarantined instead of
/// crashing later code.
/// </summary>
public static class JsonDefaults
{
    /// <summary>
    /// Gets the options. Cached once, as CA1869 requires.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        RespectNullableAnnotations = true,
        Converters = { new LenientEnumConverterFactory() },
    };
}
```

Create `src/Jellyfin.Plugin.TgJelly/State/LenientEnumConverterFactory.cs`:

```csharp
using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.TgJelly.Configuration;

namespace Jellyfin.Plugin.TgJelly.State;

/// <summary>
/// Writes enums by name and reads unknown names as <see cref="EnumText.Fallback{T}"/>, so settings.bak.json and
/// the state files written by a newer version still load after a downgrade.
/// </summary>
public sealed class LenientEnumConverterFactory : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        => (JsonConverter)Activator.CreateInstance(typeof(LenientEnumConverter<>).MakeGenericType(typeToConvert))!;

    private sealed class LenientEnumConverter<T> : JsonConverter<T>
        where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.TokenType == JsonTokenType.String ? EnumText.Parse<T>(reader.GetString()) : EnumText.Fallback<T>();

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString());
    }
}
```

Create `src/Jellyfin.Plugin.TgJelly/State/AtomicFile.cs`:

```csharp
using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Jellyfin.Plugin.TgJelly.State;

/// <summary>
/// Crash-safe file writes: write a temp file in the same folder, flush it to disk, then rename over the target.
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>
    /// Atomically replaces <paramref name="path"/> with <paramref name="contents"/>.
    /// </summary>
    /// <param name="path">The target file.</param>
    /// <param name="contents">The new contents.</param>
    /// <param name="ownerOnly">When <c>true</c>, the file is created with mode 600 on Unix.</param>
    public static void WriteAllText(string path, string contents, bool ownerOnly = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(contents);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (ownerOnly && !OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            using (var stream = new FileStream(temp, options))
            {
                var bytes = Utf8NoBom.GetBytes(contents);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    /// <summary>
    /// Moves an unreadable file aside as <c>name.bad-yyyyMMddHHmmss.json</c> so a fresh one can be written.
    /// </summary>
    /// <param name="path">The unreadable file.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The path the file was moved to.</returns>
    public static string Quarantine(string path, DateTimeOffset now)
    {
        var withoutExtension = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));
        var target = withoutExtension + ".bad-" + now.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + ".json";
        File.Move(path, target, overwrite: true);
        return target;
    }
}
```

Create `src/Jellyfin.Plugin.TgJelly/State/DataPaths.cs`:

```csharp
using System;
using System.IO;

namespace Jellyfin.Plugin.TgJelly.State;

/// <summary>
/// Locations of TgJelly's own files: <c>plugins/configurations/TgJelly/</c> (spec §5.1).
/// </summary>
public sealed class DataPaths
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DataPaths"/> class.
    /// </summary>
    /// <param name="pluginConfigurationsPath">Jellyfin's <c>IApplicationPaths.PluginConfigurationsPath</c>.</param>
    public DataPaths(string pluginConfigurationsPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginConfigurationsPath);
        Root = Path.Combine(pluginConfigurationsPath, "TgJelly");
    }

    /// <summary>Gets the data folder.</summary>
    public string Root { get; }

    /// <summary>Gets the path of secrets.json.</summary>
    public string Secrets => Path.Combine(Root, "secrets.json");

    /// <summary>Gets the path of state.json.</summary>
    public string State => Path.Combine(Root, "state.json");

    /// <summary>Gets the path of queue.json.</summary>
    public string Queue => Path.Combine(Root, "queue.json");

    /// <summary>Gets the path of settings.bak.json.</summary>
    public string SettingsBackup => Path.Combine(Root, "settings.bak.json");

    /// <summary>Gets the path of state.lock.</summary>
    public string Lock => Path.Combine(Root, "state.lock");
}
```

Create `src/Jellyfin.Plugin.TgJelly/State/BotSecrets.cs`:

```csharp
using System.Text;

namespace Jellyfin.Plugin.TgJelly.State;

/// <summary>
/// The contents of secrets.json (spec §10.2). Never returned by any API, and never printed: <see cref="object.ToString"/>
/// shows only whether each value is set.
/// </summary>
public sealed record BotSecrets
{
    /// <summary>Gets an empty set of secrets.</summary>
    public static BotSecrets Empty { get; } = new();

    /// <summary>Gets the Telegram bot token.</summary>
    public string BotToken { get; init; } = string.Empty;

    /// <summary>Gets the proxy username.</summary>
    public string ProxyUsername { get; init; } = string.Empty;

    /// <summary>Gets the proxy password.</summary>
    public string ProxyPassword { get; init; } = string.Empty;

    // Replaces the compiler-generated member list, which would print the token (spec §12, F39).
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("HasBotToken = ").Append(BotToken.Length > 0)
            .Append(", HasProxyUsername = ").Append(ProxyUsername.Length > 0)
            .Append(", HasProxyPassword = ").Append(ProxyPassword.Length > 0);
        return true;
    }
}
```

Create `src/Jellyfin.Plugin.TgJelly/State/SecretsStore.cs`:

```csharp
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.State;

/// <summary>
/// Reads and writes secrets.json: atomic writes, mode 600 on Unix (spec §10.2).
/// </summary>
public sealed class SecretsStore
{
    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly ILogger<SecretsStore> _logger;
    private readonly Lock _gate = new();
    private BotSecrets? _cached;
    private string? _loadWarning;

    /// <summary>
    /// Initializes a new instance of the <see cref="SecretsStore"/> class.
    /// </summary>
    /// <param name="paths">TgJelly's data paths.</param>
    /// <param name="time">The clock, used to name quarantined files.</param>
    /// <param name="logger">The logger.</param>
    public SecretsStore(DataPaths paths, TimeProvider time, ILogger<SecretsStore> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _path = paths.Secrets;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Gets the warning shown in Status when secrets.json was unreadable and has been moved aside; otherwise <c>null</c>.
    /// </summary>
    public string? LoadWarning
    {
        get
        {
            lock (_gate)
            {
                return _loadWarning;
            }
        }
    }

    /// <summary>
    /// Returns the current secrets. A missing file means no secrets; an unreadable file is moved aside.
    /// </summary>
    /// <returns>The secrets.</returns>
    public BotSecrets Load()
    {
        lock (_gate)
        {
            return _cached ??= ReadFromDisk();
        }
    }

    /// <summary>
    /// Replaces the secrets.
    /// </summary>
    /// <param name="secrets">The new secrets.</param>
    public void Save(BotSecrets secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        lock (_gate)
        {
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(secrets, JsonDefaults.Options), ownerOnly: true);
            _cached = secrets;
        }
    }

    /// <summary>
    /// Changes the secrets in one step, e.g. <c>s =&gt; s with { BotToken = "" }</c>.
    /// </summary>
    /// <param name="change">Produces the new secrets from the current ones.</param>
    public void Update(Func<BotSecrets, BotSecrets> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            Save(change(Load()));
        }
    }

    private BotSecrets ReadFromDisk()
    {
        if (!File.Exists(_path))
        {
            return BotSecrets.Empty;
        }

        try
        {
            return JsonSerializer.Deserialize<BotSecrets>(File.ReadAllText(_path), JsonDefaults.Options) ?? BotSecrets.Empty;
        }
        catch (JsonException ex)
        {
            var moved = AtomicFile.Quarantine(_path, _time.GetUtcNow());
            _loadWarning = "secrets.json was unreadable and has been moved to " + Path.GetFileName(moved) + "; enter the bot token again.";
            _logger.LogError(ex, "secrets.json was unreadable and has been moved to {Path}", moved);
            return BotSecrets.Empty;
        }
    }
}
```

- [ ] **Step 4: Build and run all tests**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet build Jellyfin.Plugin.TgJelly.slnx -c Release && dotnet test --solution Jellyfin.Plugin.TgJelly.slnx -c Release --no-build`

Expected: PASS — `0 Warning(s)`, `total: 102`, `failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add src/Jellyfin.Plugin.TgJelly/State tests/Jellyfin.Plugin.TgJelly.Tests/TempDir.cs tests/Jellyfin.Plugin.TgJelly.Tests/State
git commit -m "feat: atomic file writes and the secrets store (spec 10.2)"
```

---

### Task 5: Supervisor and the single-instance gate

`Supervisor` is the never-throwing restart loop every background service uses (F3: an unhandled exception would stop Jellyfin). It yields first, so `StartAsync` returns at once on .NET 9 too (there, `BackgroundService` runs `ExecuteAsync` inline up to its first `await`; on .NET 10 it doesn't). `InstanceGate` owns `state.lock`; until it does, nothing that consumes intake or talks to Telegram may start (§10.4). On Linux, .NET's `FileShare.None` lock is exclusive even between two opens in the same process, which covers an in-process restart where the old plugin instance hasn't let go yet (F4) — the first test proves it.

**Files:**
- Create: `src/Jellyfin.Plugin.TgJelly/Hosting/Supervisor.cs`, `src/Jellyfin.Plugin.TgJelly/State/InstanceLock.cs`, `src/Jellyfin.Plugin.TgJelly/State/InstanceGate.cs`
- Create: `tests/Jellyfin.Plugin.TgJelly.Tests/Eventually.cs`
- Test: `tests/Jellyfin.Plugin.TgJelly.Tests/Hosting/SupervisorTests.cs`, `tests/Jellyfin.Plugin.TgJelly.Tests/State/InstanceGateTests.cs`

**Interfaces:**
- Consumes: `DataPaths` (Task 4).
- Produces: `Supervisor.RunAsync(string name, Func<CancellationToken, Task> body, TimeProvider time, ILogger logger, CancellationToken stoppingToken) → Task` (never throws); `InstanceLock.TryAcquire(string path, out Exception? denied) → InstanceLock?` and `InstanceLock.TryAcquire(string path) → InstanceLock?` (`IDisposable`; "busy" vs "denied" told apart by the I/O error); `InstanceGate : BackgroundService` with `IsOwner` (bool), `WaitingMessage` (string?), `WaitForOwnershipAsync(CancellationToken) → Task`, constants `WaitingForPreviousInstance`, `AnotherProcessUsesFolder`, `CannotWriteFolderPrefix`. Test helpers `Eventually.TrueAsync(Func<bool>, string)`, `Eventually.SettleAsync()`, `Eventually.AdvanceUntilAsync(FakeTimeProvider, TimeSpan step, Func<bool>, string)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Jellyfin.Plugin.TgJelly.Tests/Eventually.cs`. Background loops driven by a `FakeTimeProvider` run their continuations on the thread pool, so tests wait for them in real time. `AdvanceUntilAsync` exists because a loop registers its next timer only after it has run: one big `Advance` can happen before the timer exists and then never fire (this made tests flaky on 2-CPU machines):

```csharp
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests;

/// <summary>
/// Waits (in real time) for background loops driven by a FakeTimeProvider to catch up.
/// </summary>
public static class Eventually
{
    public static async Task TrueAsync(Func<bool> condition, string because)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5))
            {
                Assert.Fail("Timed out waiting: " + because);
            }

            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Gives background continuations a moment to run, for asserting that something did NOT happen.
    /// </summary>
    public static Task SettleAsync() => Task.Delay(100);

    /// <summary>
    /// Moves fake time forward in steps until <paramref name="condition"/> holds. A loop registers its next timer
    /// only after it has run, so each step first lets it settle; a single big Advance could happen before the timer
    /// exists and then never fire.
    /// </summary>
    public static async Task AdvanceUntilAsync(FakeTimeProvider time, TimeSpan step, Func<bool> condition, string because, int maxSteps = 40)
    {
        ArgumentNullException.ThrowIfNull(time);
        for (var i = 0; i < maxSteps && !condition(); i++)
        {
            await SettleAsync();
            time.Advance(step);
        }

        await TrueAsync(condition, because);
    }
}
```

Create `tests/Jellyfin.Plugin.TgJelly.Tests/Hosting/SupervisorTests.cs`:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Hosting;

public class SupervisorTests
{
    [Fact]
    public async Task FailingBody_IsRestarted_WithDoublingBackoff()
    {
        var time = new FakeTimeProvider();
        var attempts = 0;
        using var cts = new CancellationTokenSource();

        var run = Supervisor.RunAsync(
            "test",
            _ =>
            {
                attempts++;
                return attempts < 4 ? throw new InvalidOperationException("boom") : Task.CompletedTask;
            },
            time,
            NullLogger.Instance,
            cts.Token);

        // Each retry is checked by settling first (so the backoff timer exists), then advancing exactly its delay.
        await Eventually.TrueAsync(() => attempts == 1, "first attempt");
        await Eventually.SettleAsync();
        time.Advance(TimeSpan.FromMilliseconds(999));
        await Eventually.SettleAsync();
        Assert.Equal(1, attempts);

        time.Advance(TimeSpan.FromMilliseconds(1));
        await Eventually.TrueAsync(() => attempts == 2, "retry after 1 s");
        await Eventually.SettleAsync();
        time.Advance(TimeSpan.FromSeconds(2));
        await Eventually.TrueAsync(() => attempts == 3, "retry after 2 s");
        await Eventually.SettleAsync();
        time.Advance(TimeSpan.FromSeconds(4));
        await Eventually.TrueAsync(() => attempts == 4, "retry after 4 s");

        await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RunAsync_ReturnsToTheCaller_BeforeRunningTheBody()
    {
        // A blocking body must not block the caller (BackgroundService.StartAsync on .NET 9 calls ExecuteAsync inline).
        using var release = new ManualResetEventSlim();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var run = Supervisor.RunAsync(
            "test",
            _ =>
            {
                release.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
                return Task.CompletedTask;
            },
            TimeProvider.System,
            NullLogger.Instance,
            CancellationToken.None);
        var returnedAfter = watch.Elapsed;
        release.Set();

        Assert.True(returnedAfter < TimeSpan.FromSeconds(1), $"RunAsync blocked the caller for {returnedAfter}");
        await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Cancellation_EndsQuietly_EvenDuringBackoff()
    {
        var time = new FakeTimeProvider();
        using var cts = new CancellationTokenSource();

        var run = Supervisor.RunAsync("test", _ => throw new InvalidOperationException("boom"), time, NullLogger.Instance, cts.Token);
        await Eventually.SettleAsync();
        await cts.CancelAsync();

        await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(run.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task OperationCanceledFromBody_WhenStopping_IsNotAnError()
    {
        using var cts = new CancellationTokenSource();
        var run = Supervisor.RunAsync(
            "test",
            async token => await Task.Delay(Timeout.Infinite, token),
            TimeProvider.System,
            NullLogger.Instance,
            cts.Token);

        await cts.CancelAsync();

        await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(run.IsCompletedSuccessfully);
    }
}
```

Create `tests/Jellyfin.Plugin.TgJelly.Tests/State/InstanceGateTests.cs`:

```csharp
using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.State;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.State;

public class InstanceGateTests
{
    [Fact]
    public void Lock_IsExclusive_EvenInsideOneProcess()
    {
        using var dir = new TempDir();
        var path = new DataPaths(dir.Path).Lock;

        using (var first = InstanceLock.TryAcquire(path))
        {
            Assert.NotNull(first);
            Assert.Null(InstanceLock.TryAcquire(path));
        }

        using var again = InstanceLock.TryAcquire(path);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task FreeLock_IsOwnedRightAway_AndReleasedOnStop()
    {
        using var dir = new TempDir();
        var paths = new DataPaths(dir.Path);
        using var gate = new InstanceGate(paths, new FakeTimeProvider(), NullLogger<InstanceGate>.Instance);

        await gate.StartAsync(CancellationToken.None);
        await gate.WaitForOwnershipAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(gate.IsOwner);
        Assert.Null(gate.WaitingMessage);
        Assert.Null(InstanceLock.TryAcquire(paths.Lock));

        await gate.StopAsync(CancellationToken.None);
        using var after = InstanceLock.TryAcquire(paths.Lock);
        Assert.NotNull(after);
    }

    [Fact]
    public async Task BusyLock_ReportsWaiting_ThenForeign_ThenAcquiresWhenFreed()
    {
        using var dir = new TempDir();
        var paths = new DataPaths(dir.Path);
        var time = new FakeTimeProvider();
        var other = InstanceLock.TryAcquire(paths.Lock);
        using var gate = new InstanceGate(paths, time, NullLogger<InstanceGate>.Instance);

        await gate.StartAsync(CancellationToken.None);
        await Eventually.SettleAsync();
        Assert.False(gate.IsOwner);
        Assert.Equal(InstanceGate.WaitingForPreviousInstance, gate.WaitingMessage);

        await Eventually.AdvanceUntilAsync(
            time,
            TimeSpan.FromSeconds(5),
            () => gate.WaitingMessage == InstanceGate.AnotherProcessUsesFolder,
            "foreign message after 2 min");

        other!.Dispose();
        await Eventually.AdvanceUntilAsync(time, TimeSpan.FromSeconds(5), () => gate.IsOwner, "acquired after the other holder released");

        await gate.StopAsync(CancellationToken.None);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task UnwritableFolder_ReportsAPermissionProblem_ThenAcquiresOnceFixed()
    {
        // Review Focus 1: e.g. a Docker volume with the wrong owner. This is not "another instance".
        Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.UserName == "root", "needs Unix permissions as a normal user");
        using var dir = new TempDir();
        var paths = new DataPaths(dir.Path);
        Directory.CreateDirectory(paths.Root);
        File.SetUnixFileMode(paths.Root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var time = new FakeTimeProvider();
        using var gate = new InstanceGate(paths, time, NullLogger<InstanceGate>.Instance);
        try
        {
            await gate.StartAsync(CancellationToken.None);
            await Eventually.TrueAsync(
                () => gate.WaitingMessage?.StartsWith(InstanceGate.CannotWriteFolderPrefix, StringComparison.Ordinal) == true,
                "permission problem reported");
            Assert.Contains(paths.Root, gate.WaitingMessage, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(paths.Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        await Eventually.AdvanceUntilAsync(time, TimeSpan.FromSeconds(5), () => gate.IsOwner, "acquired once the folder became writable");
        await gate.StopAsync(CancellationToken.None);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet test --project tests/Jellyfin.Plugin.TgJelly.Tests -c Release --filter-class '*.SupervisorTests'`

Expected: FAIL — build error `CS0234: The type or namespace name 'Hosting' does not exist in the namespace 'Jellyfin.Plugin.TgJelly'`.

- [ ] **Step 3: Write the implementation**

Create `src/Jellyfin.Plugin.TgJelly/Hosting/Supervisor.cs`:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.Hosting;

/// <summary>
/// Runs a background loop so that an exception can never escape to the host (spec §5.4 rule 2, F3).
/// A failed body is restarted with backoff 1 s → 60 s; the backoff resets after a run of at least 60 s.
/// </summary>
public static class Supervisor
{
    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Runs <paramref name="body"/> until it completes normally or <paramref name="stoppingToken"/> is cancelled.
    /// Never throws.
    /// </summary>
    /// <param name="name">A short name for log messages.</param>
    /// <param name="body">The loop body.</param>
    /// <param name="time">The clock used for backoff delays.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="stoppingToken">Cancelled when the host stops.</param>
    /// <returns>A task that completes when the loop ends.</returns>
    public static async Task RunAsync(
        string name,
        Func<CancellationToken, Task> body,
        TimeProvider time,
        ILogger logger,
        CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        // Return to the caller at once, so StartAsync never runs loop work inline (.NET 9 BackgroundService
        // runs ExecuteAsync synchronously up to its first await; .NET 10 does not).
        await Task.Yield();

        var delay = FirstDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            var startedAt = time.GetUtcNow();
            try
            {
                await body(stoppingToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (time.GetUtcNow() - startedAt >= MaxDelay)
                {
                    delay = FirstDelay;
                }

                logger.LogError(ex, "TgJelly {Name} failed; restarting in {Delay}", name, delay);
            }

            try
            {
                await Task.Delay(delay, time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = delay * 2 > MaxDelay ? MaxDelay : delay * 2;
        }
    }
}
```

Create `src/Jellyfin.Plugin.TgJelly/State/InstanceLock.cs`:

```csharp
using System;
using System.IO;

namespace Jellyfin.Plugin.TgJelly.State;

/// <summary>
/// An exclusive lock on <c>state.lock</c>, held while this plugin instance owns the data folder (spec §10.4).
/// </summary>
public sealed class InstanceLock : IDisposable
{
    private readonly FileStream _stream;

    private InstanceLock(FileStream stream)
    {
        _stream = stream;
    }

    /// <summary>
    /// Tries to take the lock without waiting.
    /// </summary>
    /// <param name="path">The lock file path.</param>
    /// <param name="denied">
    /// Set when the lock could not even be attempted (e.g. the folder is not writable); <c>null</c> when the lock is
    /// simply held by another instance or was acquired.
    /// </param>
    /// <returns>The held lock, or <c>null</c> when it is busy or denied.</returns>
    public static InstanceLock? TryAcquire(string path, out Exception? denied)
    {
        denied = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            return new InstanceLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException ex) when (IsLockedByAnotherHolder(ex))
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            denied = ex;
            return null;
        }
    }

    /// <summary>
    /// Tries to take the lock without waiting.
    /// </summary>
    /// <param name="path">The lock file path.</param>
    /// <returns>The held lock, or <c>null</c> when another instance holds it.</returns>
    public static InstanceLock? TryAcquire(string path) => TryAcquire(path, out _);

    /// <inheritdoc />
    public void Dispose() => _stream.Dispose();

    // EWOULDBLOCK on Linux (11) and macOS (35); sharing and lock violations on Windows.
    private static bool IsLockedByAnotherHolder(IOException ex)
        => ex.HResult is 11 or 35 or unchecked((int)0x80070020) or unchecked((int)0x80070021);
}
```

Create `src/Jellyfin.Plugin.TgJelly/State/InstanceGate.cs`:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.State;

/// <summary>
/// Owns <c>state.lock</c>. Until it has the lock, nothing that consumes intake or talks to Telegram may start
/// (spec §10.4). Retries every 5 s.
/// </summary>
public sealed class InstanceGate : BackgroundService
{
    /// <summary>Status text while the lock is busy for less than 2 minutes.</summary>
    public const string WaitingForPreviousInstance = "Waiting for the previous instance to stop";

    /// <summary>Status text once the lock has been busy for 2 minutes or more.</summary>
    public const string AnotherProcessUsesFolder = "Another Jellyfin process is using TgJelly's data folder";

    /// <summary>Start of the status text when the data folder cannot be written at all.</summary>
    public const string CannotWriteFolderPrefix = "TgJelly can't write to its data folder";

    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ForeignAfter = TimeSpan.FromMinutes(2);

    private readonly DataPaths _paths;
    private readonly TimeProvider _time;
    private readonly ILogger<InstanceGate> _logger;
    private readonly TaskCompletionSource _owned = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _gate = new();
    private InstanceLock? _lock;
    private string _waitingMessage = WaitingForPreviousInstance;

    /// <summary>
    /// Initializes a new instance of the <see cref="InstanceGate"/> class.
    /// </summary>
    /// <param name="paths">TgJelly's data paths.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public InstanceGate(DataPaths paths, TimeProvider time, ILogger<InstanceGate> logger)
    {
        _paths = paths;
        _time = time;
        _logger = logger;
    }

    /// <summary>Gets a value indicating whether this instance holds the lock.</summary>
    public bool IsOwner => _owned.Task.IsCompletedSuccessfully;

    /// <summary>Gets the status text while waiting for the lock; <c>null</c> once owned.</summary>
    public string? WaitingMessage
    {
        get
        {
            lock (_gate)
            {
                return IsOwner ? null : _waitingMessage;
            }
        }
    }

    /// <summary>
    /// Completes once this instance holds the lock.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes when the lock is held.</returns>
    public Task WaitForOwnershipAsync(CancellationToken cancellationToken) => _owned.Task.WaitAsync(cancellationToken);

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TgJelly instance gate did not stop cleanly");
        }
        finally
        {
            Release();
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        Release();
        base.Dispose();
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Supervisor.RunAsync("instance gate", AcquireAsync, _time, _logger, stoppingToken);

    private async Task AcquireAsync(CancellationToken stoppingToken)
    {
        var busySince = (DateTimeOffset?)null;
        string? lastDenied = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var acquired = InstanceLock.TryAcquire(_paths.Lock, out var denied);
            if (acquired is not null)
            {
                lock (_gate)
                {
                    _lock = acquired;
                }

                _owned.TrySetResult();
                _logger.LogInformation("TgJelly owns its data folder {Path}", _paths.Root);
                return;
            }

            string message;
            if (denied is not null)
            {
                busySince = null;
                message = CannotWriteFolderPrefix + " " + _paths.Root + ": " + denied.Message + " Check the folder's owner and permissions.";
                if (message != lastDenied)
                {
                    _logger.LogError(denied, "TgJelly can't write to its data folder {Path}", _paths.Root);
                    lastDenied = message;
                }
            }
            else
            {
                busySince ??= _time.GetUtcNow();
                lastDenied = null;
                message = _time.GetUtcNow() - busySince.Value < ForeignAfter ? WaitingForPreviousInstance : AnotherProcessUsesFolder;
            }

            lock (_gate)
            {
                _waitingMessage = message;
            }

            await Task.Delay(RetryInterval, _time, stoppingToken).ConfigureAwait(false);
        }
    }

    private void Release()
    {
        lock (_gate)
        {
            _lock?.Dispose();
            _lock = null;
        }
    }
}
```

- [ ] **Step 4: Build and run all tests three times, then once on 2 CPUs** (they use real-time waits; a flaky test must be fixed, not retried)

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet build Jellyfin.Plugin.TgJelly.slnx -c Release && ( set -e; for i in 1 2 3; do dotnet test --solution Jellyfin.Plugin.TgJelly.slnx -c Release --no-build; done; if command -v taskset >/dev/null; then taskset -c 0,1 dotnet test --solution Jellyfin.Plugin.TgJelly.slnx -c Release --no-build; fi )`

Expected: PASS — `0 Warning(s)`, three runs of `total: 118`, `failed: 0`, plus a fourth run pinned to 2 CPUs (GitHub's runners for private repos have 2), which is where timing bugs show.

- [ ] **Step 5: Commit**

```bash
git add src/Jellyfin.Plugin.TgJelly/Hosting src/Jellyfin.Plugin.TgJelly/State tests/Jellyfin.Plugin.TgJelly.Tests/Eventually.cs tests/Jellyfin.Plugin.TgJelly.Tests/Hosting tests/Jellyfin.Plugin.TgJelly.Tests/State
git commit -m "feat: supervised background loops and the state.lock instance gate"
```

---

### Task 6: State store

`state.json` (small, written within 1 s) and `queue.json` (written at most every 10 s, or within 1 s after an urgent change), unreadable files moved aside, and — Review Focus item 1 — a failed write never loses a change: each file is written on its own, a failure re-marks only that file dirty, and the first error is rethrown after both attempts (§10.3, §10.4). Later plans add their sections to `StateDocument` and `QueueDocument`.

**Files:**
- Create (in `src/Jellyfin.Plugin.TgJelly/State/Documents/`): `GlobalSuppressions.cs`, `StateDocument.cs`, `StatusSection.cs`, `QueueDocument.cs`
- Create: `src/Jellyfin.Plugin.TgJelly/State/StateStore.cs`
- Test: `tests/Jellyfin.Plugin.TgJelly.Tests/State/StateStoreTests.cs`

**Interfaces:**
- Consumes: `DataPaths`, `AtomicFile`, `JsonDefaults` (Task 4).
- Produces: `StateStore(DataPaths, TimeProvider, ILogger<StateStore>)` with `IsLoaded`, `Load()`, `Read<T>(Func<StateDocument, T>)`, `Update(Action<StateDocument>)` (throws `InvalidOperationException` before `Load`), `ReadQueue<T>(Func<QueueDocument, T>)`, `UpdateQueue(Action<QueueDocument>, bool urgent = false)`, `FlushDue()`, `FlushAll()`, `QueueWriteInterval` (10 s), `MaxLoadWarnings` (20). `StateDocument { Version, Status }`; `StatusSection { DateTimeOffset? BackupRestoredAt; List<string> LoadWarnings }`; `QueueDocument { Version }`.

- [ ] **Step 1: Write the failing test**

Create `tests/Jellyfin.Plugin.TgJelly.Tests/State/StateStoreTests.cs`:

```csharp
using System;
using System.IO;
using System.Runtime.Versioning;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.State;

public class StateStoreTests
{
    private static readonly DateTimeOffset Restored = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BeforeLoad_ReadsDefaults_AndRefusesUpdates()
    {
        using var dir = new TempDir();
        var store = NewStore(dir, new FakeTimeProvider());

        Assert.False(store.IsLoaded);
        Assert.Null(store.Read(s => s.Status.BackupRestoredAt));
        Assert.Throws<InvalidOperationException>(() => store.Update(s => s.Status.BackupRestoredAt = Restored));
    }

    [Fact]
    public void Update_IsWrittenOnNextFlush_AndSurvivesReload()
    {
        using var dir = new TempDir();
        var store = NewStore(dir, new FakeTimeProvider());
        store.Load();

        store.Update(s => s.Status.BackupRestoredAt = Restored);
        store.FlushDue();

        var reloaded = NewStore(dir, new FakeTimeProvider());
        reloaded.Load();
        Assert.Equal(Restored, reloaded.Read(s => s.Status.BackupRestoredAt));
        Assert.Contains("\"backupRestoredAt\"", File.ReadAllText(new DataPaths(dir.Path).State), StringComparison.Ordinal);
    }

    [Fact]
    public void FlushDue_WithoutChanges_WritesNothing()
    {
        using var dir = new TempDir();
        var store = NewStore(dir, new FakeTimeProvider());
        store.Load();

        store.FlushDue();

        Assert.False(File.Exists(new DataPaths(dir.Path).State));
        Assert.False(File.Exists(new DataPaths(dir.Path).Queue));
    }

    [Fact]
    public void Queue_IsWrittenAtMostEvery10Seconds_UnlessUrgent()
    {
        using var dir = new TempDir();
        var queuePath = new DataPaths(dir.Path).Queue;
        var time = new FakeTimeProvider();
        var store = NewStore(dir, time);
        store.Load();

        store.UpdateQueue(q => q.Version = 1);
        store.FlushDue();
        Assert.True(File.Exists(queuePath));

        File.Delete(queuePath);
        store.UpdateQueue(q => q.Version = 1);
        time.Advance(TimeSpan.FromSeconds(5));
        store.FlushDue();
        Assert.False(File.Exists(queuePath));

        time.Advance(TimeSpan.FromSeconds(5));
        store.FlushDue();
        Assert.True(File.Exists(queuePath));

        File.Delete(queuePath);
        time.Advance(TimeSpan.FromSeconds(1));
        store.UpdateQueue(q => q.Version = 1, urgent: true);
        store.FlushDue();
        Assert.True(File.Exists(queuePath));
    }

    [Fact]
    public void FlushAll_WritesPendingQueueImmediately()
    {
        using var dir = new TempDir();
        var queuePath = new DataPaths(dir.Path).Queue;
        var store = NewStore(dir, new FakeTimeProvider());
        store.Load();
        store.UpdateQueue(q => q.Version = 1);
        store.FlushDue();
        File.Delete(queuePath);

        store.UpdateQueue(q => q.Version = 1);
        store.FlushAll();

        Assert.True(File.Exists(queuePath));
    }

    [Fact]
    public void CorruptStateFile_IsQuarantined_AndReportedAsWarning()
    {
        using var dir = new TempDir();
        var paths = new DataPaths(dir.Path);
        Directory.CreateDirectory(paths.Root);
        File.WriteAllText(paths.State, "{definitely not json");
        var store = NewStore(dir, new FakeTimeProvider(Restored));

        store.Load();

        Assert.True(store.IsLoaded);
        var warning = Assert.Single(store.Read(s => s.Status.LoadWarnings.ToArray()));
        Assert.Equal("state.json was unreadable and has been moved to state.bad-20261016090000.json.", warning);
        Assert.True(File.Exists(Path.Combine(paths.Root, "state.bad-20261016090000.json")));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void FailedWrite_KeepsTheChange_AndWritesItOnceTheFolderIsWritableAgain()
    {
        // Review Focus 1: a read-only data folder makes both writes fail; neither change may be lost.
        Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.UserName == "root", "needs Unix permissions as a normal user");
        using var dir = new TempDir();
        var paths = new DataPaths(dir.Path);
        Directory.CreateDirectory(paths.Root);
        var store = NewStore(dir, new FakeTimeProvider());
        store.Load();
        store.Update(s => s.Status.BackupRestoredAt = Restored);
        store.UpdateQueue(q => q.Version = 1, urgent: true);

        File.SetUnixFileMode(paths.Root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.ThrowsAny<UnauthorizedAccessException>(store.FlushDue);
        }
        finally
        {
            File.SetUnixFileMode(paths.Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        store.FlushDue();
        Assert.Contains("backupRestoredAt", File.ReadAllText(paths.State), StringComparison.Ordinal);
        Assert.True(File.Exists(paths.Queue));
    }

    [Fact]
    public void OnlyTheStateWriteFails_TheQueueIsStillWritten_AndStateFollowsLater()
    {
        using var dir = new TempDir();
        var paths = new DataPaths(dir.Path);
        Directory.CreateDirectory(paths.State); // a folder where state.json should be: that write fails
        var store = NewStore(dir, new FakeTimeProvider());
        store.Load();
        store.Update(s => s.Status.BackupRestoredAt = Restored);
        store.UpdateQueue(q => q.Version = 1, urgent: true);

        Assert.NotNull(Record.Exception(store.FlushDue));
        Assert.True(File.Exists(paths.Queue));

        Directory.Delete(paths.State);
        store.FlushDue();
        Assert.Contains("backupRestoredAt", File.ReadAllText(paths.State), StringComparison.Ordinal);
    }

    [Fact]
    public void LoadWarnings_AreCapped_KeepingTheNewest()
    {
        using var dir = new TempDir();
        var paths = new DataPaths(dir.Path);
        Directory.CreateDirectory(paths.Root);
        var old = new StateDocument();
        for (var i = 0; i < StateStore.MaxLoadWarnings; i++)
        {
            old.Status.LoadWarnings.Add("old " + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        File.WriteAllText(paths.State, System.Text.Json.JsonSerializer.Serialize(old, JsonDefaults.Options));
        File.WriteAllText(paths.Queue, "{broken");
        var store = NewStore(dir, new FakeTimeProvider(Restored));

        store.Load();

        var warnings = store.Read(s => s.Status.LoadWarnings.ToArray());
        Assert.Equal(StateStore.MaxLoadWarnings, warnings.Length);
        Assert.Equal("old 1", warnings[0]);
        Assert.StartsWith("queue.json was unreadable", warnings[^1], StringComparison.Ordinal);
    }

    private static StateStore NewStore(TempDir dir, FakeTimeProvider time)
        => new(new DataPaths(dir.Path), time, NullLogger<StateStore>.Instance);

    [Fact]
    public void NullForANonNullableSection_IsTreatedAsUnreadable()
    {
        using var dir = new TempDir();
        var paths = new DataPaths(dir.Path);
        Directory.CreateDirectory(paths.Root);
        File.WriteAllText(paths.State, "{\"version\": 1, \"status\": null}");
        var store = NewStore(dir, new FakeTimeProvider(Restored));

        store.Load();

        Assert.NotNull(store.Read(s => s.Status));
        Assert.Single(store.Read(s => s.Status.LoadWarnings.ToArray()));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet test --project tests/Jellyfin.Plugin.TgJelly.Tests -c Release --filter-class '*.StateStoreTests'`

Expected: FAIL — build error `CS0246: The type or namespace name 'StateStore' could not be found`.

- [ ] **Step 3: Write the implementation**

Create `src/Jellyfin.Plugin.TgJelly/State/Documents/GlobalSuppressions.cs`. The documents are plain JSON holders with mutable collections; the suppressions are scoped to this one namespace:

```csharp
using System.Diagnostics.CodeAnalysis;

// The state documents are plain JSON data holders: System.Text.Json replaces them wholesale on load,
// and the owning stores mutate them only under their own locks.
[assembly: SuppressMessage(
    "Usage",
    "CA2227:Collection properties should be read only",
    Justification = "JSON state documents are replaced wholesale on load.",
    Scope = "namespaceanddescendants",
    Target = "~N:Jellyfin.Plugin.TgJelly.State.Documents")]
[assembly: SuppressMessage(
    "Design",
    "CA1002:Do not expose generic lists",
    Justification = "JSON state documents are internal data holders.",
    Scope = "namespaceanddescendants",
    Target = "~N:Jellyfin.Plugin.TgJelly.State.Documents")]
```

Create `src/Jellyfin.Plugin.TgJelly/State/Documents/StateDocument.cs`:

```csharp
namespace Jellyfin.Plugin.TgJelly.State.Documents;

/// <summary>
/// The contents of state.json: small, bot-scoped runtime state saved within 1 s of a change (spec §10.3).
/// Later plans add their sections here.
/// </summary>
public sealed class StateDocument
{
    /// <summary>Gets or sets the document format version.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Gets or sets the status section shown on the dashboard.</summary>
    public StatusSection Status { get; set; } = new();
}
```

Create `src/Jellyfin.Plugin.TgJelly/State/Documents/StatusSection.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TgJelly.State.Documents;

/// <summary>
/// Notices for the dashboard's Status section (spec §6.6, §10.3 <c>status</c>).
/// </summary>
public sealed class StatusSection
{
    /// <summary>Gets or sets when a wiped config was restored from settings.bak.json.</summary>
    public DateTimeOffset? BackupRestoredAt { get; set; }

    /// <summary>Gets or sets warnings from loading the state files (e.g. a quarantined file).</summary>
    public List<string> LoadWarnings { get; set; } = [];
}
```

Create `src/Jellyfin.Plugin.TgJelly/State/Documents/QueueDocument.cs`:

```csharp
namespace Jellyfin.Plugin.TgJelly.State.Documents;

/// <summary>
/// The contents of queue.json: larger pipeline state saved every 10 s while it changes, or within 1 s
/// after an urgent change (spec §10.3). Later plans add their sections here.
/// </summary>
public sealed class QueueDocument
{
    /// <summary>Gets or sets the document format version.</summary>
    public int Version { get; set; } = 1;
}
```

Create `src/Jellyfin.Plugin.TgJelly/State/StateStore.cs`:

```csharp
using System;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.TgJelly.State.Documents;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.State;

/// <summary>
/// Holds state.json and queue.json in memory and writes them on schedule (spec §10.3, §10.4).
/// Call <see cref="Load"/> once, after <see cref="InstanceGate"/> owns the data folder.
/// </summary>
public sealed class StateStore
{
    /// <summary>How many load warnings are kept (oldest dropped first), like the other capped status lists.</summary>
    public const int MaxLoadWarnings = 20;

    /// <summary>How long queue.json may stay dirty before it is written.</summary>
    public static readonly TimeSpan QueueWriteInterval = TimeSpan.FromSeconds(10);

    private readonly DataPaths _paths;
    private readonly TimeProvider _time;
    private readonly ILogger<StateStore> _logger;
    private readonly Lock _gate = new();
    private readonly Lock _writeGate = new();
    private StateDocument _state = new();
    private QueueDocument _queue = new();
    private bool _loaded;
    private bool _stateDirty;
    private bool _queueDirty;
    private bool _queueUrgent;
    private DateTimeOffset _lastQueueWrite = DateTimeOffset.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="StateStore"/> class.
    /// </summary>
    /// <param name="paths">TgJelly's data paths.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public StateStore(DataPaths paths, TimeProvider time, ILogger<StateStore> logger)
    {
        _paths = paths;
        _time = time;
        _logger = logger;
    }

    /// <summary>Gets a value indicating whether <see cref="Load"/> has run.</summary>
    public bool IsLoaded
    {
        get
        {
            lock (_gate)
            {
                return _loaded;
            }
        }
    }

    /// <summary>
    /// Reads both files. Unreadable files are moved aside and replaced by empty documents.
    /// </summary>
    public void Load()
    {
        var now = _time.GetUtcNow();
        var state = ReadOrDefault<StateDocument>(_paths.State, now, out var stateWarning);
        var queue = ReadOrDefault<QueueDocument>(_paths.Queue, now, out var queueWarning);
        lock (_gate)
        {
            _state = state;
            _queue = queue;
            foreach (var warning in new[] { stateWarning, queueWarning })
            {
                if (warning is not null)
                {
                    _state.Status.LoadWarnings.Add(warning);
                    _stateDirty = true;
                }
            }

            while (_state.Status.LoadWarnings.Count > MaxLoadWarnings)
            {
                _state.Status.LoadWarnings.RemoveAt(0);
                _stateDirty = true;
            }

            _loaded = true;
        }
    }

    /// <summary>
    /// Reads from state.json's document.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="read">Reads the value; must not keep references to the document.</param>
    /// <returns>The value.</returns>
    public T Read<T>(Func<StateDocument, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        lock (_gate)
        {
            return read(_state);
        }
    }

    /// <summary>
    /// Changes state.json's document; it is written within 1 s.
    /// </summary>
    /// <param name="change">The change.</param>
    public void Update(Action<StateDocument> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            EnsureLoaded();
            change(_state);
            _stateDirty = true;
        }
    }

    /// <summary>
    /// Reads from queue.json's document.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="read">Reads the value; must not keep references to the document.</param>
    /// <returns>The value.</returns>
    public T ReadQueue<T>(Func<QueueDocument, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        lock (_gate)
        {
            return read(_queue);
        }
    }

    /// <summary>
    /// Changes queue.json's document; it is written within 10 s, or within 1 s when <paramref name="urgent"/>.
    /// </summary>
    /// <param name="change">The change.</param>
    /// <param name="urgent"><c>true</c> after queuing a security or server entry.</param>
    public void UpdateQueue(Action<QueueDocument> change, bool urgent = false)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            EnsureLoaded();
            change(_queue);
            _queueDirty = true;
            _queueUrgent |= urgent;
        }
    }

    /// <summary>
    /// Writes whatever is due now. Called once per second by the persistence loop.
    /// </summary>
    public void FlushDue() => Flush(force: false);

    /// <summary>
    /// Writes everything that changed, e.g. on shutdown.
    /// </summary>
    public void FlushAll() => Flush(force: true);

    private void Flush(bool force)
    {
        lock (_writeGate)
        {
            var now = _time.GetUtcNow();
            string? stateJson = null;
            string? queueJson = null;
            lock (_gate)
            {
                if (!_loaded)
                {
                    return;
                }

                if (_stateDirty)
                {
                    stateJson = JsonSerializer.Serialize(_state, JsonDefaults.Options);
                    _stateDirty = false;
                }

                if (_queueDirty && (force || _queueUrgent || now - _lastQueueWrite >= QueueWriteInterval))
                {
                    queueJson = JsonSerializer.Serialize(_queue, JsonDefaults.Options);
                    _queueDirty = false;
                    _queueUrgent = false;
                    _lastQueueWrite = now;
                }
            }

            // Each file is written on its own. A failed write (e.g. a read-only data folder) marks only that
            // file dirty again, so no change is lost; the first error is rethrown after both attempts.
            Exception? firstError = null;
            if (stateJson is not null)
            {
                try
                {
                    AtomicFile.WriteAllText(_paths.State, stateJson);
                }
                catch (Exception ex)
                {
                    lock (_gate)
                    {
                        _stateDirty = true;
                    }

                    firstError ??= ex;
                }
            }

            if (queueJson is not null)
            {
                try
                {
                    AtomicFile.WriteAllText(_paths.Queue, queueJson);
                }
                catch (Exception ex)
                {
                    lock (_gate)
                    {
                        _queueDirty = true;
                        _queueUrgent = true;
                    }

                    firstError ??= ex;
                }
            }

            if (firstError is not null)
            {
                ExceptionDispatchInfo.Capture(firstError).Throw();
            }
        }
    }

    private void EnsureLoaded()
    {
        if (!_loaded)
        {
            throw new InvalidOperationException("TgJelly state is not loaded yet.");
        }
    }

    private T ReadOrDefault<T>(string path, DateTimeOffset now, out string? warning)
        where T : new()
    {
        warning = null;
        if (!File.Exists(path))
        {
            return new T();
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonDefaults.Options) ?? new T();
        }
        catch (JsonException ex)
        {
            var moved = AtomicFile.Quarantine(path, now);
            warning = Path.GetFileName(path) + " was unreadable and has been moved to " + Path.GetFileName(moved) + ".";
            _logger.LogError(ex, "TgJelly {File} was unreadable and has been moved to {Path}", Path.GetFileName(path), moved);
            return new T();
        }
    }
}
```

- [ ] **Step 4: Build and run all tests**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet build Jellyfin.Plugin.TgJelly.slnx -c Release && dotnet test --solution Jellyfin.Plugin.TgJelly.slnx -c Release --no-build`

Expected: PASS — `0 Warning(s)`, `total: 138`, `failed: 0`. (The read-only-folder test skips itself on Windows and when running as root.)

- [ ] **Step 5: Verify the Review Focus test guards the fix** (a one-off mutation check; revert afterwards)

Back the file up, then break the fix on purpose:

```bash
cp src/Jellyfin.Plugin.TgJelly/State/StateStore.cs /tmp/StateStore.cs.keep
```

In `StateStore.Flush`, in the `catch` of the **state.json** write, change `firstError ??= ex;` to `throw;`. Run `export PATH="$HOME/.dotnet:$PATH" && dotnet test --project tests/Jellyfin.Plugin.TgJelly.Tests -c Release -f net10.0 --filter-class '*.StateStoreTests'` and confirm that `OnlyTheStateWriteFails_TheQueueIsStillWritten_AndStateFollowsLater` **fails**. Then restore and prove it's restored:

```bash
cp /tmp/StateStore.cs.keep src/Jellyfin.Plugin.TgJelly/State/StateStore.cs
cmp /tmp/StateStore.cs.keep src/Jellyfin.Plugin.TgJelly/State/StateStore.cs && echo restored
```

Re-run the suite from Step 4.

- [ ] **Step 6: Commit**

```bash
git add src/Jellyfin.Plugin.TgJelly/State tests/Jellyfin.Plugin.TgJelly.Tests/State/StateStoreTests.cs
git commit -m "feat: state store with two-file cadence and quarantine (spec 10.3)"
```

---

### Task 7: SettingsStore and outside-save detection

The only way TgJelly changes settings: lock → clone → change → validate → revision+1 → save → `settings.bak.json` → `SettingsChanged` once (§10.4). The lock is the host's private save lock (`RunExclusive`), which outside saves take too. The backup is best-effort (a failed backup never turns a saved change into an error), and `SettingsChanged` is delivered per handler with errors isolated, never going back to an older revision. `Plugin` implements `IConfigurationHost`; saves that don't come from `SettingsStore` (e.g. Jellyfin's generic configuration endpoint, which replaces the whole object, F9) still get a higher revision and a backup. `RestoreBackupIfWiped` undoes Jellyfin's "reset to defaults after a failed load".

**Files:**
- Create (in `src/Jellyfin.Plugin.TgJelly/Configuration/`): `IConfigurationHost.cs`, `ConfigurationSavedEventArgs.cs`, `SettingsChangedEventArgs.cs`, `SettingsUpdateStatus.cs`, `SettingsUpdateResult.cs`, `SettingsStore.cs`
- Replace: `src/Jellyfin.Plugin.TgJelly/Plugin.cs`
- Create (in `tests/Jellyfin.Plugin.TgJelly.Tests/Fakes/`): `TestXmlSerializer.cs`, `TestPlugin.cs`, `FakeConfigurationHost.cs`
- Test: `tests/Jellyfin.Plugin.TgJelly.Tests/Configuration/SettingsStoreTests.cs`, `tests/Jellyfin.Plugin.TgJelly.Tests/PluginConfigurationHostTests.cs`

**Interfaces:**
- Consumes: `PluginConfiguration`, `SettingsValidator` (Task 3); `DataPaths`, `AtomicFile`, `JsonDefaults` (Task 4); `StateStore` (Task 6).
- Produces: `interface IConfigurationHost { event EventHandler<ConfigurationSavedEventArgs>? ExternalConfigurationSaved; PluginConfiguration Configuration { get; } T RunExclusive<T>(Func<T> action); void SaveFromStore(PluginConfiguration); }`; `SettingsStore(IConfigurationHost, DataPaths, StateStore, TimeProvider, ILogger<SettingsStore>)` with `Current` (a copy), `Update(long? expectedRevision, Action<PluginConfiguration>) → SettingsUpdateResult`, `RestoreBackupIfWiped() → bool`, `event EventHandler<SettingsChangedEventArgs>? SettingsChanged`, `internal RaiseSettingsChanged(PluginConfiguration)`, `Dispose()`; `Plugin` implements `RunExclusive` and normalizes outside saves; `enum SettingsUpdateStatus { Saved, Conflict, Invalid }`; `record SettingsUpdateResult(SettingsUpdateStatus Status, PluginConfiguration Configuration, IReadOnlyList<SettingsError> Errors)`. Test fakes `TestPlugin.Create(string root) → Plugin`, `FakeConfigurationHost` (`Saves`, `SaveFromOutside`).

- [ ] **Step 1: Write the failing tests**

Create `tests/Jellyfin.Plugin.TgJelly.Tests/Fakes/TestXmlSerializer.cs`. Jellyfin's own `MyXmlSerializer` isn't in the NuGet packages, so this mirrors it:

```csharp
using System;
using System.IO;
using System.Xml.Serialization;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.TgJelly.Tests.Fakes;

/// <summary>
/// Mirrors Jellyfin's MyXmlSerializer (a thin wrapper over XmlSerializer), which is not in the NuGet packages.
/// </summary>
public sealed class TestXmlSerializer : IXmlSerializer
{
    public object DeserializeFromStream(Type type, Stream stream) => new XmlSerializer(type).Deserialize(stream)!;

    public void SerializeToStream(object obj, Stream stream) => new XmlSerializer(obj.GetType()).Serialize(stream, obj);

    public void SerializeToFile(object obj, string file)
    {
        using var stream = new FileStream(file, FileMode.Create, FileAccess.Write);
        SerializeToStream(obj, stream);
    }

    public object DeserializeFromFile(Type type, string file)
    {
        using var stream = File.OpenRead(file);
        return DeserializeFromStream(type, stream);
    }

    public object DeserializeFromBytes(Type type, byte[] buffer)
    {
        using var stream = new MemoryStream(buffer, writable: false);
        return DeserializeFromStream(type, stream);
    }
}
```

Create `tests/Jellyfin.Plugin.TgJelly.Tests/Fakes/TestPlugin.cs`:

```csharp
using System.IO;
using MediaBrowser.Common.Configuration;
using NSubstitute;

namespace Jellyfin.Plugin.TgJelly.Tests.Fakes;

/// <summary>
/// Builds a real <see cref="Plugin"/> whose files live in a temporary folder.
/// </summary>
public static class TestPlugin
{
    public static Plugin Create(string root)
    {
        var paths = Substitute.For<IApplicationPaths>();
        paths.PluginsPath.Returns(Path.Combine(root, "plugins"));
        paths.PluginConfigurationsPath.Returns(Path.Combine(root, "plugins", "configurations"));
        return new Plugin(paths, new TestXmlSerializer());
    }
}
```

Create `tests/Jellyfin.Plugin.TgJelly.Tests/Fakes/FakeConfigurationHost.cs`:

```csharp
using System;
using System.Threading;
using Jellyfin.Plugin.TgJelly.Configuration;

namespace Jellyfin.Plugin.TgJelly.Tests.Fakes;

public sealed class FakeConfigurationHost : IConfigurationHost
{
    public event EventHandler<ConfigurationSavedEventArgs>? ExternalConfigurationSaved;

    public PluginConfiguration Configuration { get; private set; } = new();

    private readonly Lock _saveGate = new();

    public int Saves { get; private set; }

    public T RunExclusive<T>(Func<T> action)
    {
        lock (_saveGate)
        {
            return action();
        }
    }

    public void SaveFromStore(PluginConfiguration configuration)
    {
        Configuration = configuration;
        Saves++;
    }

    /// <summary>Simulates Jellyfin's generic configuration endpoint saving settings.</summary>
    public void SaveFromOutside(PluginConfiguration configuration)
    {
        Configuration = configuration;
        ExternalConfigurationSaved?.Invoke(this, new ConfigurationSavedEventArgs(configuration));
    }
}
```

Create `tests/Jellyfin.Plugin.TgJelly.Tests/Configuration/SettingsStoreTests.cs`. The concurrency test is Review Focus item 4:

```csharp
using System;
using System.IO;
using System.Text.Json;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Configuration;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeConfigurationHost _host = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 16, 10, 0, 0, TimeSpan.Zero));
    private readonly StateStore _state;
    private readonly SettingsStore _store;
    private readonly DataPaths _paths;

    public SettingsStoreTests()
    {
        _paths = new DataPaths(_dir.Path);
        _state = new StateStore(_paths, _time, NullLogger<StateStore>.Instance);
        _state.Load();
        _store = new SettingsStore(_host, _paths, _state, _time, NullLogger<SettingsStore>.Instance);
    }

    [Fact]
    public void Update_Saves_BumpsRevision_WritesBackup_RaisesEventOnce()
    {
        var events = 0;
        _store.SettingsChanged += (_, e) =>
        {
            events++;
            Assert.Equal("https://jf.example.com", e.Configuration.PublicUrl);
        };

        var result = _store.Update(0, c => c.PublicUrl = "https://jf.example.com");

        Assert.Equal(SettingsUpdateStatus.Saved, result.Status);
        Assert.Equal(1, result.Configuration.Revision);
        Assert.Equal(1, _host.Configuration.Revision);
        Assert.Equal(1, events);
        var backup = JsonSerializer.Deserialize<PluginConfiguration>(File.ReadAllText(_paths.SettingsBackup), JsonDefaults.Options)!;
        Assert.Equal("https://jf.example.com", backup.PublicUrl);
        Assert.Equal(1, backup.Revision);
    }

    [Fact]
    public void Update_WithStaleRevision_IsAConflict_AndSavesNothing()
    {
        _store.Update(null, c => c.PublicUrl = "https://a.example.com");

        var result = _store.Update(0, c => c.PublicUrl = "https://b.example.com");

        Assert.Equal(SettingsUpdateStatus.Conflict, result.Status);
        Assert.Equal("https://a.example.com", result.Configuration.PublicUrl);
        Assert.Equal(1, _host.Saves);
    }

    [Fact]
    public void Update_Invalid_ReturnsErrors_AndSavesNothing()
    {
        var result = _store.Update(null, c => c.Batching.DigestThreshold = 1);

        Assert.Equal(SettingsUpdateStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, e => e.Field == "Batching.DigestThreshold");
        Assert.Equal(0, _host.Saves);
        Assert.False(File.Exists(_paths.SettingsBackup));
    }

    [Fact]
    public void Current_IsACopy()
    {
        var copy = _store.Current;
        copy.PublicUrl = "https://changed.example.com";

        Assert.Equal(string.Empty, _host.Configuration.PublicUrl);
    }

    [Fact]
    public void OutsideSave_IsBackedUp_AndRaisesSettingsChanged()
    {
        var events = 0;
        _store.SettingsChanged += (_, _) => events++;

        _host.SaveFromOutside(new PluginConfiguration { Revision = 4, PublicUrl = "https://outside.example.com" });

        Assert.Equal(1, events);
        Assert.Contains("outside.example.com", File.ReadAllText(_paths.SettingsBackup), StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreBackupIfWiped_RestoresBackup_AndRecordsNotice()
    {
        _store.Update(null, c => c.Destinations = [new Destination { Id = "abcd1234", ChatId = 42, Title = "Me" }]);
        _host.SaveFromStore(new PluginConfiguration()); // what Jellyfin does after a failed XML load (F9)

        Assert.True(_store.RestoreBackupIfWiped());

        Assert.Equal(1, _host.Configuration.Revision);
        Assert.Equal(42, Assert.Single(_host.Configuration.Destinations).ChatId);
        Assert.Equal(_time.GetUtcNow(), _state.Read(s => s.Status.BackupRestoredAt));
    }

    [Fact]
    public void RestoreBackupIfWiped_DoesNothing_WhenSettingsAreReal_OrNoBackup()
    {
        Assert.False(_store.RestoreBackupIfWiped());

        _store.Update(null, c => c.PublicUrl = "https://jf.example.com");
        Assert.False(_store.RestoreBackupIfWiped());
        Assert.Equal("https://jf.example.com", _host.Configuration.PublicUrl);
    }

    [Fact]
    public void ConcurrentUpdates_LoseNothing_AndRevisionsStayStrictlyIncreasing()
    {
        // The bot and the dashboard can save at the same moment.
        System.Threading.Tasks.Parallel.For(0, 50, i => _store.Update(
            null,
            c => c.Destinations = [.. c.Destinations, new Destination { Id = "d" + i.ToString("D7", System.Globalization.CultureInfo.InvariantCulture), ChatId = i }]));

        Assert.Equal(50, _host.Configuration.Revision);
        Assert.Equal(50, _host.Configuration.Destinations.Length);
        Assert.Equal(50, _host.Saves);
    }

    [Fact]
    public void BackupWriteFailure_DoesNotTurnASaveIntoAnError()
    {
        Directory.CreateDirectory(_paths.SettingsBackup); // a folder where settings.bak.json should be
        var events = 0;
        _store.SettingsChanged += (_, _) => events++;

        var result = _store.Update(null, c => c.PublicUrl = "https://jf.example.com");
        _host.SaveFromOutside(new PluginConfiguration { Revision = 5 });

        Assert.Equal(SettingsUpdateStatus.Saved, result.Status);
        Assert.Equal(2, events);
    }

    [Fact]
    public void ThrowingHandler_DoesNotFailTheSave_AndOtherHandlersStillRun()
    {
        var secondCalled = false;
        _store.SettingsChanged += (_, _) => throw new InvalidOperationException("handler bug");
        _store.SettingsChanged += (_, _) => secondCalled = true;

        var result = _store.Update(null, c => c.PublicUrl = "https://jf.example.com");

        Assert.Equal(SettingsUpdateStatus.Saved, result.Status);
        Assert.True(secondCalled);
    }

    [Fact]
    public void ConcurrentSaves_DeliverEventsInRevisionOrder_NeverGoingBackwards()
    {
        var seen = new System.Collections.Concurrent.ConcurrentQueue<long>();
        _store.SettingsChanged += (_, e) => seen.Enqueue(e.Configuration.Revision);

        System.Threading.Tasks.Parallel.For(0, 40, _ => _store.Update(null, c => c.Language = "en"));

        var revisions = seen.ToArray();
        for (var i = 1; i < revisions.Length; i++)
        {
            Assert.True(revisions[i] > revisions[i - 1], "events went backwards: " + string.Join(",", revisions));
        }

        Assert.Equal(40, revisions[^1]);
    }

    [Fact]
    public void AStaleRevision_IsNeverDeliveredAfterANewerOne()
    {
        var seen = new System.Collections.Generic.List<long>();
        _store.SettingsChanged += (_, e) => seen.Add(e.Configuration.Revision);

        _store.RaiseSettingsChanged(new PluginConfiguration { Revision = 2 });
        _store.RaiseSettingsChanged(new PluginConfiguration { Revision = 1 });
        _store.RaiseSettingsChanged(new PluginConfiguration { Revision = 3 });

        Assert.Equal([2, 3], seen);
    }

    [Fact]
    public void BackupFromANewerVersion_WithUnknownEnumNames_IsStillRestored()
    {
        Directory.CreateDirectory(_paths.Root);
        File.WriteAllText(
            _paths.SettingsBackup,
            "{\"revision\": 4, \"proxy\": {\"mode\": \"Mtproto\", \"url\": \"\"}, "
            + "\"destinations\": [{\"id\": \"abcd1234\", \"chatId\": 42, \"kind\": \"Bogus\", \"preset\": \"Playback\"}]}");

        Assert.True(_store.RestoreBackupIfWiped());

        Assert.Equal(ProxyMode.System, _host.Configuration.Proxy.Mode);
        var d = Assert.Single(_host.Configuration.Destinations);
        Assert.Equal(PresetKind.Custom, d.Preset);
        Assert.Equal(ChatKind.Private, d.Kind);
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
    }
}
```

Create `tests/Jellyfin.Plugin.TgJelly.Tests/PluginConfigurationHostTests.cs`:

```csharp
using System.IO;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests;

public class PluginConfigurationHostTests
{
    [Fact]
    public void SaveFromStore_KeepsRevision_WritesXml_AndRaisesNoOutsideEvent()
    {
        using var dir = new TempDir();
        var plugin = TestPlugin.Create(dir.Path);
        var outside = 0;
        plugin.ExternalConfigurationSaved += (_, _) => outside++;

        plugin.SaveFromStore(new PluginConfiguration { Revision = 5, PublicUrl = "https://jf.example.com" });

        Assert.Equal(5, plugin.Configuration.Revision);
        Assert.Equal(0, outside);
        var xml = File.ReadAllText(Path.Combine(dir.Path, "plugins", "configurations", "Jellyfin.Plugin.TgJelly.xml"));
        Assert.Contains("<PublicUrl>https://jf.example.com</PublicUrl>", xml, System.StringComparison.Ordinal);
    }

    [Fact]
    public void OutsideSave_BumpsRevisionAboveBoth_AndRaisesEventOnce()
    {
        using var dir = new TempDir();
        var plugin = TestPlugin.Create(dir.Path);
        plugin.SaveFromStore(new PluginConfiguration { Revision = 5 });
        PluginConfiguration? seen = null;
        var outside = 0;
        plugin.ExternalConfigurationSaved += (_, e) =>
        {
            outside++;
            seen = e.Configuration;
        };

        plugin.UpdateConfiguration(new PluginConfiguration { Revision = 2, PublicUrl = "https://stale.example.com" });

        Assert.Equal(6, plugin.Configuration.Revision);
        Assert.Equal(1, outside);
        Assert.Equal(6, seen!.Revision);
    }

    [Fact]
    public void OutsideSave_WithNullSections_IsNormalized()
    {
        using var dir = new TempDir();
        var plugin = TestPlugin.Create(dir.Path);

        plugin.UpdateConfiguration(new PluginConfiguration { Batching = null!, Destinations = null! });

        Assert.NotNull(plugin.Configuration.Batching);
        Assert.Empty(plugin.Configuration.Destinations);
    }

    [Fact]
    public void OutsideAndStoreSaves_NeverShareARevision()
    {
        using var dir = new TempDir();
        var plugin = TestPlugin.Create(dir.Path);
        var paths = new Jellyfin.Plugin.TgJelly.State.DataPaths(dir.Path);
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var state = new Jellyfin.Plugin.TgJelly.State.StateStore(paths, time, Microsoft.Extensions.Logging.Abstractions.NullLogger<Jellyfin.Plugin.TgJelly.State.StateStore>.Instance);
        state.Load();
        using var store = new SettingsStore(plugin, paths, state, time, Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsStore>.Instance);
        var revisions = new System.Collections.Concurrent.ConcurrentBag<long>();
        plugin.ExternalConfigurationSaved += (_, e) => revisions.Add(e.Configuration.Revision);

        System.Threading.Tasks.Parallel.For(0, 400, i =>
        {
            if (i % 2 == 0)
            {
                revisions.Add(store.Update(null, c => c.Language = "en").Configuration.Revision);
            }
            else
            {
                plugin.UpdateConfiguration(new PluginConfiguration());
            }
        });

        Assert.Equal(400, revisions.Distinct().Count());
        Assert.Equal(400, plugin.Configuration.Revision);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet test --project tests/Jellyfin.Plugin.TgJelly.Tests -c Release --filter-class '*.SettingsStoreTests'`

Expected: FAIL — build errors `CS0246: The type or namespace name 'IConfigurationHost' could not be found` and `CS0246: The type or namespace name 'SettingsStore' could not be found`.

- [ ] **Step 3: Write the implementation**

Create `src/Jellyfin.Plugin.TgJelly/Configuration/IConfigurationHost.cs`:

```csharp
using System;

namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// The place Jellyfin keeps TgJelly's settings: implemented by <see cref="Plugin"/>, faked in tests.
/// </summary>
public interface IConfigurationHost
{
    /// <summary>
    /// Raised when the settings were saved by someone other than <see cref="SettingsStore"/>,
    /// e.g. a POST to Jellyfin's generic plugin configuration endpoint (spec §10.4).
    /// </summary>
    event EventHandler<ConfigurationSavedEventArgs>? ExternalConfigurationSaved;

    /// <summary>
    /// Gets the live settings object. Callers must not modify it.
    /// </summary>
    PluginConfiguration Configuration { get; }

    /// <summary>
    /// Runs <paramref name="action"/> while holding the host's private save lock. <see cref="SettingsStore"/> saves
    /// through it, and outside saves take the same lock, so two saves never get the same revision (spec §10.4).
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="action">The work to do under the lock.</param>
    /// <returns>The result of <paramref name="action"/>.</returns>
    T RunExclusive<T>(Func<T> action);

    /// <summary>
    /// Saves settings on behalf of <see cref="SettingsStore"/>. Does not raise <see cref="ExternalConfigurationSaved"/>.
    /// </summary>
    /// <param name="configuration">The settings to save; becomes the live object.</param>
    void SaveFromStore(PluginConfiguration configuration);
}
```

Create `src/Jellyfin.Plugin.TgJelly/Configuration/ConfigurationSavedEventArgs.cs`:

```csharp
using System;

namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// Event data for <see cref="IConfigurationHost.ExternalConfigurationSaved"/>.
/// </summary>
public sealed class ConfigurationSavedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationSavedEventArgs"/> class.
    /// </summary>
    /// <param name="configuration">The settings that were saved.</param>
    public ConfigurationSavedEventArgs(PluginConfiguration configuration)
    {
        Configuration = configuration;
    }

    /// <summary>Gets the settings that were saved.</summary>
    public PluginConfiguration Configuration { get; }
}
```

Create `src/Jellyfin.Plugin.TgJelly/Configuration/SettingsChangedEventArgs.cs`:

```csharp
using System;

namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// Event data for <see cref="SettingsStore.SettingsChanged"/>.
/// </summary>
public sealed class SettingsChangedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsChangedEventArgs"/> class.
    /// </summary>
    /// <param name="configuration">A copy of the new settings.</param>
    public SettingsChangedEventArgs(PluginConfiguration configuration)
    {
        Configuration = configuration;
    }

    /// <summary>Gets a copy of the new settings.</summary>
    public PluginConfiguration Configuration { get; }
}
```

Create `src/Jellyfin.Plugin.TgJelly/Configuration/SettingsUpdateStatus.cs`:

```csharp
namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// The outcome of <see cref="SettingsStore.Update"/>.
/// </summary>
public enum SettingsUpdateStatus
{
    /// <summary>The change was validated and saved.</summary>
    Saved,

    /// <summary>The caller's expected revision is stale; nothing was saved (HTTP 409).</summary>
    Conflict,

    /// <summary>The change failed validation; nothing was saved (HTTP 400).</summary>
    Invalid,
}
```

Create `src/Jellyfin.Plugin.TgJelly/Configuration/SettingsUpdateResult.cs`:

```csharp
using System.Collections.Generic;

namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// The result of <see cref="SettingsStore.Update"/>.
/// </summary>
/// <param name="Status">What happened.</param>
/// <param name="Configuration">A copy of the settings now in effect (the saved ones, or the unchanged current ones).</param>
/// <param name="Errors">Validation errors when <paramref name="Status"/> is <see cref="SettingsUpdateStatus.Invalid"/>.</param>
public sealed record SettingsUpdateResult(
    SettingsUpdateStatus Status,
    PluginConfiguration Configuration,
    IReadOnlyList<SettingsError> Errors);
```

Create `src/Jellyfin.Plugin.TgJelly/Configuration/SettingsStore.cs`:

```csharp
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.TgJelly.State;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// The only way TgJelly changes its settings (spec §10.4): under the host's save lock, clone, change, validate, bump the
/// revision, save, back up to settings.bak.json, then raise <see cref="SettingsChanged"/> once.
/// </summary>
public sealed class SettingsStore : IDisposable
{
    private readonly IConfigurationHost _host;
    private readonly DataPaths _paths;
    private readonly StateStore _state;
    private readonly TimeProvider _time;
    private readonly ILogger<SettingsStore> _logger;
    private readonly Lock _notifyGate = new();
    private long _lastNotifiedRevision = long.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsStore"/> class.
    /// </summary>
    /// <param name="host">Where Jellyfin keeps the settings.</param>
    /// <param name="paths">TgJelly's data paths.</param>
    /// <param name="state">The state store, for the "backup restored" notice.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public SettingsStore(IConfigurationHost host, DataPaths paths, StateStore state, TimeProvider time, ILogger<SettingsStore> logger)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _paths = paths;
        _state = state;
        _time = time;
        _logger = logger;
        _host.ExternalConfigurationSaved += OnExternalConfigurationSaved;
    }

    /// <summary>
    /// Raised after a save with a copy of the new settings. Deliveries never go backwards: a revision older than
    /// one already delivered is skipped, so handlers always end on the newest settings. A throwing handler is
    /// logged and does not affect other handlers or the save.
    /// </summary>
    public event EventHandler<SettingsChangedEventArgs>? SettingsChanged;

    /// <summary>
    /// Gets a copy of the current settings.
    /// </summary>
    public PluginConfiguration Current => _host.RunExclusive(() => Clone(_host.Configuration));

    /// <summary>
    /// Applies a change to a copy of the settings and saves it.
    /// </summary>
    /// <param name="expectedRevision">The revision the caller based its change on, or <c>null</c> to skip the check.</param>
    /// <param name="change">The change, applied to a copy.</param>
    /// <returns>The outcome.</returns>
    public SettingsUpdateResult Update(long? expectedRevision, Action<PluginConfiguration> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        var result = _host.RunExclusive(() =>
        {
            var current = _host.Configuration;
            if (expectedRevision.HasValue && expectedRevision.Value != current.Revision)
            {
                return new SettingsUpdateResult(SettingsUpdateStatus.Conflict, Clone(current), []);
            }

            var next = Clone(current);
            change(next);
            var errors = SettingsValidator.Validate(next);
            if (errors.Count > 0)
            {
                return new SettingsUpdateResult(SettingsUpdateStatus.Invalid, Clone(current), errors);
            }

            next.Revision = current.Revision + 1;
            _host.SaveFromStore(next);
            TryWriteBackup(next);
            return new SettingsUpdateResult(SettingsUpdateStatus.Saved, Clone(next), []);
        });

        if (result.Status == SettingsUpdateStatus.Saved)
        {
            RaiseSettingsChanged(Clone(result.Configuration));
        }

        return result;
    }

    /// <summary>
    /// Restores settings.bak.json when Jellyfin has replaced the settings with defaults after a load error (F9).
    /// Requires the state store to be loaded.
    /// </summary>
    /// <returns><c>true</c> when the backup was restored.</returns>
    public bool RestoreBackupIfWiped()
    {
        var restored = _host.RunExclusive(() =>
        {
            var current = _host.Configuration;
            if (current.Revision != 0 || current.Destinations.Length != 0 || !File.Exists(_paths.SettingsBackup))
            {
                return null;
            }

            PluginConfiguration? backup;
            try
            {
                backup = JsonSerializer.Deserialize<PluginConfiguration>(File.ReadAllText(_paths.SettingsBackup), JsonDefaults.Options);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "TgJelly settings backup {Path} could not be read", _paths.SettingsBackup);
                return null;
            }

            if (backup is null || backup.Revision <= 0)
            {
                return null;
            }

            backup.Normalize();
            _host.SaveFromStore(backup);
            return Clone(backup);
        });

        if (restored is null)
        {
            return false;
        }

        _state.Update(s => s.Status.BackupRestoredAt = _time.GetUtcNow());
        _logger.LogWarning("TgJelly settings were reset by Jellyfin; restored revision {Revision} from backup", restored.Revision);
        RaiseSettingsChanged(restored);
        return true;
    }

    /// <inheritdoc />
    public void Dispose() => _host.ExternalConfigurationSaved -= OnExternalConfigurationSaved;

    private static PluginConfiguration Clone(PluginConfiguration configuration)
        => JsonSerializer.Deserialize<PluginConfiguration>(JsonSerializer.Serialize(configuration, JsonDefaults.Options), JsonDefaults.Options)!;

    // The backup is a safety net: failing to write it must not turn a successful save into an error.
    private void TryWriteBackup(PluginConfiguration configuration)
    {
        try
        {
            AtomicFile.WriteAllText(_paths.SettingsBackup, JsonSerializer.Serialize(configuration, JsonDefaults.Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "TgJelly could not write its settings backup {Path}", _paths.SettingsBackup);
        }
    }

    /// <summary>
    /// Delivers <see cref="SettingsChanged"/>, skipping revisions older than the last one delivered.
    /// </summary>
    /// <param name="saved">A copy of the saved settings.</param>
    internal void RaiseSettingsChanged(PluginConfiguration saved)
    {
        lock (_notifyGate)
        {
            if (saved.Revision <= _lastNotifiedRevision)
            {
                return;
            }

            _lastNotifiedRevision = saved.Revision;
            var handlers = SettingsChanged;
            if (handlers is null)
            {
                return;
            }

            var args = new SettingsChangedEventArgs(saved);
            foreach (var handler in handlers.GetInvocationList())
            {
                try
                {
                    ((EventHandler<SettingsChangedEventArgs>)handler)(this, args);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "A TgJelly SettingsChanged handler failed");
                }
            }
        }
    }

    private void OnExternalConfigurationSaved(object? sender, ConfigurationSavedEventArgs e)
    {
        try
        {
            // Back up and publish the live object: it is what Jellyfin now has, whatever the event carried.
            var saved = _host.RunExclusive(() =>
            {
                var live = Clone(_host.Configuration);
                TryWriteBackup(live);
                return live;
            });

            RaiseSettingsChanged(saved);
        }
        catch (Exception ex)
        {
            // Never let our handler break Jellyfin's own save.
            _logger.LogError(ex, "TgJelly failed to process an outside settings save");
        }
    }
}
```

Replace the whole file `src/Jellyfin.Plugin.TgJelly/Plugin.cs` with:

```csharp
using System;
using System.Threading;
using Jellyfin.Plugin.TgJelly.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.TgJelly;

/// <summary>
/// The TgJelly plugin entry point.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IConfigurationHost
{
    /// <summary>
    /// The plugin name. Fixed forever (spec §5.1).
    /// </summary>
    public const string PluginName = "TgJelly";

    /// <summary>
    /// The plugin GUID. Fixed forever (spec §5.1).
    /// </summary>
    public static readonly Guid PluginId = new("e547b901-0dc4-42ae-8999-3871dd141270");

    // True only while SettingsStore is saving, so outside saves can be told apart (spec §10.4).
    private static readonly AsyncLocal<bool> SavingFromStore = new();

    private readonly Lock _saveGate = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public event EventHandler<ConfigurationSavedEventArgs>? ExternalConfigurationSaved;

    /// <summary>
    /// Gets the current plugin instance. Set by Jellyfin when it creates the plugin.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => PluginName;

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public override string Description => "Telegram notifications for Jellyfin.";

    /// <inheritdoc />
    public T RunExclusive<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_saveGate)
        {
            return action();
        }
    }

    /// <inheritdoc />
    public void SaveFromStore(PluginConfiguration configuration)
    {
        SavingFromStore.Value = true;
        try
        {
            UpdateConfiguration(configuration);
        }
        finally
        {
            SavingFromStore.Value = false;
        }
    }

    /// <inheritdoc />
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (SavingFromStore.Value)
        {
            base.UpdateConfiguration(configuration);
            return;
        }

        // An outside save (e.g. Jellyfin's generic configuration endpoint, which replaces the whole object):
        // serialise it with SettingsStore's saves, fill nulls with defaults, and keep revisions increasing.
        var incoming = (PluginConfiguration)configuration;
        lock (_saveGate)
        {
            incoming.Normalize();
            incoming.Revision = Math.Max(Configuration.Revision, incoming.Revision) + 1;
            base.UpdateConfiguration(incoming);
        }

        ExternalConfigurationSaved?.Invoke(this, new ConfigurationSavedEventArgs(incoming));
    }
}
```

- [ ] **Step 4: Build and run all tests**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet build Jellyfin.Plugin.TgJelly.slnx -c Release && dotnet test --solution Jellyfin.Plugin.TgJelly.slnx -c Release --no-build`

Expected: PASS — `0 Warning(s)`, `total: 172`, `failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add src/Jellyfin.Plugin.TgJelly/Configuration src/Jellyfin.Plugin.TgJelly/Plugin.cs tests/Jellyfin.Plugin.TgJelly.Tests/Fakes tests/Jellyfin.Plugin.TgJelly.Tests/Configuration/SettingsStoreTests.cs tests/Jellyfin.Plugin.TgJelly.Tests/PluginConfigurationHostTests.cs
git commit -m "feat: SettingsStore with revisions, backup restore and outside-save detection"
```

---

### Task 8: Wiring, Status API and the dashboard page

Registers everything with Jellyfin's container, adds the persistence loop, `GET /TgJelly/Status`, and the dashboard page shell. Hosted services stop in reverse registration order, so the gate is registered first: `PersistenceService` flushes before `InstanceGate` releases `state.lock`.

**Files:**
- Create: `src/Jellyfin.Plugin.TgJelly/Hosting/PersistenceService.cs`, `src/Jellyfin.Plugin.TgJelly/PluginServiceRegistrator.cs`, `src/Jellyfin.Plugin.TgJelly/Configuration/DetachedConfigurationHost.cs`
- Create: `src/Jellyfin.Plugin.TgJelly/Api/StatusDto.cs`, `src/Jellyfin.Plugin.TgJelly/Api/TgJellyController.cs`
- Create: `src/Jellyfin.Plugin.TgJelly/Web/tgjelly.html`, `src/Jellyfin.Plugin.TgJelly/Web/tgjelly.js`
- Replace: `src/Jellyfin.Plugin.TgJelly/Plugin.cs`, `src/Jellyfin.Plugin.TgJelly/Jellyfin.Plugin.TgJelly.csproj`
- Test: `tests/Jellyfin.Plugin.TgJelly.Tests/WebPagesTests.cs`, `tests/Jellyfin.Plugin.TgJelly.Tests/Api/TgJellyControllerTests.cs`, `tests/Jellyfin.Plugin.TgJelly.Tests/Hosting/PersistenceServiceTests.cs`, `tests/Jellyfin.Plugin.TgJelly.Tests/PluginServiceRegistratorTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 2–7.
- Produces: `PluginServiceRegistrator : IPluginServiceRegistrator` registering `TimeProvider` (TryAdd), `DataPaths`, `CompatResult`, `SecretsStore`, `StateStore`, `IConfigurationHost` (→ `Plugin.Instance`, or a `DetachedConfigurationHost` if Jellyfin couldn't create the plugin, so resolving services can't stop the server; via `internal static ResolveConfigurationHost(Plugin?)`), `SettingsStore`, `InstanceGate` (singleton and hosted, first), `PersistenceService(InstanceGate, StateStore, SettingsStore, SecretsStore, TimeProvider, ILogger)` (hosted, second; retries a failed backup restore after a supervisor restart); `GET /TgJelly/Status → StatusDto { Version, BuildTarget, Compatible, CompatMessage, InstanceOwner, InstanceMessage, Revision, BackupRestoredAt, LoadWarnings }` (admin only, PascalCase JSON); dashboard pages `tgjelly` (HTML, main menu "Telegram", icon `send`) and `tgjelly.js`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Jellyfin.Plugin.TgJelly.Tests/WebPagesTests.cs`:

```csharp
using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests;

public class WebPagesTests
{
    [Fact]
    public void Pages_HtmlFirst_WithSpecNames()
    {
        using var dir = new TempDir();
        var pages = TestPlugin.Create(dir.Path).GetPages().ToList();

        Assert.Equal(["tgjelly", "tgjelly.js"], pages.Select(p => p.Name));
        Assert.True(pages[0].EnableInMainMenu);
        Assert.Equal("Telegram", pages[0].DisplayName);
        Assert.False(pages[1].EnableInMainMenu);
    }

    [Fact]
    public void EveryPage_IsAnEmbeddedResource()
    {
        using var dir = new TempDir();
        var resources = typeof(Plugin).Assembly.GetManifestResourceNames();

        foreach (var page in TestPlugin.Create(dir.Path).GetPages())
        {
            Assert.Contains(page.EmbeddedResourcePath, resources);
        }
    }

    [Fact]
    public void Html_MeetsTheWebClientsRules()
    {
        var html = ReadResource("Jellyfin.Plugin.TgJelly.Web.tgjelly.html");

        // viewContainer looks for this literal, with double quotes (F25).
        Assert.Contains("data-role=\"page\"", html, StringComparison.Ordinal);
        Assert.Contains("data-controller=\"__plugin/tgjelly.js\"", html, StringComparison.Ordinal);

        // translateHtml rewrites every ${...} in the HTML (F26); all JS lives in tgjelly.js.
        Assert.DoesNotContain("${", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Js_IsAnEsModuleWithDefaultExport_AndUsesApiClientUrls()
    {
        var js = ReadResource("Jellyfin.Plugin.TgJelly.Web.tgjelly.js");

        Assert.Contains("export default function", js, StringComparison.Ordinal);
        Assert.Contains("ApiClient.getUrl('TgJelly/", js, StringComparison.Ordinal);
        Assert.DoesNotContain("X-Emby-Token", js, StringComparison.Ordinal);
        Assert.DoesNotContain("api_key", js, StringComparison.Ordinal);
    }

    private static string ReadResource(string name)
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
```

Create `tests/Jellyfin.Plugin.TgJelly.Tests/Api/TgJellyControllerTests.cs`. The second test is Review Focus item 2:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Api;
using Jellyfin.Plugin.TgJelly.Compat;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Api;

public class TgJellyControllerTests
{
    [Fact]
    public async Task Status_ReportsCompat_Instance_AndRevision()
    {
        using var dir = new TempDir();
        var paths = new DataPaths(dir.Path);
        var time = new FakeTimeProvider();
        var state = new StateStore(paths, time, NullLogger<StateStore>.Instance);
        state.Load();
        var host = new FakeConfigurationHost();
        using var settings = new SettingsStore(host, paths, state, time, NullLogger<SettingsStore>.Instance);
        settings.Update(null, c => c.PublicUrl = "https://jf.example.com");
        using var gate = new InstanceGate(paths, time, NullLogger<InstanceGate>.Instance);
        await gate.StartAsync(CancellationToken.None);
        await gate.WaitForOwnershipAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var compat = CompatGuard.Evaluate(new Version(12, 2, 0, 0), 10, "10.11");

        var status = new TgJellyController(compat, gate, settings, state, new SecretsStore(paths, time, NullLogger<SecretsStore>.Instance)).GetStatus().Value!;

        Assert.Equal("10.11", status.BuildTarget);
        Assert.False(status.Compatible);
        Assert.Equal(compat.Message, status.CompatMessage);
        Assert.True(status.InstanceOwner);
        Assert.Null(status.InstanceMessage);
        Assert.Equal(1, status.Revision);
        Assert.Empty(status.LoadWarnings);

        await gate.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Status_Answers_BeforeOwnershipAndBeforeStateIsLoaded()
    {
        // The page may be opened while a previous instance still holds state.lock.
        using var dir = new TempDir();
        var paths = new DataPaths(dir.Path);
        var time = new FakeTimeProvider();
        var state = new StateStore(paths, time, NullLogger<StateStore>.Instance);
        using var settings = new SettingsStore(new FakeConfigurationHost(), paths, state, time, NullLogger<SettingsStore>.Instance);
        using var gate = new InstanceGate(paths, time, NullLogger<InstanceGate>.Instance);
        var compat = CompatGuard.Evaluate(new Version(BuildInfo.TargetJellyfinMajor, 0, 0, 0));

        var status = new TgJellyController(compat, gate, settings, state, new SecretsStore(paths, time, NullLogger<SecretsStore>.Instance)).GetStatus().Value!;

        Assert.True(status.Compatible);
        Assert.False(status.InstanceOwner);
        Assert.Equal(InstanceGate.WaitingForPreviousInstance, status.InstanceMessage);
        Assert.Equal(0, status.Revision);
    }

    [Fact]
    public void Status_ShowsAQuarantinedSecretsFile()
    {
        using var dir = new TempDir();
        var paths = new DataPaths(dir.Path);
        var time = new FakeTimeProvider();
        var state = new StateStore(paths, time, NullLogger<StateStore>.Instance);
        state.Load();
        using var settings = new SettingsStore(new FakeConfigurationHost(), paths, state, time, NullLogger<SettingsStore>.Instance);
        using var gate = new InstanceGate(paths, time, NullLogger<InstanceGate>.Instance);
        System.IO.Directory.CreateDirectory(paths.Root);
        System.IO.File.WriteAllText(paths.Secrets, "{oops");
        var secrets = new SecretsStore(paths, time, NullLogger<SecretsStore>.Instance);
        _ = secrets.Load();

        var status = new TgJellyController(CompatGuard.Evaluate(new Version(BuildInfo.TargetJellyfinMajor, 0, 0, 0)), gate, settings, state, secrets)
            .GetStatus().Value!;

        var warning = Assert.Single(status.LoadWarnings);
        Assert.StartsWith("secrets.json was unreadable", warning, StringComparison.Ordinal);
    }
}
```

Create `tests/Jellyfin.Plugin.TgJelly.Tests/Hosting/PersistenceServiceTests.cs`. The third test is Review Focus item 1:

```csharp
using System;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Hosting;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests.Hosting;

public sealed class PersistenceServiceTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly DataPaths _paths;
    private readonly FakeTimeProvider _time = new();
    private readonly FakeConfigurationHost _host = new();
    private readonly InstanceGate _gate;
    private readonly StateStore _state;
    private readonly SettingsStore _settings;
    private readonly SecretsStore _secrets;

    public PersistenceServiceTests()
    {
        _paths = new DataPaths(_dir.Path);
        _gate = new InstanceGate(_paths, _time, NullLogger<InstanceGate>.Instance);
        _state = new StateStore(_paths, _time, NullLogger<StateStore>.Instance);
        _settings = new SettingsStore(_host, _paths, _state, _time, NullLogger<SettingsStore>.Instance);
        _secrets = new SecretsStore(_paths, _time, NullLogger<SecretsStore>.Instance);
    }

    [Fact]
    public async Task Start_LoadsState_RestoresWipedSettings_FlushesEverySecond_AndOnStop()
    {
        Directory.CreateDirectory(_paths.Root);
        var backup = new PluginConfiguration { Revision = 3, PublicUrl = "https://jf.example.com" };
        await File.WriteAllTextAsync(_paths.SettingsBackup, JsonSerializer.Serialize(backup, JsonDefaults.Options), TestContext.Current.CancellationToken);
        using var service = new PersistenceService(_gate, _state, _settings, _secrets, _time, NullLogger<PersistenceService>.Instance);

        await _gate.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);
        // BackupRestoredAt is set after the restored settings are saved, so waiting for it covers both
        // (on .NET 10 the service runs on the thread pool, so IsLoaded alone would race).
        await Eventually.TrueAsync(
            () => _state.IsLoaded && _state.Read(s => s.Status.BackupRestoredAt) is not null,
            "state loaded and backup restored after ownership");

        Assert.Equal(3, _host.Configuration.Revision);

        await Eventually.AdvanceUntilAsync(_time, TimeSpan.FromSeconds(1), () => File.Exists(_paths.State), "state.json written by the 1 s loop");

        File.Delete(_paths.State);
        _state.Update(s => s.Status.LoadWarnings.Add("x"));
        await service.StopAsync(CancellationToken.None);
        Assert.True(File.Exists(_paths.State));

        await _gate.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WithoutOwnership_NothingIsLoadedOrWritten()
    {
        using var other = InstanceLock.TryAcquire(_paths.Lock);
        using var service = new PersistenceService(_gate, _state, _settings, _secrets, _time, NullLogger<PersistenceService>.Instance);

        await _gate.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(3));
        await Eventually.SettleAsync();

        Assert.False(_state.IsLoaded);
        await service.StopAsync(CancellationToken.None);
        Assert.False(File.Exists(_paths.State));
        await _gate.StopAsync(CancellationToken.None);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task UnwritableDataFolder_DoesNotStopTheService_AndStateIsWrittenWhenItRecovers()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.UserName == "root", "needs Unix permissions as a normal user");
        using var service = new PersistenceService(_gate, _state, _settings, _secrets, _time, NullLogger<PersistenceService>.Instance);
        await _gate.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);
        await Eventually.TrueAsync(() => _state.IsLoaded, "state loaded");

        File.SetUnixFileMode(_paths.Root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            _state.Update(s => s.Status.LoadWarnings.Add("pending"));
            _time.Advance(TimeSpan.FromSeconds(1)); // flush fails; the supervisor logs and restarts the loop after 1 s
            await Eventually.SettleAsync();
            Assert.False(File.Exists(_paths.State));
        }
        finally
        {
            File.SetUnixFileMode(_paths.Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        await Eventually.AdvanceUntilAsync(_time, TimeSpan.FromSeconds(1), () => File.Exists(_paths.State), "state.json written once the folder recovered");
        await service.StopAsync(CancellationToken.None);
        await _gate.StopAsync(CancellationToken.None);
    }

    public void Dispose()
    {
        _settings.Dispose();
        _gate.Dispose();
        _dir.Dispose();
    }
}
```

Create `tests/Jellyfin.Plugin.TgJelly.Tests/PluginServiceRegistratorTests.cs`:

```csharp
using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.TgJelly.Compat;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Hosting;
using Jellyfin.Plugin.TgJelly.State;
using Jellyfin.Plugin.TgJelly.Tests.Fakes;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.TgJelly.Tests;

public class PluginServiceRegistratorTests
{
    [Fact]
    public void AllServices_Resolve_AndGateIsTheHostedInstance_RegisteredFirst()
    {
        using var dir = new TempDir();
        _ = TestPlugin.Create(dir.Path); // Jellyfin creates the plugin before resolving services.
        var paths = Substitute.For<IApplicationPaths>();
        paths.PluginConfigurationsPath.Returns(Path.Combine(dir.Path, "plugins", "configurations"));
        var host = Substitute.For<IServerApplicationHost>();
        host.ApplicationVersion.Returns(new Version(BuildInfo.TargetJellyfinMajor, 0, 0, 0));

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(paths);
        services.AddSingleton(host);
        new PluginServiceRegistrator().RegisterServices(services, host);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.NotNull(provider.GetRequiredService<SettingsStore>());
        Assert.NotNull(provider.GetRequiredService<SecretsStore>());
        Assert.NotNull(provider.GetRequiredService<StateStore>());
        Assert.True(provider.GetRequiredService<CompatResult>().IsCompatible);
        Assert.Equal(Path.Combine(dir.Path, "plugins", "configurations", "TgJelly"), provider.GetRequiredService<DataPaths>().Root);

        var hosted = provider.GetServices<IHostedService>().ToList();
        Assert.Same(provider.GetRequiredService<InstanceGate>(), hosted[0]);
        Assert.IsType<PersistenceService>(hosted[1]);
    }

    [Fact]
    public void WithoutAPluginInstance_ADetachedHostIsUsed_SoStartupCannotThrow()
    {
        var host = PluginServiceRegistrator.ResolveConfigurationHost(null);

        Assert.IsType<DetachedConfigurationHost>(host);
        Assert.Equal(0, host.Configuration.Revision);
        Assert.Throws<InvalidOperationException>(() => host.SaveFromStore(new PluginConfiguration()));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet test --project tests/Jellyfin.Plugin.TgJelly.Tests -c Release --filter-class '*.WebPagesTests'`

Expected: FAIL — build error `CS0234: The type or namespace name 'Api' does not exist in the namespace 'Jellyfin.Plugin.TgJelly'` (the other new types are missing too).

- [ ] **Step 3: Write the implementation**

Create `src/Jellyfin.Plugin.TgJelly/Hosting/PersistenceService.cs`:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TgJelly.Hosting;

/// <summary>
/// Waits until this instance owns the data folder, loads the state files and secrets.json, restores a wiped settings
/// backup, then writes due state once per second. Flushes everything on stop (spec §10.4, §12).
/// </summary>
public sealed class PersistenceService : BackgroundService
{
    private readonly InstanceGate _gate;
    private readonly StateStore _state;
    private readonly SettingsStore _settings;
    private readonly SecretsStore _secrets;
    private readonly TimeProvider _time;
    private readonly ILogger<PersistenceService> _logger;
    private bool _restoreChecked;

    /// <summary>
    /// Initializes a new instance of the <see cref="PersistenceService"/> class.
    /// </summary>
    /// <param name="gate">The instance gate.</param>
    /// <param name="state">The state store.</param>
    /// <param name="settings">The settings store.</param>
    /// <param name="secrets">The secrets store.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public PersistenceService(
        InstanceGate gate,
        StateStore state,
        SettingsStore settings,
        SecretsStore secrets,
        TimeProvider time,
        ILogger<PersistenceService> logger)
    {
        _gate = gate;
        _state = state;
        _settings = settings;
        _secrets = secrets;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TgJelly persistence loop did not stop cleanly");
        }

        try
        {
            _state.FlushAll();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TgJelly could not write its state on shutdown");
        }
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Supervisor.RunAsync("persistence", RunAsync, _time, _logger, stoppingToken);

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        await _gate.WaitForOwnershipAsync(stoppingToken).ConfigureAwait(false);
        if (!_state.IsLoaded)
        {
            _state.Load();
        }

        // Tracked separately from IsLoaded, so a failed restore is retried after the supervisor restarts the loop.
        if (!_restoreChecked)
        {
            _settings.RestoreBackupIfWiped();
            _restoreChecked = true;
        }

        // Reads secrets.json once now, so an unreadable file shows up in Status right away.
        _ = _secrets.Load();

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            _state.FlushDue();
        }
    }
}
```

Create `src/Jellyfin.Plugin.TgJelly/Configuration/DetachedConfigurationHost.cs`:

```csharp
using System;
using System.Threading;

namespace Jellyfin.Plugin.TgJelly.Configuration;

/// <summary>
/// Stands in for <see cref="Plugin"/> when Jellyfin could not create the plugin instance (its constructor threw and
/// the plugin is marked Malfunctioned) while TgJelly's services are still registered. Resolving services must then
/// not stop the whole server (F3), so reads return defaults and saves fail inside supervised code.
/// </summary>
public sealed class DetachedConfigurationHost : IConfigurationHost
{
    private readonly Lock _saveGate = new();

    /// <inheritdoc />
    public event EventHandler<ConfigurationSavedEventArgs>? ExternalConfigurationSaved
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public PluginConfiguration Configuration { get; } = new();

    /// <inheritdoc />
    public T RunExclusive<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_saveGate)
        {
            return action();
        }
    }

    /// <inheritdoc />
    public void SaveFromStore(PluginConfiguration configuration)
        => throw new InvalidOperationException("The TgJelly plugin instance is missing, so settings cannot be saved.");
}
```

Create `src/Jellyfin.Plugin.TgJelly/PluginServiceRegistrator.cs`:

```csharp
using System;
using Jellyfin.Plugin.TgJelly.Compat;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.Hosting;
using Jellyfin.Plugin.TgJelly.State;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jellyfin.Plugin.TgJelly;

/// <summary>
/// Registers TgJelly's services with Jellyfin's container (spec §5.3). Runs before the container is built,
/// so everything that needs other services is registered as a factory. Must never throw (F2, F3).
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.TryAddSingleton(TimeProvider.System);
        serviceCollection.AddSingleton(sp => new DataPaths(sp.GetRequiredService<IApplicationPaths>().PluginConfigurationsPath));
        serviceCollection.AddSingleton(sp => CompatGuard.Evaluate(sp.GetRequiredService<IServerApplicationHost>().ApplicationVersion));
        serviceCollection.AddSingleton<SecretsStore>();
        serviceCollection.AddSingleton<StateStore>();
        serviceCollection.AddSingleton(_ => ResolveConfigurationHost(Plugin.Instance));
        serviceCollection.AddSingleton<SettingsStore>();

        // Hosted services stop in reverse order: persistence flushes first, then the gate releases state.lock.
        serviceCollection.AddSingleton<InstanceGate>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<InstanceGate>());
        serviceCollection.AddHostedService<PersistenceService>();
    }

    /// <summary>
    /// Picks the settings host. If Jellyfin could not create the plugin instance, a detached host keeps service
    /// resolution from throwing at host start, which would stop the whole server (F3).
    /// </summary>
    /// <param name="instance">The plugin instance, if Jellyfin created one.</param>
    /// <returns>The host to use.</returns>
    internal static IConfigurationHost ResolveConfigurationHost(Plugin? instance)
        => (IConfigurationHost?)instance ?? new DetachedConfigurationHost();
}
```

Create `src/Jellyfin.Plugin.TgJelly/Api/StatusDto.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// Response of <c>GET /TgJelly/Status</c> (spec §11.2). Jellyfin serializes it in PascalCase.
/// Later plans add the Telegram connection state and counters.
/// </summary>
public sealed class StatusDto
{
    /// <summary>Gets the plugin version.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Gets the Jellyfin line this build targets ("10.11" or "12").</summary>
    public string BuildTarget { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether this build matches the server (CompatGuard).</summary>
    public bool Compatible { get; init; }

    /// <summary>Gets the CompatGuard banner text when incompatible.</summary>
    public string? CompatMessage { get; init; }

    /// <summary>Gets a value indicating whether this instance owns the data folder.</summary>
    public bool InstanceOwner { get; init; }

    /// <summary>Gets the instance gate's waiting message, if any.</summary>
    public string? InstanceMessage { get; init; }

    /// <summary>Gets the current settings revision.</summary>
    public long Revision { get; init; }

    /// <summary>Gets when a wiped config was restored from backup, if ever.</summary>
    public DateTimeOffset? BackupRestoredAt { get; init; }

    /// <summary>Gets warnings from loading the state files.</summary>
    public IReadOnlyList<string> LoadWarnings { get; init; } = [];
}
```

Create `src/Jellyfin.Plugin.TgJelly/Api/TgJellyController.cs`:

```csharp
using System.Net.Mime;
using Jellyfin.Plugin.TgJelly.Compat;
using Jellyfin.Plugin.TgJelly.Configuration;
using Jellyfin.Plugin.TgJelly.State;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TgJelly.Api;

/// <summary>
/// Admin-only REST API for the dashboard page (spec §11.2). Later plans add endpoints.
/// </summary>
[ApiController]
[Route("TgJelly")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public class TgJellyController : ControllerBase
{
    private readonly CompatResult _compat;
    private readonly InstanceGate _gate;
    private readonly SettingsStore _settings;
    private readonly StateStore _state;
    private readonly SecretsStore _secrets;

    /// <summary>
    /// Initializes a new instance of the <see cref="TgJellyController"/> class.
    /// </summary>
    /// <param name="compat">The CompatGuard result.</param>
    /// <param name="gate">The instance gate.</param>
    /// <param name="settings">The settings store.</param>
    /// <param name="state">The state store.</param>
    /// <param name="secrets">The secrets store.</param>
    public TgJellyController(CompatResult compat, InstanceGate gate, SettingsStore settings, StateStore state, SecretsStore secrets)
    {
        _compat = compat;
        _gate = gate;
        _settings = settings;
        _state = state;
        _secrets = secrets;
    }

    /// <summary>
    /// Gets the plugin status shown at the top of the dashboard page.
    /// </summary>
    /// <returns>The status.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<StatusDto> GetStatus()
    {
        return new StatusDto
        {
            Version = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? string.Empty,
            BuildTarget = _compat.BuildLabel,
            Compatible = _compat.IsCompatible,
            CompatMessage = _compat.Message,
            InstanceOwner = _gate.IsOwner,
            InstanceMessage = _gate.WaitingMessage,
            Revision = _settings.Current.Revision,
            BackupRestoredAt = _state.Read(s => s.Status.BackupRestoredAt),
            LoadWarnings = [.. _state.Read(s => s.Status.LoadWarnings.ToArray()), .. _secrets.LoadWarning is { } w ? new[] { w } : []],
        };
    }
}
```

Create `src/Jellyfin.Plugin.TgJelly/Web/tgjelly.html`:

```html
<div data-role="page" id="tgjConfigPage" class="page type-interior pluginConfigurationPage" data-controller="__plugin/tgjelly.js">
    <div data-role="content">
        <div class="content-primary">
            <h2 class="sectionTitle">Telegram</h2>
            <div class="tgj-status verticalSection">
                <p class="tgj-status-compat" id="tgj-status-compat">Loading…</p>
                <p class="fieldDescription tgj-status-instance" id="tgj-status-instance"></p>
                <p class="fieldDescription tgj-status-version" id="tgj-status-version"></p>
            </div>
        </div>
    </div>
</div>
```

Create `src/Jellyfin.Plugin.TgJelly/Web/tgjelly.js`:

```javascript
// TgJelly dashboard page controller (spec §11.1). Loaded through data-controller="__plugin/tgjelly.js".
// Plan 1 shows the plugin status only; later plans add the setup UI.

const POLL_MS = 3000;

function render(view, status) {
    const compat = view.querySelector('#tgj-status-compat');
    compat.textContent = status.Compatible
        ? `TgJelly is running (build for Jellyfin ${status.BuildTarget}).`
        : status.CompatMessage;

    view.querySelector('#tgj-status-instance').textContent = status.InstanceMessage || '';
    view.querySelector('#tgj-status-version').textContent = `Version ${status.Version} · settings revision ${status.Revision}`;
}

export default function TgJellyPage(view) {
    let timer = null;

    const stop = () => {
        if (timer !== null) {
            clearInterval(timer);
            timer = null;
        }
    };

    const refresh = async () => {
        try {
            const status = await window.ApiClient.fetch({
                url: window.ApiClient.getUrl('TgJelly/Status'),
                type: 'GET',
                dataType: 'json',
                headers: { accept: 'application/json' }
            });
            render(view, status);
        } catch (err) {
            view.querySelector('#tgj-status-compat').textContent = 'Could not load the TgJelly status.';
            console.error('TgJelly status request failed', err);
        }
    };

    const start = () => {
        stop();
        refresh();
        timer = setInterval(() => {
            if (!view.isConnected || view.classList.contains('hide')) {
                stop();
                return;
            }
            if (!document.hidden) {
                refresh();
            }
        }, POLL_MS);
    };

    view.addEventListener('viewshow', start);
    view.addEventListener('viewbeforehide', stop);
    view.addEventListener('viewhide', stop);
    view.addEventListener('viewdestroy', stop);
}
```

Replace the whole file `src/Jellyfin.Plugin.TgJelly/Plugin.cs` with (adds `IHasWebPages`; the HTML page must be listed first):

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using Jellyfin.Plugin.TgJelly.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.TgJelly;

/// <summary>
/// The TgJelly plugin entry point.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IConfigurationHost, IHasWebPages
{
    /// <summary>
    /// The plugin name. Fixed forever (spec §5.1).
    /// </summary>
    public const string PluginName = "TgJelly";

    /// <summary>
    /// The plugin GUID. Fixed forever (spec §5.1).
    /// </summary>
    public static readonly Guid PluginId = new("e547b901-0dc4-42ae-8999-3871dd141270");

    // True only while SettingsStore is saving, so outside saves can be told apart (spec §10.4).
    private static readonly AsyncLocal<bool> SavingFromStore = new();

    private readonly Lock _saveGate = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public event EventHandler<ConfigurationSavedEventArgs>? ExternalConfigurationSaved;

    /// <summary>
    /// Gets the current plugin instance. Set by Jellyfin when it creates the plugin.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => PluginName;

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public override string Description => "Telegram notifications for Jellyfin.";

    /// <summary>
    /// Lists the dashboard pages (spec §11.1). The HTML page must come first: the plugin's Settings button
    /// opens the first page, and a JS page there would crash the view.
    /// </summary>
    /// <returns>The page descriptors.</returns>
    public IEnumerable<PluginPageInfo> GetPages()
    {
        var resourcePrefix = typeof(Plugin).Namespace + ".Web.";
        return
        [
            new PluginPageInfo
            {
                Name = "tgjelly",
                DisplayName = "Telegram",
                EmbeddedResourcePath = resourcePrefix + "tgjelly.html",
                EnableInMainMenu = true,
                MenuIcon = "send",
            },
            new PluginPageInfo
            {
                Name = "tgjelly.js",
                EmbeddedResourcePath = resourcePrefix + "tgjelly.js",
            },
        ];
    }

    /// <inheritdoc />
    public T RunExclusive<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_saveGate)
        {
            return action();
        }
    }

    /// <inheritdoc />
    public void SaveFromStore(PluginConfiguration configuration)
    {
        SavingFromStore.Value = true;
        try
        {
            UpdateConfiguration(configuration);
        }
        finally
        {
            SavingFromStore.Value = false;
        }
    }

    /// <inheritdoc />
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (SavingFromStore.Value)
        {
            base.UpdateConfiguration(configuration);
            return;
        }

        // An outside save (e.g. Jellyfin's generic configuration endpoint, which replaces the whole object):
        // serialise it with SettingsStore's saves, fill nulls with defaults, and keep revisions increasing.
        var incoming = (PluginConfiguration)configuration;
        lock (_saveGate)
        {
            incoming.Normalize();
            incoming.Revision = Math.Max(Configuration.Revision, incoming.Revision) + 1;
            base.UpdateConfiguration(incoming);
        }

        ExternalConfigurationSaved?.Invoke(this, new ConfigurationSavedEventArgs(incoming));
    }
}
```

Replace the whole file `src/Jellyfin.Plugin.TgJelly/Jellyfin.Plugin.TgJelly.csproj` with (adds the embedded web files):

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFrameworks>net9.0;net10.0</TargetFrameworks>
    <RootNamespace>Jellyfin.Plugin.TgJelly</RootNamespace>
    <AssemblyName>Jellyfin.Plugin.TgJelly</AssemblyName>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Nullable>enable</Nullable>
    <AnalysisMode>AllEnabledByDefault</AnalysisMode>
    <!-- XmlSerializer-friendly config uses arrays (spec §10.1), like jellyfin-plugin-webhook. -->
    <NoWarn>$(NoWarn);CA1819</NoWarn>
  </PropertyGroup>

  <!-- Jellyfin 12 build: .NET 10, compiled against the lowest 12.x packages (spec §13.2). -->
  <PropertyGroup Condition="'$(TargetFramework)' == 'net10.0'">
    <DefineConstants>$(DefineConstants);JF12</DefineConstants>
  </PropertyGroup>

  <ItemGroup Condition="'$(TargetFramework)' == 'net9.0'">
    <PackageReference Include="Jellyfin.Controller" Version="10.11.0" ExcludeAssets="runtime" />
    <PackageReference Include="Jellyfin.Model" Version="10.11.0" ExcludeAssets="runtime" />
  </ItemGroup>

  <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'">
    <PackageReference Include="Jellyfin.Controller" Version="12.0.0" ExcludeAssets="runtime" />
    <PackageReference Include="Jellyfin.Model" Version="12.0.0" ExcludeAssets="runtime" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="SerilogAnalyzer" Version="0.15.0" PrivateAssets="All" />
    <PackageReference Include="StyleCop.Analyzers" Version="1.2.0-beta.556" PrivateAssets="All" />
    <PackageReference Include="SmartAnalyzers.MultithreadingAnalyzer" Version="1.1.31" PrivateAssets="All" />
  </ItemGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="Jellyfin.Plugin.TgJelly.Tests" />
  </ItemGroup>

  <ItemGroup>
    <EmbeddedResource Include="Web\tgjelly.html" />
    <EmbeddedResource Include="Web\tgjelly.js" />
  </ItemGroup>

</Project>
```

- [ ] **Step 4: Build and run all tests three times, then once on 2 CPUs**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet build Jellyfin.Plugin.TgJelly.slnx -c Release && ( set -e; for i in 1 2 3; do dotnet test --solution Jellyfin.Plugin.TgJelly.slnx -c Release --no-build; done; if command -v taskset >/dev/null; then taskset -c 0,1 dotnet test --solution Jellyfin.Plugin.TgJelly.slnx -c Release --no-build; fi )`

Expected: PASS — `0 Warning(s)`, three runs of `total: 196`, `failed: 0`, plus the 2-CPU run.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat: service wiring, persistence loop, Status API and dashboard page shell"
```

---

### Task 9: Packaging script

Builds one zip per track from the same commit, with exactly the whitelisted files, and extends the repository manifest (§13.3–§13.4). `package.py` uses only the Python standard library.

**Files:**
- Create: `build/package.py`
- Test: `build/test_package.py`

**Interfaces:**
- Consumes: the plugin project (`dotnet publish`).
- Produces: `python3 build/package.py --release X.Y [--changelog TEXT] [--output DIR] [--manifest-in FILE] [--source-url-base URL] [--dotnet EXE]` writing `DIR/tgjelly_1.X.Y.0.zip`, `DIR/tgjelly_2.X.Y.0.zip` and `DIR/manifest.json`. Module constants `GUID`, `NAME`, `ASSEMBLIES` (plan 2 appends `Telegram.Bot.dll`, plan 5 `Scriban.dll`), `TRACKS`, `IMAGE_URL`; functions `track_version`, `make_meta`, `stage`, `write_zip`, `verify_zip`, `md5`, `update_manifest`.

- [ ] **Step 1: Write the failing test**

Create `build/test_package.py`:

```python
"""Tests for build/package.py. Run: python3 -m unittest discover -s build -p 'test_*.py' -v"""

import json
import tempfile
import unittest
import zipfile
from pathlib import Path

import package


class TrackVersionTests(unittest.TestCase):
    def test_maps_release_to_both_tracks(self):
        self.assertEqual(package.track_version(1, "0.3"), "1.0.3.0")
        self.assertEqual(package.track_version(2, "1.12"), "2.1.12.0")

    def test_every_12_build_outranks_every_1011_build(self):
        newest_1011 = tuple(int(p) for p in package.track_version(1, "99.99").split("."))
        oldest_12 = tuple(int(p) for p in package.track_version(2, "0.0").split("."))
        self.assertLess(newest_1011, oldest_12)

    def test_rejects_malformed_release(self):
        for bad in ["1", "1.2.3", "v1.2", "1.x", ""]:
            with self.assertRaises(ValueError, msg=bad):
                package.track_version(1, bad)


class MetaTests(unittest.TestCase):
    def test_meta_matches_spec(self):
        meta = package.make_meta("2.0.1.0", "12.0.0.0", "notes", "2026-10-17T00:00:00Z")
        self.assertEqual(meta["guid"], "e547b901-0dc4-42ae-8999-3871dd141270")
        self.assertEqual(meta["name"], "TgJelly")
        self.assertEqual(meta["category"], "Administration")
        self.assertEqual(meta["targetAbi"], "12.0.0.0")
        self.assertEqual(meta["imagePath"], "")
        self.assertEqual(meta["assemblies"], package.ASSEMBLIES)


class ZipTests(unittest.TestCase):
    def _publish_dir(self, root: Path, extra=()):
        publish = root / "publish"
        publish.mkdir()
        for name in package.ASSEMBLIES + list(extra):
            (publish / name).write_bytes(b"dll")
        return publish

    def test_stage_and_zip_contain_exactly_the_whitelist(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            publish = self._publish_dir(root, extra=["System.Text.Json.dll", "Jellyfin.Plugin.TgJelly.pdb"])
            stage = root / "stage"
            package.stage(publish, stage, package.make_meta("1.0.0.0", "10.11.0.0", "", "t"))
            zip_path = root / "out.zip"
            package.write_zip(stage, zip_path)
            with zipfile.ZipFile(zip_path) as archive:
                self.assertEqual(set(archive.namelist()), package.expected_zip_names())
                meta = json.loads(archive.read("meta.json"))
            self.assertEqual(meta["version"], "1.0.0.0")

    def test_verify_zip_rejects_extra_files(self):
        with tempfile.TemporaryDirectory() as tmp:
            zip_path = Path(tmp) / "bad.zip"
            with zipfile.ZipFile(zip_path, "w") as archive:
                for name in package.expected_zip_names() | {"Microsoft.Extensions.Options.dll"}:
                    archive.writestr(name, "x")
            with self.assertRaises(ValueError):
                package.verify_zip(zip_path)

    def test_stage_fails_when_a_dll_is_missing(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "publish").mkdir()
            with self.assertRaises(FileNotFoundError):
                package.stage(root / "publish", root / "stage", package.make_meta("1.0.0.0", "10.11.0.0", "", "t"))


class ManifestTests(unittest.TestCase):
    def _version(self, version, abi):
        return {"version": version, "changelog": "", "targetAbi": abi, "sourceUrl": "u", "checksum": "c", "timestamp": "t"}

    def test_creates_entry_with_image_url(self):
        manifest = package.update_manifest([], [self._version("1.0.1.0", "10.11.0.0")])
        entry = manifest[0]
        self.assertEqual(entry["guid"], package.GUID)
        self.assertEqual(entry["imageUrl"], package.IMAGE_URL)
        self.assertEqual(entry["category"], "Administration")

    def test_keeps_older_versions_replaces_same_version_newest_first(self):
        old = package.update_manifest(
            [{"guid": "other-plugin", "versions": []}],
            [self._version("1.0.1.0", "10.11.0.0"), self._version("2.0.1.0", "12.0.0.0")],
        )
        updated = package.update_manifest(
            old,
            [self._version("1.0.2.0", "10.11.0.0"), self._version("2.0.2.0", "12.0.0.0"), self._version("2.0.1.0", "12.0.0.0")],
        )
        entry = next(p for p in updated if p["guid"] == package.GUID)
        self.assertEqual([v["version"] for v in entry["versions"]], ["2.0.2.0", "2.0.1.0", "1.0.2.0", "1.0.1.0"])
        self.assertEqual(len(updated), 2)


if __name__ == "__main__":
    unittest.main()
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `python3 -m unittest discover -s build -p 'test_*.py'`

Expected: FAIL — `ModuleNotFoundError: No module named 'package'`.

- [ ] **Step 3: Write the implementation**

Create `build/package.py`:

```python
#!/usr/bin/env python3
"""Build the TgJelly plugin zips and the plugin repository manifest (spec §13.3-§13.4).

One release tag vX.Y produces two builds from the same commit:
  track 1: net9.0,  targetAbi 10.11.0.0, version 1.X.Y.0  (Jellyfin 10.11)
  track 2: net10.0, targetAbi 12.0.0.0,  version 2.X.Y.0  (Jellyfin 12)
Every 12 build therefore outranks every 10.11 build, which Jellyfin needs because it only checks
targetAbi <= server version (spec F7).

Usage:
  python3 build/package.py --release 0.1 --changelog "First release" \
      [--output artifacts] [--manifest-in old-manifest.json] [--source-url-base URL]
"""

import argparse
import hashlib
import json
import shutil
import subprocess
import sys
import zipfile
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PROJECT = ROOT / "src" / "Jellyfin.Plugin.TgJelly" / "Jellyfin.Plugin.TgJelly.csproj"

GUID = "e547b901-0dc4-42ae-8999-3871dd141270"
NAME = "TgJelly"
OWNER = "MrSnowball-dev"
CATEGORY = "Administration"
OVERVIEW = "Telegram notifications for Jellyfin"
DESCRIPTION = (
    "A Telegram bot inside your Jellyfin server: new media, security and server notifications "
    "for the chats, groups and channels you choose, set up from the dashboard or the bot."
)
IMAGE_URL = "https://mrsnowball-dev.github.io/tgjelly_plugin/logo.png"
REPO_URL = "https://github.com/MrSnowball-dev/tgjelly_plugin"

# Exactly these DLLs ship, and meta.json lists them, so no stray framework DLL is ever loaded (spec §13.4, F5).
# Plan 2 adds Telegram.Bot.dll; plan 5 adds Scriban.dll.
ASSEMBLIES = ["Jellyfin.Plugin.TgJelly.dll"]

TRACKS = [
    {"track": 1, "tfm": "net9.0", "target_abi": "10.11.0.0"},
    {"track": 2, "tfm": "net10.0", "target_abi": "12.0.0.0"},
]


def track_version(track: int, release: str) -> str:
    """Map release "X.Y" to the four-part version of a track, e.g. (2, "0.3") -> "2.0.3.0"."""
    parts = release.split(".")
    if len(parts) != 2 or not all(p.isdigit() for p in parts):
        raise ValueError(f"release must look like X.Y, got {release!r}")
    return f"{track}.{int(parts[0])}.{int(parts[1])}.0"


def make_meta(version: str, target_abi: str, changelog: str, timestamp: str) -> dict:
    """The meta.json packaged in the zip. imagePath stays empty so the repository's imageUrl is used (F5)."""
    return {
        "guid": GUID,
        "name": NAME,
        "overview": OVERVIEW,
        "description": DESCRIPTION,
        "owner": OWNER,
        "category": CATEGORY,
        "version": version,
        "targetAbi": target_abi,
        "changelog": changelog,
        "timestamp": timestamp,
        "imagePath": "",
        "assemblies": list(ASSEMBLIES),
    }


def expected_zip_names() -> set:
    return set(ASSEMBLIES) | {"meta.json"}


def stage(publish_dir: Path, stage_dir: Path, meta: dict) -> None:
    """Copy exactly the whitelisted DLLs plus meta.json into stage_dir."""
    if stage_dir.exists():
        shutil.rmtree(stage_dir)
    stage_dir.mkdir(parents=True)
    for name in ASSEMBLIES:
        source = publish_dir / name
        if not source.is_file():
            raise FileNotFoundError(f"{name} is missing from {publish_dir}")
        shutil.copy2(source, stage_dir / name)
    (stage_dir / "meta.json").write_text(json.dumps(meta, indent=2) + "\n", encoding="utf-8")


def write_zip(stage_dir: Path, zip_path: Path) -> None:
    zip_path.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(stage_dir.iterdir()):
            archive.write(path, arcname=path.name)
    verify_zip(zip_path)


def verify_zip(zip_path: Path) -> None:
    """Fail unless the zip holds exactly the whitelisted DLLs and meta.json, at its root."""
    with zipfile.ZipFile(zip_path) as archive:
        names = set(archive.namelist())
    expected = expected_zip_names()
    if names != expected:
        extra = sorted(names - expected)
        missing = sorted(expected - names)
        raise ValueError(f"{zip_path.name}: unexpected contents; extra={extra} missing={missing}")


def md5(path: Path) -> str:
    digest = hashlib.md5()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(65536), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _version_key(entry: dict) -> tuple:
    return tuple(int(p) for p in entry["version"].split("."))


def update_manifest(manifest: list, new_versions: list) -> list:
    """Add version entries to TgJelly's PackageInfo, keeping older ones; newest first (spec §13.4)."""
    package = next((p for p in manifest if p.get("guid") == GUID), None)
    if package is None:
        package = {"guid": GUID, "versions": []}
        manifest.append(package)
    package.update(
        {
            "name": NAME,
            "description": DESCRIPTION,
            "overview": OVERVIEW,
            "owner": OWNER,
            "category": CATEGORY,
            "imageUrl": IMAGE_URL,
        }
    )
    replaced = {v["version"] for v in new_versions}
    kept = [v for v in package.get("versions", []) if v["version"] not in replaced]
    package["versions"] = sorted(kept + list(new_versions), key=_version_key, reverse=True)
    return manifest


def publish(tfm: str, version: str, out_dir: Path, dotnet: str) -> None:
    subprocess.run(
        [
            dotnet, "publish", str(PROJECT),
            "-c", "Release",
            "-f", tfm,
            "-o", str(out_dir),
            f"-p:Version={version}",
            f"-p:AssemblyVersion={version}",
            f"-p:FileVersion={version}",
        ],
        check=True,
    )


def main(argv: list) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--release", required=True, help="release number X.Y (from tag vX.Y)")
    parser.add_argument("--changelog", default="", help="changelog text for this release")
    parser.add_argument("--output", default=str(ROOT / "artifacts"), help="output folder")
    parser.add_argument("--manifest-in", help="existing manifest.json to extend")
    parser.add_argument("--source-url-base", help="base URL of the zips (default: the GitHub release of tag vX.Y)")
    parser.add_argument("--dotnet", default="dotnet", help="dotnet executable")
    args = parser.parse_args(argv)

    output = Path(args.output).resolve()
    base_url = args.source_url_base or f"{REPO_URL}/releases/download/v{args.release}"
    timestamp = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

    manifest = []
    if args.manifest_in:
        manifest = json.loads(Path(args.manifest_in).read_text(encoding="utf-8"))

    new_versions = []
    for track in TRACKS:
        version = track_version(track["track"], args.release)
        publish_dir = output / "publish" / track["tfm"]
        stage_dir = output / "stage" / track["tfm"]
        zip_path = output / f"tgjelly_{version}.zip"

        publish(track["tfm"], version, publish_dir, args.dotnet)
        stage(publish_dir, stage_dir, make_meta(version, track["target_abi"], args.changelog, timestamp))
        write_zip(stage_dir, zip_path)
        new_versions.append(
            {
                "version": version,
                "changelog": args.changelog,
                "targetAbi": track["target_abi"],
                "sourceUrl": f"{base_url}/{zip_path.name}",
                "checksum": md5(zip_path),
                "timestamp": timestamp,
            }
        )
        print(f"built {zip_path.name} (targetAbi {track['target_abi']})")

    manifest_path = output / "manifest.json"
    manifest_path.write_text(json.dumps(update_manifest(manifest, new_versions), indent=2) + "\n", encoding="utf-8")
    print(f"wrote {manifest_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
```

- [ ] **Step 4: Run the tests**

Run: `python3 -m unittest discover -s build -p 'test_*.py' -v`

Expected: PASS — `Ran 9 tests`, `OK`.

- [ ] **Step 5: Package both tracks for real**

Run: `export PATH="$HOME/.dotnet:$PATH" && python3 build/package.py --release 0.0 --changelog "local build" --output artifacts`

Expected: PASS — ends with `built tgjelly_1.0.0.0.zip (targetAbi 10.11.0.0)`, `built tgjelly_2.0.0.0.zip (targetAbi 12.0.0.0)` and `wrote …/artifacts/manifest.json`. Confirm each zip lists exactly `Jellyfin.Plugin.TgJelly.dll` and `meta.json`: `python3 -m zipfile -l artifacts/tgjelly_2.0.0.0.zip`. (`artifacts/` is git-ignored.)

- [ ] **Step 6: Commit**

```bash
git add build
git commit -m "build: packaging script for both tracks with whitelist and manifest"
```

---

### Task 10: CI workflow and Docker smoke test

CI builds, tests and packages on every push and PR, then installs each zip into real Jellyfin Docker images — `10.11.0`, `10.11.11`, `12.0`, `12.2` — and checks that TgJelly is **Active**, its page is served, and `GET /TgJelly/Status` reports it compatible and owning its data folder (§14.3). The official image keeps its data in `/config`, so plugins go to `/config/plugins/<Name>_<version>/`; the startup wizard endpoints accept anonymous calls until setup completes, in both versions.

**Files:**
- Create: `tests/integration/smoke.py`, `.github/workflows/ci.yml`
- Test: `tests/integration/test_smoke.py`

**Interfaces:**
- Consumes: the zips from `build/package.py` (Task 9).
- Produces: `python3 tests/integration/smoke.py --image IMAGE --zip ZIP` (exit 0 = pass); helpers `plugin_folder_name(meta) → str`, `parse_host_port(text) → int`; workflow jobs `build` and `smoke` (matrix of four images).

- [ ] **Step 1: Write the failing test**

Create `tests/integration/test_smoke.py`:

```python
"""Tests for the smoke-test helpers. Run: python3 -m unittest discover -s tests/integration -p 'test_*.py' -v"""

import unittest

import smoke


class HelperTests(unittest.TestCase):
    def test_plugin_folder_matches_jellyfin_layout(self):
        self.assertEqual(smoke.plugin_folder_name({"name": "TgJelly", "version": "2.0.0.0"}), "TgJelly_2.0.0.0")

    def test_parse_host_port_takes_first_binding(self):
        self.assertEqual(smoke.parse_host_port("127.0.0.1:49153\n"), 49153)
        self.assertEqual(smoke.parse_host_port("127.0.0.1:40001\n[::1]:40002"), 40001)


if __name__ == "__main__":
    unittest.main()
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `python3 -m unittest discover -s tests/integration -p 'test_*.py'`

Expected: FAIL — `ModuleNotFoundError: No module named 'smoke'`.

- [ ] **Step 3: Write the implementation**

Create `tests/integration/smoke.py`:

```python
#!/usr/bin/env python3
"""Docker smoke test: install a TgJelly zip into a real Jellyfin and check that it loads (spec §14.3).

Usage:
  python3 tests/integration/smoke.py --image jellyfin/jellyfin:12.2 --zip artifacts/tgjelly_2.0.0.0.zip

Needs docker. Exit code 0 means every check passed. Runs in CI; there is no local Docker requirement.
"""

import argparse
import json
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import uuid
import zipfile
from pathlib import Path

CLIENT = 'MediaBrowser Client="tgjelly-smoke", Device="ci", DeviceId="tgjelly-smoke", Version="1.0"'
USER = "smoke"
PASSWORD = "smoke-pass-1"


def plugin_folder_name(meta: dict) -> str:
    """Jellyfin's own install layout: plugins/<Name>_<version>/."""
    return f"{meta['name']}_{meta['version']}"


def parse_host_port(docker_port_output: str) -> int:
    """Parse `docker port <c> 8096/tcp` output such as '127.0.0.1:49153' (first line wins)."""
    first = docker_port_output.strip().splitlines()[0]
    return int(first.rsplit(":", 1)[1])


def docker(*args: str, check: bool = True) -> str:
    """Run docker; on failure print its stdout and stderr (a bare CalledProcessError would hide them)."""
    result = subprocess.run(["docker", *args], text=True, capture_output=True)
    if check and result.returncode != 0:
        print(f"$ docker {' '.join(args)}\n{result.stdout}{result.stderr}", file=sys.stderr)
        result.check_returncode()
    return result.stdout.strip()


def http(method: str, url: str, body=None, token: str = None, timeout: float = 20):
    """Returns (status, headers, text). Uses the header-based auth that Jellyfin 12 requires (F29)."""
    data = json.dumps(body).encode("utf-8") if body is not None else None
    request = urllib.request.Request(url, data=data, method=method)
    request.add_header("Authorization", CLIENT + (f', Token="{token}"' if token else ""))
    if data is not None:
        request.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return response.status, response.headers, response.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as error:
        return error.code, error.headers, error.read().decode("utf-8", "replace")


def check(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)
    print(f"ok - {message}")


def wait_until(predicate, what: str, timeout: float) -> None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            if predicate():
                print(f"ok - {what}")
                return
        except (OSError, urllib.error.URLError):
            pass
        time.sleep(2)
    raise AssertionError(f"timed out after {timeout:.0f}s waiting for: {what}")


def run(image: str, zip_path: Path) -> None:
    with zipfile.ZipFile(zip_path) as archive:
        meta = json.loads(archive.read("meta.json"))
        with tempfile.TemporaryDirectory() as tmp:
            plugin_dir = Path(tmp) / "plugins" / plugin_folder_name(meta)
            plugin_dir.mkdir(parents=True)
            archive.extractall(plugin_dir)

            name = f"tgjelly-smoke-{uuid.uuid4().hex[:8]}"
            try:
                docker("create", "--name", name, "-p", "127.0.0.1::8096", image)  # also pulls the image
                # Copying the "plugins" folder creates /config/plugins/<Name>_<version>/ (data dir /config, see Dockerfile).
                docker("cp", str(Path(tmp) / "plugins"), f"{name}:/config/")
                docker("start", name)
                base = f"http://127.0.0.1:{parse_host_port(docker('port', name, '8096/tcp'))}"
                checks(base, meta)
            except BaseException:
                print("---- container logs (tail) ----")
                print(docker("logs", "--tail", "200", name, check=False))
                raise
            finally:
                docker("rm", "-f", "-v", name, check=False)


def checks(base: str, meta: dict) -> None:
    wait_until(lambda: http("GET", f"{base}/health")[2].strip() == "Healthy", "server is healthy", 240)

    # Startup wizard: open to anonymous callers until it completes (same in 10.11 and 12).
    check(http("GET", f"{base}/Startup/Configuration")[0] == 200, "wizard: read configuration")
    status = http("POST", f"{base}/Startup/Configuration",
                  {"UICulture": "en-US", "MetadataCountryCode": "US", "PreferredMetadataLanguage": "en"})[0]
    check(status in (200, 204), "wizard: save configuration")
    check(http("GET", f"{base}/Startup/User")[0] == 200, "wizard: read first user")
    check(http("POST", f"{base}/Startup/User", {"Name": USER, "Password": PASSWORD})[0] in (200, 204), "wizard: set user")
    check(http("POST", f"{base}/Startup/Complete")[0] in (200, 204), "wizard: complete")

    status, _, text = http("POST", f"{base}/Users/AuthenticateByName", {"Username": USER, "Pw": PASSWORD})
    check(status == 200, "login")
    token = json.loads(text)["AccessToken"]

    status, _, text = http("GET", f"{base}/Plugins", token=token)
    check(status == 200, "list plugins")
    plugin = next((p for p in json.loads(text) if p.get("Name") == meta["name"]), None)
    check(plugin is not None, "TgJelly is installed")
    check(plugin.get("Status") == "Active", f"TgJelly is Active (got {plugin.get('Status')})")
    check(plugin.get("Version") == meta["version"], f"TgJelly version is {meta['version']}")

    status, _, html = http("GET", f"{base}/web/ConfigurationPage?name=tgjelly")
    check(status == 200 and 'data-role="page"' in html, "dashboard page is served")
    status, headers, _ = http("GET", f"{base}/web/ConfigurationPage?name=tgjelly.js")
    check(status == 200 and "javascript" in (headers.get("Content-Type") or ""), "page controller is served as JavaScript")

    def status_ok():
        code, _, body = http("GET", f"{base}/TgJelly/Status", token=token)
        data = json.loads(body) if code == 200 else {}
        return data.get("Compatible") is True and data.get("InstanceOwner") is True

    wait_until(status_ok, "GET /TgJelly/Status: compatible and owning its data folder", 60)


def main(argv) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--image", required=True, help="e.g. jellyfin/jellyfin:12.2")
    parser.add_argument("--zip", required=True, type=Path, help="plugin zip built by build/package.py")
    args = parser.parse_args(argv)
    run(args.image, args.zip)
    print("smoke test passed")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
```

Create `.github/workflows/ci.yml`:

```yaml
name: CI

on:
  push:
    branches: ['**']
  pull_request:
  workflow_dispatch:

permissions:
  contents: read

jobs:
  build:
    name: Build, test, package
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v7

      - uses: actions/setup-dotnet@v6
        with:
          dotnet-version: |
            9.0.x
            10.0.x

      - name: Build (net9.0 + net10.0, warnings are errors)
        run: dotnet build Jellyfin.Plugin.TgJelly.slnx -c Release

      - name: Unit tests (both frameworks)
        run: dotnet test --solution Jellyfin.Plugin.TgJelly.slnx -c Release --no-build

      - name: Script tests
        run: |
          python3 -m unittest discover -s build -p 'test_*.py' -v
          python3 -m unittest discover -s tests/integration -p 'test_*.py' -v

      - name: Package both tracks
        run: python3 build/package.py --release 0.0 --changelog "CI build ${{ github.sha }}" --output artifacts

      - uses: actions/upload-artifact@v7
        with:
          name: packages
          path: |
            artifacts/*.zip
            artifacts/manifest.json

  smoke:
    name: Smoke ${{ matrix.image }}
    needs: build
    runs-on: ubuntu-latest
    strategy:
      fail-fast: false
      matrix:
        include:
          - image: jellyfin/jellyfin:10.11.0
            zip: tgjelly_1.0.0.0.zip
          - image: jellyfin/jellyfin:10.11.11
            zip: tgjelly_1.0.0.0.zip
          - image: jellyfin/jellyfin:12.0
            zip: tgjelly_2.0.0.0.zip
          - image: jellyfin/jellyfin:12.2
            zip: tgjelly_2.0.0.0.zip
    steps:
      - uses: actions/checkout@v7

      - uses: actions/download-artifact@v8
        with:
          name: packages
          path: artifacts

      - name: Install the plugin into a real Jellyfin and check it
        run: python3 tests/integration/smoke.py --image ${{ matrix.image }} --zip artifacts/${{ matrix.zip }}
```

- [ ] **Step 4: Run the local checks**

Run: `python3 -m unittest discover -s tests/integration -p 'test_*.py' -v && python3 -m py_compile tests/integration/smoke.py`

Expected: PASS — `Ran 2 tests`, `OK`.

- [ ] **Step 5: Commit**

```bash
git add tests/integration .github/workflows/ci.yml
git commit -m "ci: build, test, package and smoke-test on Jellyfin 10.11 and 12"
```

- [ ] **Step 6: Run CI** (outward-facing: **ask the owner first**)

The smoke test needs Docker, which this machine doesn't have, so it runs in GitHub Actions. When asking to push, also ask whether follow-up pushes to `feat/plan1-foundation` (to fix CI) are approved; if not, ask before each push. Then:

```bash
git push -u origin feat/plan1-foundation
# The first run of a new workflow appears after a few seconds; wait for its id.
for i in $(seq 1 30); do
  RUN_ID=$(gh run list --branch feat/plan1-foundation --limit 1 --json databaseId --jq '.[0].databaseId // empty')
  [ -n "$RUN_ID" ] && break
  sleep 5
done
echo "run: $RUN_ID"
```

Watch it until it finishes. The four smoke jobs pull Docker images, so expect several minutes: run `gh run watch --exit-status "$RUN_ID"` with a long timeout or in the background, or poll `gh run view "$RUN_ID" --json status,conclusion` every minute.

Expected: the `build` job and all four `smoke` jobs succeed. If a smoke job fails, its log shows Docker's own error output and the container's last 200 log lines; fix the cause and commit, then push again (with approval).

---

## Self-check against the spec (done while writing this plan)

| Spec | Where |
|---|---|
| §5.1 identity, data folder | Tasks 1, 4, 8 (`DataPaths`, registrator test) |
| §5.3 CompatGuard, §12 CompatGuard banner | Task 2; exposed in Status (Task 8) |
| §5.4 rules 2 and 4 (host safety, statics) | Tasks 5, 8 (`Supervisor`, never-throwing `StopAsync`, detached host). Rules 3 and 5 (consumer constructors, nothing sent before startup) apply from plans 2–3 |
| §10.1 settings model and ranges | Task 3 |
| §10.2 secrets | Task 4 |
| §10.3 state files and cadence | Task 6 (sections are added by later plans) |
| §10.4 writes, revisions, outside saves, backup restore, state.lock | Tasks 5, 6, 7, 8 |
| §11.1 page shell rules, §11.2 `GET Status` | Task 8 |
| §13.2 project file, §13.3 versions/ABI, §13.4 packaging, §13.5 CI | Tasks 1, 9, 10 |
| §14.3 smoke matrix | Task 10 (BaseUrl variant in plan 5) |

---

## Roadmap: plans 2–5

Each plan is written with the writing-plans skill after the previous one has been executed, so it can build on the real code. Each ends in working, testable software.

- **Plan 2 — Telegram core** (§9, §6.1, §10.2–§10.4 token rules, §11.2 token/destination/settings endpoints, §12 logging, Appendix A). Telegram.Bot 22.10.3.3 (added to `ASSEMBLIES`), client factory with proxy modes and the `TGJELLY_BOT_API_BASE_URL` override, `ITelegramSender`, the own `GetUpdates` loop with in-memory offset and connection states, Outbox (queues, priority, pacing, caps and rolling summary, retries, edit merging, file_id cache), error classifier and destination health with re-checks, token save flow (same/different bot, profile calls), Destinations API (add by ID, test, check, remove), **GET/PUT Settings** (plan 3's end-to-end test needs it), **§12 secret redaction** for every TgJelly log line and exception (with a test using a real-looking token), a fake Bot API server for tests, the string table's first entries. Settles §15 Q7 (bin contents) and records Q9's error strings. Ends with: connect a bot, add a chat by ID, receive a test message.
- **Plan 3 — Notification pipeline** (§7, §8.1–§8.4, §8.6, §12 delivery). Intake consumers and watchers (incl. `ScanFinishedTask`, `StartupWatcher` for `server.started`), two intake queues, MediaReadiness (filter, album tracking, readiness, upgrades), Router, Aggregator (groups, release buffer, digests, late edits, security coalescing), Renderer (default layouts, posters, links, `<tg-time>`, plurals), UpdateChecker, **§12's 5-second graceful drain** and at-least-once delivery, and the end-to-end smoke check against the fake Bot API. Settles §15 Q8 (edit time limits). Ends with: real notifications.
- **Plan 4 — Bot UX** (§6.2–§6.5). UpdateRouter, FlowEngine with persisted flows, native-picker onboarding, forum topics (promotion picker, existing-topic link), menus and screens, detected chats, fallback gating, manager notifications. Settles §15 Q1 (BotFather path), Q2 (button URLs), Q3 (picker UX) and Q4 (General topic) on a real bot.
- **Plan 5 — Dashboard, templates, release** (§6.1 first-run card, §6.6, §8.5, §11, §13.5 release, §14.3 BaseUrl variant, §14.4). The full dashboard page and remaining endpoints (strings from `GET Strings`, notice dismissal including `loadWarnings`, Diagnostics with redaction), Scriban templates (context, limits, validation; `Scriban.dll` added to `ASSEMBLIES`), README and `docs/templates.md`, `release.yml` with the `gh-pages` manifest and `logo.png`, the BaseUrl smoke variant, and the §14.4 manual pre-release checklist. Settles §15 Q5 and Q6.
