# TgJelly Plan 2: Telegram Core — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** TgJelly connects to a Telegram bot from the dashboard API, keeps a supervised long-poll connection, tracks each destination's health, delivers queued messages through a paced Outbox with every §9.4 fallback, and lets an admin add a chat by ID and receive the automatic test message. CI proves the last part end to end against a stub Bot API inside real Jellyfin 10.11 and 12.

**Architecture:** Telegram.Bot 22.10.3.3 is used through two doors only: `ITelegramSender` (one thin method per Bot API call, named arguments) and `BotSupervisor`'s own GetUpdates loop. `BotConnection` holds the current client, sender and connection state, and the supervisor swaps it on token or proxy changes. `HealthService` owns `state.health`; `Outbox` (queue rules, pure over `QueueDocument`) and `OutboxDelivery` (one send, all fallbacks) feed an `OutboxService` loop. Every logger TgJelly resolves is a `RedactingLogger<T>`, so a bot token never reaches a log line. The dashboard API is split into four controllers under `/TgJelly`.

**Tech Stack:** C# / .NET 9 + .NET 10 (unchanged from plan 1), Telegram.Bot **22.10.3.3**, xunit.v3, NSubstitute, `Microsoft.Extensions.TimeProvider.Testing`, Python 3 stdlib for the CI stub.

**Spec:** `docs/superpowers/specs/2026-10-16-tgjelly-v1-design.md` (rev. 2). This plan implements §9 (all), §6.1 (the API side of the token flow), §10.2–§10.4 (token change, disconnect, the `telegram`, `health`, `status` state and the `outbox`, `sentMessages`, `fileIdCache` queue sections), §11.2 (Status, Token, Webhook, Settings, Proxy, Destinations, Test, Check, Links), §12 (logging and redaction), Appendix A, and §14.1's classifier, Outbox, health, token-change and poll-loop tests. It also settles §15 Q7 and prepares Q9. Plan 1 (`docs/superpowers/plans/2026-10-17-tgjelly-plan1-foundation.md`) is merged code; read its Global Constraints, which still apply.

**Deliberate deviations and deferrals (so reviewers don't flag them):**
- **Test sends bypass the Outbox.** `POST Destinations/{id}/Test` and the automatic test after an add call Telegram directly through `DestinationTester`, so the dashboard gets the result in the same request. §9.4 already says test sends bypass the hold; pacing doesn't matter for a one-off.
- **TopicMissing probe = `sendChatAction(typing)` to the thread** (§9.4 says "a probe send"). It proves the thread exists without posting a visible message.
- **Entries of a paused destination (`Enabled = false`) are held**, like an unhealthy destination's (TTL and caps still apply). The spec only says the Router stops routing to it.
- **Deferred to plan 3:** the render model on Outbox entries and re-rendering edits from it, the `⏱` delayed-delivery line, `server.started`, the graceful 5 s drain, preset upgrades on load (with the Router), the PublicUrl *suggestion* in `GET Settings` (it needs `LinkBuilder`'s network rules). Plan 2's entries carry their rendered text; edits carry their new text.
- **Deferred to plan 4:** `UpdateRouter`/`FlowEngine`, detected chats, managers and the private messages about destination problems (`health.notified` is stored now and consumed there), the bot work queue, the §6.5 manual-block gate in `GET Destinations`.
- **Unit tests talk to an in-process fake Bot API** (`FakeBotApi`, an `HttpMessageHandler` behind a real `TelegramBotClient`) instead of a socket server. CI uses a real HTTP stub (`tests/integration/botapi_stub.py`).
- **Every `POST Token` outcome returns 200** with an `Outcome` field; the page branches on it (§6.1 has several non-error outcomes: needs-confirm, unreachable → proxy panel).

## Global Constraints

Plan 1's Global Constraints apply unchanged (identity, data folder, two builds, analyzers with zero warnings and one type per file, host safety, no static mutable state, settings rules, dashboard rules, zip whitelist, owner approval, the `export PATH="$HOME/.dotnet:$PATH" &&` prefix, one commit per task with the harness's trailer lines). In addition, from the spec:

- **Telegram.Bot 22.10.3.3**, with `RetryThreshold = 0` on every client (F39). The zip whitelist becomes `["Jellyfin.Plugin.TgJelly.dll", "Telegram.Bot.dll"]` (F5).
- **Only `ITelegramSender` and `BotSupervisor`'s GetUpdates loop call Telegram.Bot API methods** (§5.4 rule 1). Calls use named arguments.
- **The token is never logged, returned or shown** (§10.2, §12, F39). Every log line and diagnostics text passes `SecretRedactor` (`\d{5,}:[A-Za-z0-9_-]{30,}` → `<token>`). Request URLs are never logged. Proxy passwords are never logged.
- **`TGJELLY_BOT_API_BASE_URL`** is read once at startup, is never part of settings or the API, and switches Telegram.Bot to local-server mode only when non-blank (§9.1).
- **HttpClient:** a `SocketsHttpHandler` with `PooledConnectionLifetime = 3 min`, `HttpClient.Timeout = 100 s`, not from `IHttpClientFactory` (§9.1).
- **Poll:** `limit: 100`, `timeout: 50`, `allowedUpdates: [Message, CallbackQuery, MyChatMember]`, offset in memory only (§9.2).
- **Pacing (§9.3):** ≥ 1 s between sends to one chat, ≤ 20 per minute per group or channel, ≤ 25 per second overall; caps 500 media / 50 security+server per destination; TTL 24 h; retries 2 s → 5 min ± 20 %.
- **Health never changes `Destination.Enabled`** (§9.4). Re-check every 30 min while unhealthy or `PhotosBlocked`.
- **All user-facing text comes from `Strings/en.json`** through `StringTable` (§8.6). Telegram HTML values go through `HtmlText.Escape`.
- **Jellyfin API JSON is PascalCase**; request bodies bind case-insensitively.

## Review Focus

The five inputs the spec implies but its happy paths don't exercise, most likely first. Each has a test in the task that owns the code:

1. **Telegram is unreachable** (no internet, a wrong proxy) when the token is saved or while polling. Expected: `POST Token` answers `Unreachable` (the page opens the proxy panel) and stores nothing; the supervisor shows "Telegram unreachable, check proxy", backs off, and reconnects by itself when the network returns. Tests: `BotTokenServiceTests.Unreachable_ReturnsUnreachable_AndStoresNothing` (Task 12), `BotSupervisorTests.TransportErrors_ShowUnreachable_ThenReconnect` (Task 11).
2. **A bot token inside an exception or error text** (a transport error, an `ArgumentException`, a description echoed by a stub). Expected: no log line, `status.lastErrors` entry or Status field contains it. Tests: `RedactingLoggerTests.TokenInMessageAndException_IsRedacted` (Task 2), `StateStoreErrorTests.RecordError_RedactsTokens` (Task 1), `BotTokenServiceTests.NothingReturnedOrLogged_ContainsTheToken` (Task 12).
3. **The token or proxy changes while a 50-second long poll is in flight** (or two token saves arrive at once). Expected: the old poll is cancelled and finished before the new session polls, so TgJelly never conflicts with itself (409 "terminated by other getUpdates request"). Test: `BotSupervisorTests.Restart_DuringALongPoll_NeverPollsTwiceAtOnce` (Task 11).
4. **The same chat is added twice at once** (two dashboard tabs, or a double click). Expected: one destination; the second request answers `AlreadyExists`, never a 500 or a duplicate. Test: `DestinationsControllerTests.TheSameChatAddedTwiceAtOnce_CreatesOneDestination` (Task 14).
5. **Jellyfin shuts down while Telegram hangs** (a black-holed connection mid-poll or mid-send). Expected: `StopAsync` returns within the host's grace, the offset confirmation gives up after 2 s, and nothing throws (F3, F4). Test: `BotSupervisorTests.Stop_WhileTelegramHangs_ReturnsQuickly_AndDoesNotThrow` (Task 11).

---

## File Structure

```
src/Jellyfin.Plugin.TgJelly/
  Logging/            SecretRedactor, RedactingLogger<T>, RedactedException, RedactedLoggingExtensions
  Strings/            en.json (embedded), StringTable
  Bot/                BotApiEnvironment, ITelegramClientFactory, TelegramClientFactory,
                      ITelegramSender, TelegramSender, OutgoingText, OutgoingPhoto, MessageEdit,
                      TelegramErrorClassifier, DeliveryError(+Kind), PollError(+Kind),
                      BotConnection, ConnectionState, BotNotConnectedException,
                      IServerStartup, ServerStartup, IUpdateHandler, BasicUpdateHandler,
                      ProfileCalls, BotSupervisor, BotTokenService, TokenSaveResult(+Outcome), BotStateReset
  Destinations/       DestinationIds, TopicLink, ChatCheckResult, DestinationChecker, HealthService,
                      DestinationTester, TestSendResult
  Delivery/           Outbox, PacingGate, OutboxDelivery, OutboxService
  Configuration/      EventCatalog, PublicUrlRules (+ SettingsUpdateStatus.Unchanged, SettingsStore.Update(Func<…,bool>))
  State/Documents/    TelegramSection, ProfileState, DestinationHealth, HealthStatus, StatusError, StatusCounters,
                      PublicUrlFlag, OutboxEntry, OutboxOp, OutboxMessage, OutboxButton, SentMessage, SentKind, CachedFileId
  Api/                BotController, SettingsController, DestinationsController (+ request/response DTOs), StatusDto (extended)
tests/Jellyfin.Plugin.TgJelly.Tests/
  Fakes/FakeBotApi.cs, Fakes/BotApiCall.cs, Fakes/BotTestHarness.cs
  Logging/, Strings/, Bot/, Destinations/, Delivery/, Api/   tests per unit
tests/integration/botapi_stub.py, tests/integration/test_botapi_stub.py, smoke.py (extended)
.github/workflows/ci.yml (stub, F8 variant, bin listing)
build/package.py (whitelist)
```

---

### Task 1: Plan-1 follow-ups: errors in Status, safer reads, small fixes

Closes the plan-1 review's deferred minors before anything builds on them. `status.lastErrors` (§10.3) is created here; later tasks record into it.

**Files:**
- Create: `src/Jellyfin.Plugin.TgJelly/State/Documents/StatusError.cs`, `StatusCounters.cs`
- Modify: `State/Documents/StatusSection.cs`, `State/StateStore.cs`, `State/SecretsStore.cs`, `State/AtomicFile.cs`, `State/LenientEnumConverterFactory.cs`, `Hosting/PersistenceService.cs`, `Plugin.cs`, `PluginServiceRegistrator.cs`, `Api/StatusDto.cs`, `Api/TgJellyController.cs`
- Test: `tests/…/State/StateStoreErrorTests.cs`; extend `State/SecretsStoreTests.cs`, `State/StateStoreTests.cs`, `State/AtomicFileTests.cs`, `Configuration/PluginConfigurationXmlTests.cs` (or a new `State/LenientEnumTests.cs`), `PluginConfigurationHostTests.cs`, `Hosting/PersistenceServiceTests.cs`, `Api/TgJellyControllerTests.cs`

**Interfaces:**
- Consumes: plan 1's `StateStore`, `SecretsStore`, `AtomicFile`, `PersistenceService`, `Plugin`, `InstanceGate`.
- Produces: `StatusError { DateTimeOffset At; string Source; string Message }`; `StatusCounters { long DroppedOutbox; long DroppedIntake; long Evictions }`; `StatusSection.LastErrors` (`List<StatusError>`, cap `StateStore.MaxLastErrors = 20`, oldest dropped), `StatusSection.Counters`; `StateStore.RecordError(string source, string message)` (redacts with `SecretRedactor` once Task 2 exists — in this task, store the message as given and add the redaction call in Task 2, Step 5); `StateStore.LastErrorsSnapshot()` (works before `Load`, like `Read`); `StatusDto.LastErrors` (`IReadOnlyList<StatusError>`).

- [ ] **Step 1: Write the failing tests**

`StateStoreErrorTests.cs`:

```csharp
[Fact]
public void RecordError_KeepsTheNewest20_InOrder()
{
    using var dir = new TempDir();
    var store = new StateStore(new DataPaths(dir.Path), new FakeTimeProvider(), NullLogger<StateStore>.Instance);
    store.Load();

    for (var i = 0; i < 25; i++)
    {
        store.RecordError("outbox", $"error {i}");
    }

    var errors = store.LastErrorsSnapshot();
    Assert.Equal(StateStore.MaxLastErrors, errors.Count);
    Assert.Equal("error 5", errors[0].Message);
    Assert.Equal("error 24", errors[^1].Message);
}

[Fact]
public void RecordError_BeforeLoad_IsKeptInMemory_AndSurvivesLoad()
{
    using var dir = new TempDir();
    var store = new StateStore(new DataPaths(dir.Path), new FakeTimeProvider(), NullLogger<StateStore>.Instance);

    store.RecordError("persistence", "disk full");
    store.Load();

    Assert.Contains(store.LastErrorsSnapshot(), e => e.Message == "disk full");
}
```

Add, each in the file that owns the class:

- `SecretsStoreTests.UnreadableFile_IsALoadWarning_NotAnException` — `[UnsupportedOSPlatform("windows")]`, `Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.UserName == "root", …)`; write a valid `secrets.json`, `File.SetUnixFileMode(path, UnixFileMode.None)`, then `store.Load()` returns `BotSecrets.Empty`, `store.LoadWarning` contains `"secrets.json"`, and the file is **not** moved aside (`File.Exists(path)`; it may be fine once permissions are fixed). Restore the mode in `finally`.
- `StateStoreTests.UnreadableStateFile_IsALoadWarning_NotAnException` — same shape for `state.json`: `Load()` doesn't throw, `IsLoaded` is true, `Read(s => s.Status.LoadWarnings)` has one entry naming `state.json`, the file is not quarantined.
- `AtomicFileTests.TwoQuarantinesInTheSameSecond_KeepBothFiles` — quarantine `state.json` twice with the same `now`; both returned paths exist and differ (the second gets a `-1` suffix before `.json`).
- `LenientEnumTests.ObjectOrArrayValue_ForAnEnum_ReadsTheFallback_AndTheRestOfTheFileLoads` — deserialize `{"kind":{"x":1},"title":"t"}` and `{"kind":[1,2],"title":"t"}` into a test record with a `ChatKind Kind` and `string Title` using `JsonDefaults.Options`: `Kind == ChatKind.Private` (the first member), `Title == "t"`.
- `PluginConfigurationHostTests.OutsideSave_WithMaxRevision_DoesNotWrapAround` — `plugin.UpdateConfiguration(new PluginConfiguration { Revision = long.MaxValue })` after a store save at revision 5: `plugin.Configuration.Revision == 6`; a later `store.Update` still raises `SettingsChanged`.
- `PersistenceServiceTests.FlushFailure_IsRecordedInStatus_AndTheLoopKeepsRunning` — `[UnsupportedOSPlatform("windows")]`, skip on Windows/root; start the service with the gate owned, make the data folder read-only (`UserRead | UserExecute`), change state (`store.Update(...)`), then `Eventually.AdvanceUntilAsync(time, TimeSpan.FromSeconds(1), () => store.LastErrorsSnapshot().Any(e => e.Source == "persistence"), "flush error recorded")`; restore the folder mode; `AdvanceUntilAsync(... () => File.Exists(paths.State) ...)` proves the same loop then writes it (no supervisor restart: assert a counting `ILogger<PersistenceService>` saw no "restarting" error).
- `PluginServiceRegistratorTests.DetachedHost_LogsAWarning` — calling `PluginServiceRegistrator.ResolveConfigurationHost(null, logger)` returns a `DetachedConfigurationHost` and `logger` received one `Warning`. (Change the internal helper's signature to take an `ILogger`; the registration resolves `ILogger<PluginServiceRegistrator>` from the provider.)
- **Fix the two tests the plan-1 review found vacuous:**
  - `PersistenceServiceTests.UnwritableDataFolder_DoesNotStopTheService_AndStateIsWrittenWhenItRecovers`: assert the failure happened before restoring the mode (`store.LastErrorsSnapshot()` has a `"persistence"` entry), so the test can't pass when the timer never fired.
  - `TgJellyControllerTests.Status_Answers_BeforeOwnershipAndBeforeStateIsLoaded`: hold `InstanceLock.TryAcquire(paths.Lock)` and **start** the gate, wait until `gate.WaitingMessage == InstanceGate.WaitingForPreviousInstance`, then call `GetStatus()`; the busy-lock path is now the one under test.
- `TgJellyControllerTests.Status_ShowsLastErrors` — after `state.RecordError("outbox", "boom")`, `GetStatus().Value!.LastErrors` has one entry with `Source == "outbox"`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet test --project tests/Jellyfin.Plugin.TgJelly.Tests -c Release -f net10.0`
Expected: FAIL — the build reports that `RecordError`, `LastErrorsSnapshot`, `MaxLastErrors` and `StatusDto.LastErrors` don't exist (CS1061/CS0117).

- [ ] **Step 3: Implement**

- `StatusSection`: add `List<StatusError> LastErrors` and `StatusCounters Counters`.
- `StateStore`: `public const int MaxLastErrors = 20;` `RecordError(source, message)` appends `new StatusError { At = _time.GetUtcNow(), Source = source, Message = message }` under the lock, trims from the front to 20, marks state dirty; before `Load`, errors go to a pending list that `Load` appends after reading the file. `LastErrorsSnapshot()` returns a copy (pending list before load).
- `StateStore.ReadOrDefault` and `SecretsStore.Load`: catch `IOException` and `UnauthorizedAccessException` on **read** → load warning "`<file>` can't be read: <message>" and an empty document; don't quarantine (moving it may fail too, and the file may be fine once permissions are fixed). `JsonException` keeps plan 1's quarantine path.
- `AtomicFile.Quarantine`: when the target name exists, append `-1`, `-2`, … before `.json`; never pass `overwrite: true`.
- `LenientEnumConverterFactory`: on a `StartObject`/`StartArray` token call `reader.Skip()` before returning the fallback.
- `Plugin.UpdateConfiguration` (outside branch): `var basis = incoming.Revision == long.MaxValue ? current : Math.Max(current, incoming.Revision); incoming.Revision = basis + 1;` (a hostile or broken POST can't wrap the revision).
- `PersistenceService.RunAsync`: wrap `FlushDue()` in `try/catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)` → `_state.RecordError("persistence", ex.Message)` and a `LogWarning`, at most one log line per distinct message per minute; the loop continues. The secrets read moves out of the loop body's failure path: wrap `_secrets.Load()` the same way (its warning is already in `LoadWarning`).
- `PluginServiceRegistrator.ResolveConfigurationHost(Plugin? plugin, ILogger logger)`: log `Warning` "TgJelly's plugin instance is missing; settings can't be saved until Jellyfin restarts" when falling back.
- `StatusDto.LastErrors`; `TgJellyController.GetStatus` fills it from `LastErrorsSnapshot()`.

- [ ] **Step 4: Run all tests**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet build Jellyfin.Plugin.TgJelly.slnx -c Release && dotnet test --solution Jellyfin.Plugin.TgJelly.slnx -c Release --no-build`
Expected: PASS — `0 Warning(s)`, `failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "fix: plan-1 review follow-ups (lastErrors, unreadable files, quarantine names, revision clamp)"
```

---

### Task 2: Telegram.Bot, packaging whitelist and secret redaction

**Files:**
- Modify: `src/Jellyfin.Plugin.TgJelly/Jellyfin.Plugin.TgJelly.csproj`, `build/package.py`, `build/test_package.py`, `src/…/State/StateStore.cs` (redact in `RecordError`), `src/…/PluginServiceRegistrator.cs`
- Create: `src/…/Logging/SecretRedactor.cs`, `RedactingLogger.cs`, `RedactedException.cs`, `RedactedLoggingExtensions.cs`
- Create: `tests/…/Fakes/CapturingLoggerProvider.cs` (an `ILoggerProvider` recording `Category`, `Level`, formatted `Message` and `Exception?.ToString()`; reused by Task 12)
- Test: `tests/…/Logging/SecretRedactorTests.cs`, `tests/…/Logging/RedactingLoggerTests.cs`, extend `PluginServiceRegistratorTests.cs`, `State/StateStoreErrorTests.cs`

**Interfaces:**
- Produces:
  - `SecretRedactor.Placeholder` (`"<token>"`), `SecretRedactor.Redact(string? text) → string` (null → `""`).
  - `RedactingLogger<T> : ILogger<T>`, constructor `(ILoggerFactory factory)`, category `typeof(T).FullName` (what `Logger<T>` uses). Logs the formatted message redacted, as a plain string state, and wraps a non-null exception in `RedactedException`.
  - `RedactedException : Exception` (internal): `Message` = redacted inner message; `ToString()` = redacted `inner.ToString()`; `StackTrace` = the inner's.
  - `RedactedLoggingExtensions.AddRedactedLogger<T>(this IServiceCollection services)` registers `ILogger<T>` → `new RedactingLogger<T>(sp.GetRequiredService<ILoggerFactory>())` (a closed generic beats Jellyfin's open `ILogger<>`).
  - `PluginServiceRegistrator` calls `AddRedactedLogger<T>()` for every TgJelly type whose constructor takes `ILogger<T>`; from here on, every task that adds such a type adds its call.

- [ ] **Step 1: Write the failing tests**

```csharp
// SecretRedactorTests
private const string Token = "123456789:AAEhBP0av28KrBd9xL4Zq3Wm7Tn5Ys1Cv6QpZ";

[Theory]
[InlineData("https://api.telegram.org/bot123456789:AAEhBP0av28KrBd9xL4Zq3Wm7Tn5Ys1Cv6QpZ/getMe", "https://api.telegram.org/bot<token>/getMe")]
[InlineData("token 123456789:AAEhBP0av28KrBd9xL4Zq3Wm7Tn5Ys1Cv6QpZ, again 123456789:AAEhBP0av28KrBd9xL4Zq3Wm7Tn5Ys1Cv6QpZ", "token <token>, again <token>")]
[InlineData("chat -1001234567890 and time 12:30", "chat -1001234567890 and time 12:30")]
[InlineData(null, "")]
public void Redact_ReplacesTokens_AndLeavesOtherNumbersAlone(string? input, string expected)
    => Assert.Equal(expected, SecretRedactor.Redact(input));

// RedactingLoggerTests — a capturing ILoggerProvider records (level, message, exception.ToString())
[Fact]
public void TokenInMessageAndException_IsRedacted()
{
    var provider = new CapturingLoggerProvider();
    using var factory = LoggerFactory.Create(b => b.AddProvider(provider));
    var logger = new RedactingLogger<RedactingLoggerTests>(factory);
    var inner = new HttpRequestException($"failed for /bot{Token}/getUpdates");

    logger.LogError(new InvalidOperationException($"outer {Token}", inner), "Request {Url} failed", $"/bot{Token}/getMe");

    var entry = Assert.Single(provider.Entries);
    Assert.Equal(LogLevel.Error, entry.Level);
    Assert.DoesNotContain(Token, entry.Message, StringComparison.Ordinal);
    Assert.DoesNotContain(Token, entry.Exception, StringComparison.Ordinal);
    Assert.Contains("<token>", entry.Message, StringComparison.Ordinal);
    Assert.Equal(typeof(RedactingLoggerTests).FullName, entry.Category);
}

// PluginServiceRegistratorTests
[Fact]
public void EveryTgJellyLogger_IsRedacting()
{
    var services = new ServiceCollection();
    new PluginServiceRegistrator().RegisterServices(services, Substitute.For<IServerApplicationHost>());
    var needed = typeof(Plugin).Assembly.GetTypes()
        .SelectMany(t => t.GetConstructors())
        .SelectMany(c => c.GetParameters())
        .Select(p => p.ParameterType)
        .Where(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ILogger<>))
        .Distinct();
    foreach (var loggerType in needed)
    {
        Assert.Contains(services, d => d.ServiceType == loggerType && d.ImplementationFactory is not null);
    }
}
```

Plus `StateStoreErrorTests.RecordError_RedactsTokens`: `RecordError("bot", $"oops {Token}")` → the snapshot's message is `"oops <token>"`.

In `build/test_package.py`, add `test_whitelist_includes_telegram_bot`: `self.assertEqual(package.ASSEMBLIES, ["Jellyfin.Plugin.TgJelly.dll", "Telegram.Bot.dll"])`.

- [ ] **Step 2: Run them to verify they fail**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet test --project tests/Jellyfin.Plugin.TgJelly.Tests -c Release -f net10.0 --filter-namespace '*.Logging' ; python3 -m unittest discover -s build -p 'test_*.py'`
Expected: FAIL — `SecretRedactor`/`RedactingLogger` don't exist; the Python test fails on the whitelist.

- [ ] **Step 3: Implement**

- csproj: `<PackageReference Include="Telegram.Bot" Version="22.10.3.3" />` (both TFMs; no `ExcludeAssets`).
- `package.py`: `ASSEMBLIES = ["Jellyfin.Plugin.TgJelly.dll", "Telegram.Bot.dll"]`; update the comment (plan 5 adds `Scriban.dll`).
- The four `Logging/` types per the Interfaces block. `SecretRedactor` uses a `[GeneratedRegex(@"\d{5,}:[A-Za-z0-9_-]{30,}")]` partial method.
- `StateStore.RecordError` passes the message through `SecretRedactor.Redact`.
- `PluginServiceRegistrator`: `AddRedactedLogger<…>()` for `SecretsStore`, `StateStore`, `SettingsStore`, `InstanceGate`, `PersistenceService`, `PluginServiceRegistrator`.

- [ ] **Step 4: Run all tests and the packaging script**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet build Jellyfin.Plugin.TgJelly.slnx -c Release && dotnet test --solution Jellyfin.Plugin.TgJelly.slnx -c Release --no-build && python3 -m unittest discover -s build -p 'test_*.py' && python3 build/package.py --release 0.0 --changelog "local build" --output artifacts && python3 -m zipfile -l artifacts/tgjelly_2.0.0.0.zip`
Expected: PASS — `0 Warning(s)`, `failed: 0`, the Python tests `OK`, and the zip lists exactly `Jellyfin.Plugin.TgJelly.dll`, `Telegram.Bot.dll`, `meta.json`.

- [ ] **Step 5: Commit**

```bash
git add src tests build
git commit -m "feat: Telegram.Bot dependency and redacting loggers for every TgJelly type"
```

---

### Task 3: String table

**Files:**
- Create: `src/…/Strings/en.json` (embedded resource), `src/…/Strings/StringTable.cs`
- Modify: csproj (`<EmbeddedResource Include="Strings\en.json" />`), `PluginServiceRegistrator.cs` (`AddSingleton(_ => StringTable.LoadEmbedded())`)
- Test: `tests/…/Strings/StringTableTests.cs`

**Interfaces:**
- Produces: `StringTable.LoadEmbedded(string language = "en") → StringTable` (missing keys fall back to `en` key by key); `string Get(string key, params (string Name, object? Value)[] args)`; `string Plural(string key, long count, params (string Name, object? Value)[] args)` (English rule: `count == 1` → `key.one`, else `key.other`; `{count}` is always available); an unknown key returns the key itself; values are formatted with the table's culture (`en` → `CultureInfo.GetCultureInfo("en-US")`). Placeholders are `{name}`; a missing argument leaves `{name}` in place.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void Get_FillsNamedPlaceholders()
    => Assert.Equal("Connected as @jf_bot", Strings.Get("connection.Connected", ("bot", "jf_bot")));

[Theory]
[InlineData(1, "+1 more security/server event, see the dashboard")]
[InlineData(2, "+2 more security/server events, see the dashboard")]
public void Plural_PicksOneOrOther(long count, string expected)
    => Assert.Equal(expected, Strings.Plural("outbox.summary", count));

[Fact]
public void UnknownKey_ReturnsTheKey() => Assert.Equal("no.such.key", Strings.Get("no.such.key"));

[Fact]
public void EveryKeyThePluginUses_Exists()
{
    foreach (var key in StringKeys.All) // a test-side list, grown by later tasks
    {
        Assert.NotEqual(key, Strings.Get(key));
    }
}

private static readonly StringTable Strings = StringTable.LoadEmbedded();
```

- [ ] **Step 2: Run to verify it fails**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet test --project tests/Jellyfin.Plugin.TgJelly.Tests -c Release -f net10.0 --filter-class '*.StringTableTests'`
Expected: FAIL — `StringTable` doesn't exist.

- [ ] **Step 3: Implement, with this initial `en.json`** (later tasks add keys and append them to `StringKeys.All` in the test project):

```json
{
  "connection.NoToken": "No bot connected",
  "connection.WaitingForStartup": "Waiting for Jellyfin to finish starting",
  "connection.Incompatible": "Wrong build for this server",
  "connection.WaitingForInstance": "Waiting for the previous instance to stop",
  "connection.Connecting": "Connecting to Telegram…",
  "connection.Connected": "Connected as @{bot}",
  "connection.TokenRejected": "Token rejected",
  "connection.WebhookConflict": "Another app set a webhook for this bot",
  "connection.ConflictingInstance": "Another app is polling this bot",
  "connection.Unreachable": "Telegram unreachable, check proxy",
  "connection.InvalidBaseUrl": "TGJELLY_BOT_API_BASE_URL is not a valid URL",
  "token.format": "This doesn't look like a bot token",
  "token.rejected": "Telegram rejected this token",
  "token.unreachable": "Can't reach Telegram. Check the proxy settings.",
  "token.failed": "Telegram answered with an error: {error}",
  "token.switch.one": "@{bot} is a different bot. It isn't in your destination, so it will need to be added again.",
  "token.switch.other": "@{bot} is a different bot. It isn't in your {count} destinations, so they will need to be added again.",
  "token.webhookDeleted": "Another app had set a webhook for this bot. TgJelly removed it.",
  "health.botChanged": "bot changed: add @{bot} to this chat",
  "check.notFound": "Telegram can't find this chat. Add the bot to it first.",
  "check.channelNotAdmin": "The bot must be an admin of this channel with permission to post messages.",
  "check.groupCantSend": "The bot isn't a member of this group or can't send messages there.",
  "check.topicMissing": "This topic doesn't exist any more.",
  "check.lost": "The bot was removed from this chat or blocked.",
  "check.photosBlocked": "Members can't send photos here, so posters will be skipped.",
  "destination.duplicate": "{title} is already set up",
  "destination.notConnected": "Connect a bot first",
  "destination.badLink": "This isn't a Telegram topic link",
  "test.message": "✅ TgJelly test: this chat will receive <i>{groups}</i>",
  "groups.media": "new media",
  "groups.security": "security alerts",
  "groups.server": "server alerts",
  "groups.none": "nothing yet (all events are off)",
  "groups.separator": ", ",
  "outbox.summary.one": "+{count} more security/server event, see the dashboard",
  "outbox.summary.other": "+{count} more security/server events, see the dashboard",
  "profile.description": "Jellyfin notifications from {server}",
  "profile.shortDescription": "Jellyfin notifications from {server}",
  "command.menu": "Open the menu",
  "command.status": "Show the status",
  "command.test": "Send a test message",
  "command.help": "How TgJelly works"
}
```

`test.message` contains HTML; callers escape `{groups}`' value before passing it. `LoadEmbedded` reads `Jellyfin.Plugin.TgJelly.Strings.<lang>.json` with `JsonSerializer.Deserialize<Dictionary<string, string>>`.

- [ ] **Step 4: Run all tests** — same command as Task 1 Step 4. Expected: PASS, `0 Warning(s)`.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat: embedded English string table with named placeholders and plurals"
```

---

### Task 4: Telegram client factory

**Files:**
- Create: `src/…/Bot/BotApiEnvironment.cs`, `Bot/ITelegramClientFactory.cs`, `Bot/TelegramClientFactory.cs`
- Modify: `PluginServiceRegistrator.cs`
- Test: `tests/…/Bot/TelegramClientFactoryTests.cs`

**Interfaces:**
- Consumes: `SettingsStore.Current.Proxy` (`ProxySettings { Mode, Url }`), `SecretsStore.Load()` (`ProxyUsername`, `ProxyPassword`).
- Produces:
  - `sealed record BotApiEnvironment(string? BaseUrl)`, `const string VariableName = "TGJELLY_BOT_API_BASE_URL"`, `static BotApiEnvironment FromEnvironment()` (blank → `null`). Registered once as a singleton.
  - `interface ITelegramClientFactory { ITelegramBotClient Create(string token); }` — throws `ArgumentException` with `ParamName` `"token"` (bad format) or `"baseUrl"` (bad environment value), exactly as `TelegramBotClientOptions` does (F39).
  - `TelegramClientFactory(SettingsStore settings, SecretsStore secrets, BotApiEnvironment environment) : ITelegramClientFactory, IDisposable`. Keeps one `SocketsHttpHandler` and rebuilds it (disposing the old one) when (mode, URL, username, password) changes; each client gets `new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(100) }`.
  - `internal static SocketsHttpHandler CreateHandler(ProxySettings proxy, BotSecrets secrets)`: `PooledConnectionLifetime = 3 min`; `System` → defaults (`UseProxy = true`, no explicit proxy); `None` → `UseProxy = false`; `Http` → `Proxy = new WebProxy(url)`; `Socks5` → `Proxy = new WebProxy(socksUrl)`. A URL without a scheme gets `http://` or `socks5://` by mode. Credentials → `new NetworkCredential(username, password)` when the username isn't empty.
  - `internal static TelegramBotClientOptions CreateOptions(string token, BotApiEnvironment environment)` → `new TelegramBotClientOptions(token, environment.BaseUrl) { RetryThreshold = 0 }`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void Options_DefaultToTheRealApi_WithRetriesOff()
{
    var options = TelegramClientFactory.CreateOptions(FakeBotApi.Token, new BotApiEnvironment(null));
    Assert.False(options.LocalBotServer);
    Assert.Equal(0, options.RetryThreshold);
    Assert.Equal(123456789, options.BotId);
}

[Fact]
public void Options_UseTheEnvironmentBaseUrl()
    => Assert.True(TelegramClientFactory.CreateOptions(FakeBotApi.Token, new BotApiEnvironment("http://stub:8081")).LocalBotServer);

[Theory]
[InlineData("not-a-token", "token")]
[InlineData("abc:def", "token")]
public void BadToken_ThrowsWithParamNameToken(string token, string param)
    => Assert.Equal(param, Assert.Throws<ArgumentException>(() => TelegramClientFactory.CreateOptions(token, new BotApiEnvironment(null))).ParamName);

[Fact]
public void BadBaseUrl_ThrowsWithParamNameBaseUrl()
    => Assert.Equal("baseUrl", Assert.Throws<ArgumentException>(() => TelegramClientFactory.CreateOptions(FakeBotApi.Token, new BotApiEnvironment("stub"))).ParamName);

[Theory]
[InlineData(ProxyMode.None, "", false, null)]
[InlineData(ProxyMode.Http, "proxy.lan:3128", true, "http://proxy.lan:3128/")]
[InlineData(ProxyMode.Socks5, "socks5://10.0.0.2:1080", true, "socks5://10.0.0.2:1080/")]
public void Handler_FollowsTheProxyMode(ProxyMode mode, string url, bool useProxy, string? proxyUri)
{
    using var handler = TelegramClientFactory.CreateHandler(new ProxySettings { Mode = mode, Url = url }, BotSecrets.Empty);
    Assert.Equal(TimeSpan.FromMinutes(3), handler.PooledConnectionLifetime);
    Assert.Equal(useProxy, handler.UseProxy);
    Assert.Equal(proxyUri, (handler.Proxy as WebProxy)?.Address?.ToString());
}

[Fact]
public void Handler_UsesProxyCredentials()
{
    using var handler = TelegramClientFactory.CreateHandler(
        new ProxySettings { Mode = ProxyMode.Http, Url = "http://proxy.lan:3128" },
        BotSecrets.Empty with { ProxyUsername = "u", ProxyPassword = "p" });
    var credentials = (NetworkCredential)((WebProxy)handler.Proxy!).Credentials!;
    Assert.Equal(("u", "p"), (credentials.UserName, credentials.Password));
}
```

(`FakeBotApi.Token` is defined in Task 5; until then use a local constant with the same value, `"123456789:AAEhBP0av28KrBd9xL4Zq3Wm7Tn5Ys1Cv6QpZ"`, and switch to `FakeBotApi.Token` in Task 5.)

- [ ] **Step 2: Run to verify it fails** — `--filter-class '*.TelegramClientFactoryTests'`. Expected: FAIL, `TelegramClientFactory` doesn't exist.
- [ ] **Step 3: Implement** per the Interfaces block. Register `BotApiEnvironment.FromEnvironment()` and `ITelegramClientFactory → TelegramClientFactory` as singletons.
- [ ] **Step 4: Run all tests** — Expected: PASS, `0 Warning(s)`.
- [ ] **Step 5: Commit** — `git commit -m "feat: Telegram client factory with proxy modes and the base-URL override"`

---

### Task 5: Fake Bot API and `ITelegramSender`

**Files:**
- Create: `src/…/Bot/ITelegramSender.cs`, `TelegramSender.cs`, `OutgoingText.cs`, `OutgoingPhoto.cs`, `MessageEdit.cs`
- Create: `tests/…/Fakes/FakeBotApi.cs`, `tests/…/Fakes/BotApiCall.cs`
- Test: `tests/…/Bot/TelegramSenderTests.cs`

**Interfaces:**
- Produces (src):
  - `sealed record OutgoingText(long ChatId, int ThreadId, string Text, ParseMode ParseMode, InlineKeyboardMarkup? Buttons, int? ReplyToMessageId, bool Silent)`.
  - `sealed record OutgoingPhoto(long ChatId, int ThreadId, InputFile Photo, string Caption, ParseMode ParseMode, InlineKeyboardMarkup? Buttons, int? ReplyToMessageId, bool Silent)`.
  - `sealed record MessageEdit(long ChatId, int MessageId, string Html, ParseMode ParseMode, InlineKeyboardMarkup? Buttons)`.
  - `interface ITelegramSender` and `TelegramSender(ITelegramBotClient client) : ITelegramSender`:

    | Method | Bot API call (named arguments) |
    |---|---|
    | `Task<User> GetMeAsync(CancellationToken ct)` | `GetMe` |
    | `Task<WebhookInfo> GetWebhookInfoAsync(CancellationToken ct)` | `GetWebhookInfo` |
    | `Task DeleteWebhookAsync(CancellationToken ct)` | `DeleteWebhook(dropPendingUpdates: false)` |
    | `Task<ChatFullInfo> GetChatAsync(ChatId chat, CancellationToken ct)` | `GetChat` |
    | `Task<ChatMember> GetChatMemberAsync(ChatId chat, long userId, CancellationToken ct)` | `GetChatMember` |
    | `Task<Message> SendTextAsync(OutgoingText m, CancellationToken ct)` | `SendMessage(..., linkPreviewOptions: new() { IsDisabled = true })` |
    | `Task<Message> SendPhotoAsync(OutgoingPhoto m, CancellationToken ct)` | `SendPhoto` |
    | `Task EditTextAsync(MessageEdit e, CancellationToken ct)` | `EditMessageText(..., linkPreviewOptions: new() { IsDisabled = true })` |
    | `Task EditCaptionAsync(MessageEdit e, CancellationToken ct)` | `EditMessageCaption` |
    | `Task SendTypingAsync(long chatId, int threadId, CancellationToken ct)` | `SendChatAction(action: ChatAction.Typing)` |
    | `Task LeaveChatAsync(long chatId, CancellationToken ct)` | `LeaveChat` |
    | `Task AnswerCallbackAsync(string callbackQueryId, CancellationToken ct)` | `AnswerCallbackQuery` |
    | `Task SetCommandsAsync(IReadOnlyList<BotCommand> commands, CancellationToken ct)` | `SetMyCommands(scope: new BotCommandScopeAllPrivateChats())`, then `SetMyCommands(commands: [], scope: new BotCommandScopeDefault())` (§6.1: empty default scope) |
    | `Task SetDescriptionsAsync(string description, string shortDescription, CancellationToken ct)` | `SetMyDescription`, `SetMyShortDescription` |
    | `Task SetChannelAdminRightsAsync(CancellationToken ct)` | `SetMyDefaultAdministratorRights(rights: new() { CanPostMessages = true, CanEditMessages = true }, forChannels: true)` |

    `ThreadId == 0` → `messageThreadId: null`; otherwise the thread. `ReplyToMessageId` → `replyParameters: new() { MessageId = id, AllowSendingWithoutReply = true }`. `Silent` → `disableNotification`. The sender never catches exceptions; callers classify them (Task 6).
- Produces (tests):
  - `sealed record BotApiCall(string Method, JsonObject Body)` (multipart form fields become string properties; the photo part's file name is kept as `"photo"` → `"attach:<filename>"` or the file id string).
  - `FakeBotApi : HttpMessageHandler`:
    - `const string Token = "123456789:AAEhBP0av28KrBd9xL4Zq3Wm7Tn5Ys1Cv6QpZ"`, `const long BotId = 123456789`, `const string BotUsername = "tgjelly_test_bot"`.
    - `IReadOnlyList<BotApiCall> Calls` (a thread-safe snapshot), `IEnumerable<BotApiCall> CallsTo(string method)`.
    - `void On(string method, Func<BotApiCall, string> respond)` — replaces the default answer; `respond` returns the full response body. `void OnSequence(string method, params string[] bodies)` — answers in order, then repeats the last.
    - `static string Ok(string resultJson)`, `static string Error(int code, string description, string? parametersJson = null)` — the HTTP status equals `code` (Telegram does the same; Telegram.Bot reads the body).
    - `void AddChat(long chatId, string type, string title, bool isForum = false, string? permissionsJson = null)`, `void SetMember(long chatId, string memberJson)` — data for the default `getChat`/`getChatMember` answers (unknown chat → `Error(400, "Bad Request: chat not found")`).
    - `ITelegramBotClient CreateClient()` → `new TelegramBotClient(new TelegramBotClientOptions(Token, "http://fake.test") { RetryThreshold = 0 }, new HttpClient(this))`.
    - Defaults: `getMe` → `{"id":123456789,"is_bot":true,"first_name":"TgJelly test","username":"tgjelly_test_bot"}`; `getWebhookInfo` → `{"url":"","has_custom_certificate":false,"pending_update_count":0}`; `getUpdates` → waits 20 ms (real time, honouring cancellation), then `[]`; `sendMessage`/`sendPhoto` → a message with an increasing `message_id` (photo messages carry a `photo` array whose last size has `file_id` `"file-<n>"`); `editMessageText`/`editMessageCaption` → a message; every other method → `true`.

- [ ] **Step 1: Write the failing tests**

```csharp
public sealed class TelegramSenderTests
{
    private readonly FakeBotApi _api = new();
    private TelegramSender Sender => new(_api.CreateClient());

    [Fact]
    public async Task SendText_UsesHtml_Thread_Reply_Silence_AndNoPreview()
    {
        var ct = TestContext.Current.CancellationToken;
        await Sender.SendTextAsync(new OutgoingText(-1001, 7, "<b>hi</b>", ParseMode.Html, null, 42, Silent: true), ct);

        var body = Assert.Single(_api.CallsTo("sendMessage")).Body;
        Assert.Equal(-1001, (long)body["chat_id"]!);
        Assert.Equal(7, (int)body["message_thread_id"]!);
        Assert.Equal("HTML", (string)body["parse_mode"]!);
        Assert.Equal(42, (int)body["reply_parameters"]!["message_id"]!);
        Assert.True((bool)body["disable_notification"]!);
        Assert.True((bool)body["link_preview_options"]!["is_disabled"]!);
    }

    [Fact]
    public async Task ThreadZero_SendsNoThreadId()
    {
        await Sender.SendTextAsync(new OutgoingText(-1001, 0, "x", ParseMode.None, null, null, false), TestContext.Current.CancellationToken);
        Assert.False(Assert.Single(_api.CallsTo("sendMessage")).Body.ContainsKey("message_thread_id"));
    }

    [Fact]
    public async Task SetCommands_SetsPrivateScope_AndClearsTheDefaultScope()
    {
        await Sender.SetCommandsAsync([new BotCommand("menu", "Open the menu")], TestContext.Current.CancellationToken);
        var calls = _api.CallsTo("setMyCommands").ToArray();
        Assert.Equal("all_private_chats", (string)calls[0].Body["scope"]!["type"]!);
        Assert.Equal("default", (string)calls[1].Body["scope"]!["type"]!);
        Assert.Empty(calls[1].Body["commands"]!.AsArray());
    }

    [Fact]
    public async Task ApiErrors_ArriveAsApiRequestException_WithParameters()
    {
        _api.On("sendMessage", _ => FakeBotApi.Error(429, "Too Many Requests: retry after 7", """{"retry_after":7}"""));
        var ex = await Assert.ThrowsAsync<ApiRequestException>(() =>
            Sender.SendTextAsync(new OutgoingText(1, 0, "x", ParseMode.None, null, null, false), TestContext.Current.CancellationToken));
        Assert.Equal(429, ex.ErrorCode);
        Assert.Equal(7, ex.Parameters!.RetryAfter);
        Assert.Single(_api.CallsTo("sendMessage")); // RetryThreshold = 0: Telegram.Bot did not retry
    }
}
```

Also `SetChannelAdminRights_RequestsPostAndEdit` (asserts `for_channels` true and `rights.can_post_messages`/`can_edit_messages` true) and `SendPhoto_UploadsTheStream_WithItsFileName` (`InputFile.FromStream(new MemoryStream([1, 2, 3]), "poster.jpg")` → the recorded call's `photo` is `"attach:poster.jpg"` or contains `poster.jpg`).

- [ ] **Step 2: Run to verify it fails** — `--filter-class '*.TelegramSenderTests'`. Expected: FAIL, `TelegramSender`/`FakeBotApi` don't exist.
- [ ] **Step 3: Implement** the sender types and `FakeBotApi`. The method name is the last path segment of the request URL. Telegram.Bot sends JSON bodies (parse with `JsonNode.Parse`), and `MultipartFormDataContent` for uploads: iterate its parts and key them by `Headers.ContentDisposition.Name`, reading string parts as text and recording a file part as `"attach:" + FileName`.
- [ ] **Step 4: Run all tests** — Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat: ITelegramSender over Telegram.Bot, with an in-process fake Bot API for tests"`

---

### Task 6: Error classifier

**Files:**
- Create: `src/…/Bot/DeliveryErrorKind.cs`, `DeliveryError.cs`, `PollErrorKind.cs`, `PollError.cs`, `TelegramErrorClassifier.cs`
- Test: `tests/…/Bot/TelegramErrorClassifierTests.cs`

**Interfaces:**
- Produces:
  - `enum DeliveryErrorKind { PhotosBlocked, Lost, NeedsPermissions, TopicMissing, Migrated, ParseError, BadButton, BadPhoto, TooLong, EditTargetGone, NotModified, RateLimited, Transport, ServerError, Other }` — rows 1–15 of §9.4, in order.
  - `sealed record DeliveryError(DeliveryErrorKind Kind, string Description, TimeSpan? RetryAfter = null, long? MigrateToChatId = null)`.
  - `enum PollErrorKind { TokenRejected, ConflictingInstance, WebhookConflict, RateLimited, ApiError, Unreachable }`; `sealed record PollError(PollErrorKind Kind, string Description, TimeSpan? RetryAfter = null)`.
  - `static DeliveryError TelegramErrorClassifier.ClassifyDelivery(Exception ex)` and `static PollError ClassifyPoll(Exception ex)`. Matching: `OrdinalIgnoreCase` substrings, first row wins. `RetryAfter` = `Parameters.RetryAfter` seconds, or 5 s when a 429 carries none. A non-Telegram exception → `Other` / `ApiError` with its message (redacted by the caller's logger). `OperationCanceledException` is never classified: callers let it propagate.

- [ ] **Step 1: Write the failing table test** — every Appendix A string, plus the edge cases §14.1 names:

```csharp
public static TheoryData<int, string, DeliveryErrorKind> Delivery => new()
{
    { 400, "Bad Request: not enough rights to send photos to the chat", DeliveryErrorKind.PhotosBlocked },
    { 400, "Bad Request: not enough rights to send text messages to the chat", DeliveryErrorKind.NeedsPermissions },
    { 403, "Forbidden: bot was blocked by the user", DeliveryErrorKind.Lost },
    { 403, "Forbidden: user is deactivated", DeliveryErrorKind.Lost },
    { 403, "Forbidden: bot was kicked from the group chat", DeliveryErrorKind.Lost },
    { 403, "Forbidden: bot was kicked from the supergroup chat", DeliveryErrorKind.Lost },
    { 403, "Forbidden: bot was kicked from the channel chat", DeliveryErrorKind.Lost },
    { 403, "Forbidden: bot is not a member of the group chat", DeliveryErrorKind.Lost },
    { 403, "Forbidden: bot is not a member of the supergroup chat", DeliveryErrorKind.Lost },
    { 403, "Forbidden: bot is not a member of the channel chat", DeliveryErrorKind.Lost },
    { 403, "Forbidden: the group chat was deleted", DeliveryErrorKind.Lost },
    { 403, "Forbidden: bot can't initiate conversation with a user", DeliveryErrorKind.Lost },
    { 400, "Bad Request: chat not found", DeliveryErrorKind.Lost },
    { 400, "Bad Request: private chat not found", DeliveryErrorKind.Lost },
    { 400, "Bad Request: need administrator rights in the channel chat", DeliveryErrorKind.NeedsPermissions },
    { 400, "Bad Request: have no write access to the chat", DeliveryErrorKind.NeedsPermissions },
    { 400, "Bad Request: message thread not found", DeliveryErrorKind.TopicMissing },
    { 400, "Bad Request: can't parse entities: Unsupported start tag \"x\" at byte offset 12", DeliveryErrorKind.ParseError },
    { 400, "Bad Request: can't parse entities: Can't find end tag corresponding to start tag \"b\"", DeliveryErrorKind.ParseError },
    { 400, "Bad Request: inline keyboard button URL 'http://jellyfin/web' is invalid: Wrong HTTP URL", DeliveryErrorKind.BadButton },
    { 400, "Bad Request: BUTTON_URL_INVALID", DeliveryErrorKind.BadButton },
    { 400, "Bad Request: IMAGE_PROCESS_FAILED", DeliveryErrorKind.BadPhoto },
    { 400, "Bad Request: wrong file identifier/HTTP URL specified", DeliveryErrorKind.BadPhoto },
    { 400, "Bad Request: PHOTO_INVALID_DIMENSIONS", DeliveryErrorKind.BadPhoto },
    { 400, "Bad Request: message is too long", DeliveryErrorKind.TooLong },
    { 400, "Bad Request: message caption is too long", DeliveryErrorKind.TooLong },
    { 400, "Bad Request: message to edit not found", DeliveryErrorKind.EditTargetGone },
    { 400, "Bad Request: message can't be edited", DeliveryErrorKind.EditTargetGone },
    { 400, "Bad Request: there is no text in the message to edit", DeliveryErrorKind.EditTargetGone },
    { 400, "Bad Request: message is not modified: specified new message content and reply markup are exactly the same as a current content and reply markup of the message", DeliveryErrorKind.NotModified },
    { 429, "Too Many Requests: retry after 9", DeliveryErrorKind.RateLimited },
    { 502, "Bad Gateway", DeliveryErrorKind.ServerError },
    { 400, "Bad Request: something new", DeliveryErrorKind.Other },
};

[Theory]
[MemberData(nameof(Delivery))]
public void Delivery_FirstMatchingRowWins_CaseInsensitive(int code, string description, DeliveryErrorKind expected)
{
    Assert.Equal(expected, TelegramErrorClassifier.ClassifyDelivery(Api(code, description)).Kind);
    Assert.Equal(expected, TelegramErrorClassifier.ClassifyDelivery(Api(code, description.ToUpperInvariant())).Kind);
}

[Fact]
public void Migration_CarriesTheNewChatId()
{
    var error = TelegramErrorClassifier.ClassifyDelivery(Api(400, "Bad Request: group chat was upgraded to a supergroup chat", migrateTo: -1009876));
    Assert.Equal((DeliveryErrorKind.Migrated, -1009876L), (error.Kind, error.MigrateToChatId));
}

[Fact]
public void RateLimit_CarriesRetryAfter() =>
    Assert.Equal(TimeSpan.FromSeconds(9), TelegramErrorClassifier.ClassifyDelivery(Api(429, "Too Many Requests: retry after 9", retryAfter: 9)).RetryAfter);

[Theory]
[InlineData("Bot API Request timed out")]
[InlineData("Bot API Service Failure: HttpRequestException: Connection refused")]
public void TransportErrors_AreTransport(string message)
    => Assert.Equal(DeliveryErrorKind.Transport, TelegramErrorClassifier.ClassifyDelivery(new RequestException(message)).Kind);

[Theory]
[InlineData(401, "Unauthorized", PollErrorKind.TokenRejected)]
[InlineData(409, "Conflict: terminated by other getUpdates request; make sure that only one bot instance is running", PollErrorKind.ConflictingInstance)]
[InlineData(409, "Conflict: can't use getUpdates method while webhook is active; use deleteWebhook to delete the webhook first", PollErrorKind.WebhookConflict)]
[InlineData(409, "Conflict: terminated by setWebhook request", PollErrorKind.WebhookConflict)]
[InlineData(429, "Too Many Requests: retry after 3", PollErrorKind.RateLimited)]
[InlineData(500, "Internal Server Error", PollErrorKind.ApiError)]
public void Poll_Errors(int code, string description, PollErrorKind expected)
    => Assert.Equal(expected, TelegramErrorClassifier.ClassifyPoll(Api(code, description)).Kind);

private static ApiRequestException Api(int code, string description, long? migrateTo = null, int? retryAfter = null)
    => new(description, code, new ResponseParameters { MigrateToChatId = migrateTo, RetryAfter = retryAfter });
```

- [ ] **Step 2: Run to verify it fails** — `--filter-class '*.TelegramErrorClassifierTests'`. Expected: FAIL.
- [ ] **Step 3: Implement** the classifier as an ordered rule list `(Func<int, string, ResponseParameters?, bool> match, DeliveryErrorKind kind)`, mirroring the §9.4 table rows 1–15 (row 1: `"not enough rights to send"` and not `"text messages"`; row 2: 403 with any of `kicked`, `not a member`, `blocked`, `deactivated`, `was deleted`, `can't initiate conversation`, or 400 `chat not found`; row 8: `photo_`, `image_process_failed`, `wrong file`, `photo_invalid`; row 6: `can't parse entities`, `unsupported start tag`, `unmatched end tag`, `can't find end tag`, `byte offset`; row 7: `inline keyboard button`, `button_url_invalid`).
- [ ] **Step 4: Run all tests** — Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat: Telegram error classifier for deliveries and polling (spec 9.4, Appendix A)"`

---
### Task 7: Destination rules: event catalog, public URL rule, IDs, topic links, no-op saves

Pure helpers the API, health and later the Router need.

**Files:**
- Create: `src/…/Configuration/EventCatalog.cs`, `Configuration/PublicUrlRules.cs`, `Destinations/DestinationIds.cs`, `Destinations/TopicLink.cs`
- Modify: `Configuration/SettingsUpdateStatus.cs` (add `Unchanged`), `Configuration/SettingsStore.cs`
- Test: `tests/…/Configuration/EventCatalogTests.cs`, `Configuration/PublicUrlRulesTests.cs`, `Destinations/DestinationIdsTests.cs`, `Destinations/TopicLinkTests.cs`, extend `Configuration/SettingsStoreTests.cs`

**Interfaces:**
- Produces:
  - `EventCatalog.AllKeys` (`IReadOnlyList<string>`, the 19 keys of §7.1 in table order), `EventCatalog.Groups` (`["media", "security", "server"]`), `GroupOf(string eventKey) → string` (the prefix before the first `.`), `IsKnown(string key) → bool`, `Expand(PresetKind preset) → string[]` (§7.2; `Custom` → empty).
  - `PublicUrlRules.IsValid(string? url) → bool` (§8.3: an absolute `http`/`https` URL whose host contains a `.` or is an IPv6 literal).
  - `DestinationIds.New(IEnumerable<string> existing) → string` (8 characters from `a-z0-9`, `RandomNumberGenerator.GetString`, not in `existing`; passes `SettingsValidator.IsValidId`).
  - `sealed record TopicLink(ChatId Chat, int ThreadId)` with `static bool TryParse(string? text, [NotNullWhen(true)] out TopicLink? link)`.
  - `SettingsUpdateStatus.Unchanged`; `SettingsStore.Update(long? expectedRevision, Func<PluginConfiguration, bool> change) → SettingsUpdateResult`: same as the `Action` overload, but when `change` returns `false` nothing is saved, the revision stays, no event fires, and the status is `Unchanged`. The revision check still runs first.

- [ ] **Step 1: Write the failing tests**

```csharp
// EventCatalogTests
[Fact]
public void Presets_ExpandToTheSpecsKeys()
{
    Assert.Equal(19, EventCatalog.Expand(PresetKind.Everything).Length);
    Assert.Equal(["media.movie", "media.episodes", "media.album", "media.musicvideo", "media.book"], EventCatalog.Expand(PresetKind.NewMedia));
    var admin = EventCatalog.Expand(PresetKind.AdminAlerts);
    Assert.Equal(13, admin.Length);
    Assert.DoesNotContain("security.login_succeeded", admin);
    Assert.Contains("server.started", admin);
    Assert.Empty(EventCatalog.Expand(PresetKind.Custom));
}

[Theory]
[InlineData("media.movie", "media")]
[InlineData("security.lockout", "security")]
[InlineData("server.restart_pending", "server")]
public void GroupOf_IsThePrefix(string key, string group) => Assert.Equal(group, EventCatalog.GroupOf(key));

// PublicUrlRulesTests
[Theory]
[InlineData("https://jf.example.com", true)]
[InlineData("http://192.168.1.10:8096/jf", true)]
[InlineData("http://[fd00::1]:8096", true)]
[InlineData("http://localhost:8096", false)]
[InlineData("http://jellyfin:8096", false)]
[InlineData("ftp://jf.example.com", false)]
[InlineData("jf.example.com", false)]
[InlineData("", false)]
[InlineData(null, false)]
public void IsValid(string? url, bool expected) => Assert.Equal(expected, PublicUrlRules.IsValid(url));

// TopicLinkTests
[Theory]
[InlineData("https://t.me/c/1234567890/42", -1001234567890L, null, 42)]
[InlineData("https://t.me/c/1234567890/42/100", -1001234567890L, null, 42)]
[InlineData("t.me/c/1234567890/42", -1001234567890L, null, 42)]
[InlineData("https://telegram.me/c/1234567890/7", -1001234567890L, null, 7)]
[InlineData("https://t.me/jfgroup/42", null, "@jfgroup", 42)]
[InlineData("https://t.me/jfgroup/42/9", null, "@jfgroup", 42)]
public void TryParse_ValidLinks(string text, long? chatId, string? username, int thread)
{
    Assert.True(TopicLink.TryParse(text, out var link));
    Assert.Equal(thread, link.ThreadId);
    Assert.Equal(chatId, link.Chat.Identifier);
    Assert.Equal(username, link.Chat.Username);
}

[Theory]
[InlineData("https://t.me/c/1234567890")]
[InlineData("https://t.me/c/1234567890/0")]
[InlineData("https://t.me/c/abc/42")]
[InlineData("https://example.com/c/1234567890/42")]
[InlineData("https://t.me/jfgroup")]
[InlineData("not a link")]
[InlineData("")]
public void TryParse_RejectsEverythingElse(string text) => Assert.False(TopicLink.TryParse(text, out _));

// DestinationIdsTests
[Fact]
public void New_IsValid_AndAvoidsExistingIds()
{
    var existing = new HashSet<string>();
    for (var i = 0; i < 500; i++)
    {
        var id = DestinationIds.New(existing);
        Assert.True(SettingsValidator.IsValidId(id));
        Assert.True(existing.Add(id));
    }
}

// SettingsStoreTests (added)
[Fact]
public void Update_ReturningFalse_SavesNothing_AndIsUnchanged()
{
    var events = 0;
    _store.SettingsChanged += (_, _) => events++;
    var result = _store.Update(0, _ => false);
    Assert.Equal(SettingsUpdateStatus.Unchanged, result.Status);
    Assert.Equal((0, 0L, 0), (_host.Saves, _host.Configuration.Revision, events));
}
```

- [ ] **Step 2: Run to verify they fail** — `dotnet test … -f net10.0`. Expected: FAIL, the new types don't exist.
- [ ] **Step 3: Implement** per the Interfaces block. `TopicLink`: accept an optional `http(s)://` prefix, host `t.me` or `telegram.me` (case-insensitive), then `c/<digits>/<topic>[/<msg>]` → `ChatId(long.Parse("-100" + digits))`, or `<username>/<topic>[/<msg>]` → `ChatId("@" + username)`; the topic must be a positive `int`.
- [ ] **Step 4: Run all tests** — Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat: event catalog and presets, public URL rule, destination IDs, topic links"`

---

### Task 8: Connection holder, chat checks and destination health

**Files:**
- Create: `src/…/Bot/ConnectionState.cs`, `Bot/BotConnection.cs`, `Bot/BotNotConnectedException.cs`
- Create: `src/…/State/Documents/HealthStatus.cs`, `State/Documents/DestinationHealth.cs`; modify `State/Documents/StateDocument.cs` (add `Dictionary<string, DestinationHealth> Health`)
- Create: `src/…/Destinations/ChatCheckResult.cs`, `Destinations/DestinationChecker.cs`, `Destinations/HealthService.cs`
- Create: `tests/…/Fakes/BotTestHarness.cs`
- Modify: `PluginServiceRegistrator.cs` (singletons `BotConnection`, `DestinationChecker`; `HealthService` singleton + hosted, registered after `PersistenceService`; redacted loggers)
- Test: `tests/…/Destinations/DestinationCheckerTests.cs`, `Destinations/HealthServiceTests.cs`

**Interfaces:**
- Consumes: `ITelegramSender` (Task 5), `TelegramErrorClassifier` (Task 6), `StringTable` (Task 3), `SettingsStore`, `StateStore`, `InstanceGate`.
- Produces:
  - `enum ConnectionState { NoToken, WaitingForInstance, Incompatible, WaitingForStartup, Connecting, Connected, TokenRejected, WebhookConflict, ConflictingInstance, Unreachable, InvalidBaseUrl }`.
  - `BotConnection`: `ConnectionState State`, `string? Detail`, `ITelegramSender? Sender`, `long BotId`, `string BotUsername` (empty when unknown); `void SetState(ConnectionState state, string? detail = null)`; `void Attach(ITelegramSender sender, long botId, string botUsername)`; `void Detach()`; `ITelegramSender RequireSender()` (throws `BotNotConnectedException` unless a sender is attached); `event EventHandler? Changed` (raised on any state or attach change). Thread-safe.
  - `enum HealthStatus { Ok, NeedsPermissions, TopicMissing, Lost }`; `DestinationHealth { HealthStatus Status; bool PhotosBlocked; string? LastError; DateTimeOffset? At; bool Notified; DateTimeOffset? NextCheckAt }`.
  - `sealed record ChatCheckResult(bool Passed, HealthStatus Problem, string? Message, long ChatId, string Title, ChatKind Kind, bool IsForum, bool PhotosBlocked)`.
  - `DestinationChecker(BotConnection connection, StringTable strings)`: `Task<ChatCheckResult> CheckAsync(ChatId chat, int threadId, CancellationToken ct)`. A health-type Telegram error (`Lost`, `NeedsPermissions`, `TopicMissing`) is a failed result; any other error (transport, 5xx, 429, unknown) **throws**, so callers keep the previous health.
  - `HealthService(InstanceGate gate, BotConnection connection, DestinationChecker checker, SettingsStore settings, StateStore state, TimeProvider time, ILogger<HealthService> logger) : BackgroundService`:
    - `static readonly TimeSpan RecheckInterval = TimeSpan.FromMinutes(30)`;
    - `DestinationHealth Get(string destinationId)` (a copy; `Ok` when unknown), `bool IsDeliverable(string destinationId)` (`Status == Ok`);
    - `void MarkProblem(string destinationId, HealthStatus status, string error)` (a change of status resets `Notified`; `NextCheckAt = now + 30 min`), `void SetPhotosBlocked(string destinationId)`, `void SetChecked(string destinationId, ChatCheckResult result)`, `void Remove(string destinationId)`;
    - `Task<DestinationHealth> CheckNowAsync(string destinationId, CancellationToken ct)` (checks and applies the result; errors that `DestinationChecker` throws propagate);
    - `void RequestCheckForChat(long chatId)`, `void RequestCheckAll()`;
    - `event EventHandler? Recovered` (a destination went from not-OK to OK).
  - Test harness `BotTestHarness : IDisposable` with `Dir`, `Time` (`FakeTimeProvider` at 2026-10-18 10:00 UTC), `Host` (`FakeConfigurationHost`), `Paths`, `State` (loaded), `Settings`, `Secrets`, `Strings`, `Api` (`FakeBotApi`), `Connection`; `void Connect()` (attaches a `TelegramSender` over `Api.CreateClient()` with `FakeBotApi.BotId`/`BotUsername` and sets `Connected`); `Destination AddDestination(long chatId, ChatKind kind = ChatKind.Supergroup, int threadId = 0, PresetKind preset = PresetKind.Everything, string? id = null)` (saves through `Settings.Update`, registers the chat in `Api` with the bot as administrator with `can_post_messages`); `InstanceGate StartOwnedGate()`.

- [ ] **Step 1: Write the failing tests**

`DestinationCheckerTests` (fixtures are Bot API JSON, as §14.2 asks):

```csharp
private const string AdminCanPost = """{"status":"administrator","user":{"id":123456789,"is_bot":true,"first_name":"b"},"can_be_edited":false,"can_manage_chat":true,"can_change_info":false,"can_post_messages":true,"can_edit_messages":true,"can_delete_messages":false,"can_invite_users":false,"can_restrict_members":false,"can_promote_members":false,"can_manage_video_chats":false,"is_anonymous":false,"can_post_stories":false,"can_edit_stories":false,"can_delete_stories":false}""";
private const string Member = """{"status":"member","user":{"id":123456789,"is_bot":true,"first_name":"b"}}""";
private const string RestrictedNoPhotos = """{"status":"restricted","user":{"id":123456789,"is_bot":true,"first_name":"b"},"is_member":true,"can_send_messages":true,"can_send_audios":true,"can_send_documents":true,"can_send_photos":false,"can_send_videos":true,"can_send_video_notes":true,"can_send_voice_notes":true,"can_send_polls":true,"can_send_other_messages":true,"can_add_web_page_previews":true,"can_change_info":false,"can_invite_users":false,"can_pin_messages":false,"can_manage_topics":false,"until_date":0}""";
private const string Left = """{"status":"left","user":{"id":123456789,"is_bot":true,"first_name":"b"}}""";

[Fact] public async Task Channel_AdminWhoCanPost_Passes() { /* AddChat(-1001,"channel","News"); SetMember(-1001, AdminCanPost) → Passed, Kind Channel, Title "News" */ }
[Fact] public async Task Channel_PlainMember_NeedsPermissions() { /* SetMember(Member) → !Passed, Problem NeedsPermissions, Message == strings "check.channelNotAdmin" */ }
[Fact] public async Task Group_Member_Passes() { /* "supergroup", Member → Passed, PhotosBlocked false */ }
[Fact] public async Task Group_RestrictedWithoutPhotos_PassesWithPhotosBlocked() { }
[Fact] public async Task Group_MemberWhenMembersCantSendPhotos_PassesWithPhotosBlocked() { /* AddChat(..., permissionsJson: {"can_send_messages":true,"can_send_photos":false}) */ }
[Fact] public async Task Group_BotLeft_IsLost() { }
[Fact] public async Task UnknownChat_IsLost() { /* default getChat answer: 400 chat not found → Problem Lost, Message "check.notFound" */ }
[Fact] public async Task PrivateChat_Passes_WithoutAMemberCheck() { /* AddChat(42,"private","Ann") → Passed, Kind Private, no getChatMember call */ }
[Fact] public async Task Topic_ThatNoLongerExists_IsTopicMissing() { /* threadId 7, On("sendChatAction", 400 "Bad Request: message thread not found") → Problem TopicMissing */ }
[Fact] public async Task Topic_ProbeIsATypingAction_NotAMessage() { /* passes; one sendChatAction with message_thread_id 7 and action "typing"; no sendMessage */ }
[Fact] public async Task TransportError_Throws_SoTheOldHealthIsKept() { /* On("getChat", throw HttpRequestException) → ThrowsAsync<RequestException> */ }
[Fact] public async Task NotConnected_ThrowsBotNotConnected() { }
```

(Write each body in full; the comments give the setup and the assertions. To make the fake throw a transport error, let `On(...)`'s callback throw `HttpRequestException`; Telegram.Bot wraps it as `RequestException`.)

`HealthServiceTests`:

```csharp
[Fact]
public void MarkProblem_NeverTouchesEnabled()
{
    using var h = new BotTestHarness();
    var d = h.AddDestination(-1001);
    var health = NewService(h);
    health.MarkProblem(d.Id, HealthStatus.Lost, "kicked");
    Assert.True(h.Settings.Current.Destinations.Single().Enabled);
    Assert.False(health.IsDeliverable(d.Id));
}

[Fact]
public async Task CheckNow_Recovers_AndRaisesRecovered() { /* MarkProblem Lost, then CheckNowAsync (admin fixture) → Status Ok, LastError null, Recovered raised once */ }

[Fact]
public async Task Unhealthy_IsRecheckedEvery30Minutes_ByTheLoop()
{
    // gate owned, Connect(), MarkProblem(Lost) at t0; start the service.
    // AdvanceUntilAsync(29 min worth of 1-minute steps) → no getChat call; AdvanceUntilAsync(…) past 30 min → one getChat call and Status Ok.
}

[Fact]
public async Task RequestCheckForChat_ChecksOnlyThatChatsDestinations() { /* two destinations; RequestCheckForChat(-1002) → only getChat for -1002 */ }

[Fact]
public async Task RequestCheckAll_ChecksEveryDestination() { }

[Fact]
public void ANewProblem_ResetsNotified() { /* MarkProblem Lost; set Notified=true via State.Update; MarkProblem NeedsPermissions → Notified false; MarkProblem NeedsPermissions again → stays true */ }

[Fact]
public async Task Loop_DoesNothingWhileDisconnected() { /* no Connect(); RequestCheckAll; advance 1 min → no calls */ }
```

- [ ] **Step 2: Run to verify they fail** — `--filter-namespace '*.Destinations'`. Expected: FAIL.
- [ ] **Step 3: Implement.** Pass rules (§6.2 step 2): a channel passes when the bot is the creator, or an administrator with `CanPostMessages`; a group or supergroup passes for creator, administrator, member, or restricted with `IsMember && CanSendMessages`; left or kicked → `Lost` (`check.lost`), other failures → `NeedsPermissions` (`check.channelNotAdmin` or `check.groupCantSend`). `PhotosBlocked`: restricted with `!CanSendPhotos`, or member with `chat.Permissions?.CanSendPhotos == false`. A private chat passes when `getChat` succeeds. A thread > 0 is probed with `SendTypingAsync` after the member check passes. The loop runs through `Supervisor.RunAsync("health", …)`: wait for the gate, then wake on a request signal or every 30 s; skip while `connection.State != Connected`; check the requested destinations and every not-OK or `PhotosBlocked` destination whose `NextCheckAt <= now`; a check that throws is logged, and that destination's `NextCheckAt` moves 1 minute ahead.
- [ ] **Step 4: Run all tests three times, then once on 2 CPUs** (real-time waits):
  Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet build Jellyfin.Plugin.TgJelly.slnx -c Release && ( set -e; for i in 1 2 3; do dotnet test --solution Jellyfin.Plugin.TgJelly.slnx -c Release --no-build; done; taskset -c 0,1 dotnet test --solution Jellyfin.Plugin.TgJelly.slnx -c Release --no-build )`
  Expected: PASS every run, `0 Warning(s)`.
- [ ] **Step 5: Commit** — `git commit -m "feat: connection holder, chat checks and destination health with re-checks (spec 9.4)"`

---

### Task 9: Outbox queue rules and pacing

The queue logic, pure over `QueueDocument` and a clock. Delivery comes in Task 10.

**Files:**
- Create (in `State/Documents/`): `OutboxOp.cs`, `OutboxButton.cs`, `OutboxMessage.cs`, `OutboxEntry.cs`, `SentKind.cs`, `SentMessage.cs`, `CachedFileId.cs`, `PublicUrlFlag.cs`; modify `QueueDocument.cs` (add `Outbox`, `SentMessages`, `FileIdCache`) and `StatusSection.cs` (add `PublicUrlFlag? PublicUrlFlag`)
- Create: `src/…/Delivery/DestinationView.cs`, `Delivery/OutboxPick.cs`, `Delivery/Outbox.cs`, `Delivery/PacingGate.cs`
- Test: `tests/…/Delivery/OutboxTests.cs`, `Delivery/PacingGateTests.cs`

**Interfaces:**
- Produces (documents; JSON camelCase via `JsonDefaults`):
  - `enum OutboxOp { Send, Edit }`; `OutboxButton { string Text; string Url }`.
  - `OutboxMessage { string Caption = ""; string Text = ""; string? PosterPath; string? PosterCacheKey; List<List<OutboxButton>> Buttons = []; bool Silent; bool ForceTextOnly; bool NoButtons; bool PlainText; bool Truncated }` — `Caption` goes with a poster (≤ 1024), `Text` is the text-only layout (≤ 4096); the fallback flags are set by delivery.
  - `OutboxEntry { string EntryId; string DestinationId; string EventKey; string GroupKey = ""; OutboxOp Op; string? TargetKey; string? ReplyToKey; string? SentKey; OutboxMessage Message; DateTimeOffset QueuedAt; int Attempts; int PhotoFailures; bool Migrated; DateTimeOffset? NotBefore; int Summary }` (`Summary > 0` marks the rolling summary and holds its count).
  - `enum SentKind { Photo, Text }`; `SentMessage { int MessageId; SentKind Kind; DateTimeOffset SentAt; DateTimeOffset? LastEditAt; string EventKey }`; `CachedFileId { string FileId; DateTimeOffset CachedAt }`; `PublicUrlFlag { string Url; DateTimeOffset At }`.
  - `QueueDocument.Outbox` (`List<OutboxEntry>`), `QueueDocument.SentMessages` (`Dictionary<string, SentMessage>`, key `"{destinationId}|{key}"`), `QueueDocument.FileIdCache` (`Dictionary<string, CachedFileId>`).
- Produces (logic):
  - `sealed record DestinationView(long ChatId, ChatKind Kind, bool Deliverable)`; `sealed record OutboxPick(OutboxEntry? Entry, DateTimeOffset? WakeAt)`.
  - `PacingGate(TimeProvider time)`: `DateTimeOffset NextAllowed(long chatId, bool groupOrChannel, DateTimeOffset now)`, `void RecordSend(long chatId, DateTimeOffset now)`, `void Pause(long chatId, DateTimeOffset until)`. In memory only.
  - `Outbox(StateStore state, StringTable strings, TimeProvider time)`:
    - `const int MediaCap = 500`, `const int AlertCap = 50`, `const int SentMessagesCap = 5000`, `const int FileIdCacheCap = 2000`, `static readonly TimeSpan Ttl = TimeSpan.FromHours(24)`, `static readonly TimeSpan FileIdTtl = TimeSpan.FromHours(24)`;
    - `void Enqueue(OutboxEntry entry)`; `OutboxPick Pick(IReadOnlyDictionary<string, DestinationView> destinations, PacingGate pacing, DateTimeOffset now)`;
    - `void Complete(OutboxEntry entry, SentMessage? sent)`, `void Retry(string entryId, DateTimeOffset notBefore, bool countAttempt)`, `void Replace(OutboxEntry entry)`, `void Drop(string entryId)`, `int PurgeExpired(DateTimeOffset now)`;
    - `void RemoveDestination(string destinationId)`, `void Repoint(string fromDestinationId, string toDestinationId)`;
    - `bool TryGetSent(string destinationId, string key, [NotNullWhen(true)] out SentMessage? sent)`;
    - `bool TryGetFileId(string cacheKey, DateTimeOffset now, [NotNullWhen(true)] out string? fileId)`, `void CacheFileId(string cacheKey, string fileId, DateTimeOffset now)`;
    - `int Count`, `IReadOnlyList<OutboxEntry> Snapshot()`, `event EventHandler? Changed`;
    - `static TimeSpan Backoff(int attempt, double random01)` — `min(2 s × 2^(attempt−1), 5 min) × (0.8 + 0.4 × random01)`.

- [ ] **Step 1: Write the failing tests**

```csharp
// PacingGateTests (FakeTimeProvider t0)
[Fact] public void SameChat_WaitsOneSecond() { /* RecordSend(1, t0) → NextAllowed(1, false, t0) == t0 + 1 s; another chat → t0 */ }
[Fact] public void GroupOrChannel_At20PerMinute_WaitsForTheWindow() { /* 20 RecordSend(-100, t0 + i s) → NextAllowed(-100, true, t0 + 20 s) == t0 + 60 s; a private chat with the same history → t0 + 21 s */ }
[Fact] public void Overall_25PerSecond() { /* 25 sends to 25 chats within one second → NextAllowed(999, false, t0 + 0.5 s) == first + 1 s */ }
[Fact] public void Pause_HoldsTheChatUntil() { /* Pause(1, t0 + 7 s) → NextAllowed(1, false, t0) == t0 + 7 s */ }

// OutboxTests (BotTestHarness gives State/Strings/Time)
[Theory]
[InlineData(1, 0.0, 1.6)] [InlineData(1, 1.0, 2.4)] [InlineData(3, 0.5, 8.0)] [InlineData(12, 0.0, 240)] [InlineData(12, 1.0, 360)]
public void Backoff_DoublesFrom2s_CapsAt5min_WithJitter(int attempt, double random, double seconds)
    => Assert.Equal(seconds, Outbox.Backoff(attempt, random).TotalSeconds, precision: 3);

[Fact]
public void MediaCap_DropsTheOldestMedia_AndCountsIt()
{
    // 501 media entries for one destination → Count == 500, the first entry's id is gone, Counters.DroppedOutbox == 1.
}

[Fact]
public void AlertCap_FoldsTheRestIntoOneRollingSummary_UpdatedInPlace()
{
    // 60 "security.login_failed" entries → 50 plain alerts + 1 entry with Summary == 10
    // whose Message.Text == Strings.Plural("outbox.summary", 10); no alert dropped (DroppedOutbox == 0).
}

[Fact]
public void Alerts_AreWrittenToQueueJsonWithinASecond()
{
    // Enqueue one security entry, then State.FlushDue() at the same instant → queue.json exists and contains its entryId.
}

[Fact]
public void Edit_ReplacesAQueuedEditForTheSameTarget()
{
    // two Edit entries with TargetKey "g1" for one destination → one queued entry, carrying the second one's text; Edits never count toward AlertCap.
}

[Fact] public void Pick_AlertsGoBeforeMedia_ThenOldestFirst() { }
[Fact] public void Pick_SkipsHeldDestinations() { /* Deliverable false → Entry null */ }
[Fact] public void Pick_RespectsNotBefore_AndReportsWhenToWake() { }
[Fact] public void Pick_WaitsForAnUnsentReplyTarget_ButNotForAMissingOne() { /* entry B ReplyToKey "k" while entry A with SentKey "k" is queued → A picked first, B waits; with no A queued and no sent "k" → B is pickable */ }
[Fact] public void Pick_RespectsPacing_AndReportsWhenToWake() { }
[Fact] public void PurgeExpired_DropsEntriesOlderThan24h_AndCountsThem() { }
[Fact] public void Complete_RecordsTheSentMessage_AndEvictsBeyond5000() { /* Evictions counter increments */ }
[Fact] public void FileIdCache_ExpiresAfter24h() { }
[Fact] public void RemoveDestination_DropsItsEntriesAndSentMessages_WithoutCounting() { }
```

(Fill in each body; the comments give setup and assertions.)

- [ ] **Step 2: Run to verify they fail** — `--filter-namespace '*.Delivery'`. Expected: FAIL.
- [ ] **Step 3: Implement.** `Enqueue`: alerts are entries whose `EventKey` group is `security` or `server`, that aren't edits, and that aren't the summary; they use `UpdateQueue(…, urgent: true)`. Every eviction or TTL drop increments `StatusCounters.DroppedOutbox` (queue entries) or `Evictions` (sent messages, file ids) through `StateStore.Update`. `Pick`: per destination, the first entry by (alert before media, `QueuedAt`) that isn't waiting on `NotBefore` or an unsent reply target; then the pacing check (`groupOrChannel` = kind is `Group`, `Supergroup` or `Channel`); across destinations, alerts first, then the oldest. `WakeAt` = the earliest moment a skipped entry could become pickable.
- [ ] **Step 4: Run all tests** — Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat: Outbox queue rules: caps, rolling summary, TTL, priority, pacing (spec 9.3)"`

---

### Task 10: Outbox delivery and the Outbox loop

**Files:**
- Create: `src/…/Delivery/OutboxDelivery.cs`, `Delivery/OutboxService.cs`
- Modify: `PluginServiceRegistrator.cs` (`Outbox`, `PacingGate`, `OutboxDelivery` singletons; `OutboxService` hosted, registered after `HealthService`)
- Test: `tests/…/Delivery/OutboxDeliveryTests.cs`, `Delivery/OutboxServiceTests.cs`

**Interfaces:**
- Consumes: `Outbox`, `PacingGate` (Task 9), `HealthService` (Task 8), `BotConnection`, `ITelegramSender`, `TelegramErrorClassifier`, `SettingsStore`, `StateStore`, `EventCatalog.GroupOf`.
- Produces:
  - `OutboxDelivery(BotConnection connection, Outbox outbox, HealthService health, SettingsStore settings, StateStore state, PacingGate pacing, TimeProvider time, ILogger<OutboxDelivery> logger)`, with an `internal Func<double> Random { get; set; } = System.Random.Shared.NextDouble` for tests; `Task DeliverAsync(OutboxEntry entry, Destination destination, CancellationToken ct)` — performs one delivery including immediate fallbacks, applies every outcome to the Outbox, health and state, and never throws except `OperationCanceledException`.
  - `OutboxService(InstanceGate gate, CompatResult compat, BotConnection connection, Outbox outbox, OutboxDelivery delivery, HealthService health, SettingsStore settings, PacingGate pacing, TimeProvider time, ILogger<OutboxService> logger) : BackgroundService`.

**Delivery rules** (one call to `DeliverAsync`; at most 4 immediate re-sends, each after a fallback changes the entry):

| Classified as (Task 6) | Action |
|---|---|
| success | `pacing.RecordSend`; `outbox.Complete(entry, sent)`; a fresh photo upload caches the largest size's `FileId` under `PosterCacheKey`; a send with buttons clears `status.publicUrlFlag` |
| `PhotosBlocked` | `health.SetPhotosBlocked`; `ForceTextOnly = true`; re-send |
| `Lost` / `NeedsPermissions` / `TopicMissing` | `health.MarkProblem(…)`; the entry stays queued (now held) |
| `Migrated` (only when `!entry.Migrated`) | rewrite the destination's `ChatId` through `SettingsStore`, or merge into the destination that already has the new chat (Events and SilentGroups unioned; LibraryIds unioned, or empty if either was empty, since empty means all; Preset kept when equal, else `Custom`), then `outbox.Repoint` and `health.Remove` for the merged-away one; `Migrated = true`; re-send |
| `ParseError` | `PlainText = true` → re-send with `ParseMode.None` and `HtmlText.ToPlain(...)` |
| `BadButton` | `NoButtons = true`; `status.publicUrlFlag = { Url = PublicUrl, At = now }`; re-send |
| `BadPhoto` | `ForceTextOnly = true`; re-send |
| `TooLong` (only when `!Truncated`) | `Truncated = true`; `HtmlText.Truncate` to 90 % of the limit actually used (921 for a caption, 3686 for text); re-send |
| `EditTargetGone` | turn the edit into a send; re-send |
| `NotModified` | success; the target's `LastEditAt = now` |
| `RateLimited` | `pacing.Pause(chat, now + RetryAfter)`; `outbox.Retry(…, now + RetryAfter, countAttempt: false)` |
| `Transport` | a photo **upload** failure increments `PhotoFailures`, and the second one sets `ForceTextOnly`; `outbox.Retry(…, now + Backoff(attempts + 1, Random()), countAttempt: true)` |
| `ServerError` | as `Transport`, without the photo rule |
| `Other`, or a fallback that has already been used | log; `outbox.Drop`; `state.RecordError("outbox", "<destination title>: <description>")` |

Photo or text: a photo is used when `!ForceTextOnly`, `PosterPath` is set, the destination isn't `PhotosBlocked`, and the file can be opened; otherwise the text layout is sent (`Text`). A cached file id (`TryGetFileId`) is sent as `InputFile.FromFileId`; otherwise the file is uploaded with `InputFile.FromStream(stream, "poster" + extension)` and the stream is disposed afterwards. Edits resolve `TargetKey` with `TryGetSent`: `Photo` → `EditCaptionAsync(Caption)`, `Text` → `EditTextAsync(Text)`; an unresolvable target is sent as a new message. `ReplyToKey` resolves to `ReplyToMessageId` when sent. Buttons → `InlineKeyboardMarkup` of URL buttons, unless `NoButtons`.

**Loop** (`Supervisor.RunAsync("outbox", …)`): wait for the gate; if `!compat.IsCompatible`, return without ever sending; then repeat: while not `Connected`, wait for `connection.Changed` (or 5 s); `PurgeExpired`; build the `DestinationView` map from `settings.Current` (`Deliverable = Enabled && health.IsDeliverable(id)`); `Pick`; deliver the entry, or wait for `outbox.Changed`, `health.Recovered`, `connection.Changed`, `WakeAt` or 30 s, whichever comes first. Entries whose destination no longer exists are dropped.

- [ ] **Step 1: Write the failing tests** (`OutboxDeliveryTests` use `BotTestHarness` + a real `Outbox`/`HealthService`/`PacingGate` and call `DeliverAsync` directly):

```csharp
[Fact] public async Task Text_IsSentAsHtml_AndRecordedUnderItsSentKey() { }
[Fact] public async Task Poster_IsUploaded_ThenItsFileIdIsReusedForTheNextDestination() { /* write a 3-byte poster.jpg in the temp dir; first call's photo is "attach:poster.jpg", second destination's photo is "file-1" */ }
[Fact] public async Task MissingPosterFile_SendsTheTextLayout() { }
[Fact] public async Task Edit_UsesCaptionForPhotosAndTextForTextMessages() { }
[Fact] public async Task Edit_WithAnUnknownTarget_IsSentAsANewMessage() { }
[Fact] public async Task NotModified_CountsAsSuccess() { }
[Fact] public async Task PhotosBlocked_ResendsAsText_AndFlagsTheDestination() { }
[Fact] public async Task Lost_HoldsTheEntry_MarksHealth_AndLeavesEnabledAlone() { }
[Fact] public async Task Migration_RewritesTheChatId_AndResendsOnce() { }
[Fact] public async Task Migration_IntoAnExistingDestination_MergesAndRepoints() { }
[Fact] public async Task ParseError_ResendsAsPlainText() { }
[Fact] public async Task BadButton_ResendsWithoutButtons_AndFlagsThePublicUrl() { }
[Fact] public async Task ASuccessfulSendWithButtons_ClearsThePublicUrlFlag() { }
[Fact] public async Task TooLong_TruncatesTo90Percent_AndResendsOnlyOnce() { }
[Fact] public async Task RateLimited_PausesTheChat_WithoutCountingAnAttempt() { }
[Fact] public async Task Transport_RetriesWithBackoff() { /* Random = () => 0.5 → NotBefore == now + 2 s, Attempts 1 */ }
[Fact] public async Task PhotoUploadFailingTwice_FallsBackToText() { }
[Fact] public async Task ServerError_Retries_ButAnUnknown400_IsDroppedAndRecorded() { }
```

`OutboxServiceTests` (the service started on an owned gate, `Connect()`ed harness):

```csharp
[Fact] public async Task QueuedEntries_AreDelivered_InPickOrder() { }
[Fact] public async Task HeldEntries_WaitUntilTheDestinationRecovers() { /* MarkProblem Lost → no send; CheckNowAsync passes → Recovered → sent */ }
[Fact] public async Task PausedDestination_IsHeld() { }
[Fact] public async Task Incompatible_NeverSends() { }
[Fact] public async Task Disconnected_SendsNothing_UntilConnected() { }
```

(Fill in each body from the rules table; assert on `Api.CallsTo(...)`, `outbox.Snapshot()`, `health.Get(...)`, `State.LastErrorsSnapshot()`.)

- [ ] **Step 2: Run to verify they fail** — `--filter-namespace '*.Delivery'`. Expected: FAIL, `OutboxDelivery`/`OutboxService` don't exist.
- [ ] **Step 3: Implement** per the rules table and the loop description.
- [ ] **Step 4: Run all tests three times, then once on 2 CPUs** — the Task 8 Step 4 command. Expected: PASS every run.
- [ ] **Step 5: Commit** — `git commit -m "feat: Outbox delivery with every 9.4 fallback, file_id cache, and the Outbox loop"`

---
### Task 11: Bot supervisor and the poll loop

**Files:**
- Create: `src/…/State/Documents/TelegramSection.cs`, `State/Documents/ProfileState.cs`; modify `StateDocument.cs` (add `TelegramSection Telegram`)
- Create: `src/…/Bot/IServerStartup.cs`, `Bot/ServerStartup.cs`, `Bot/IUpdateHandler.cs`, `Bot/BasicUpdateHandler.cs`, `Bot/ProfileCalls.cs`, `Bot/ISupervisorControl.cs`, `Bot/BotSupervisor.cs`
- Modify: `PluginServiceRegistrator.cs` (`IServerStartup`, `IUpdateHandler`, `ProfileCalls` singletons; `BotSupervisor` singleton, also as `ISupervisorControl` and as a hosted service registered **after `PersistenceService` and before `HealthService`**, so it stops after the Outbox and health loops and before persistence flushes)
- Test: `tests/…/Bot/BotSupervisorTests.cs`, `Bot/ProfileCallsTests.cs`, `Bot/BasicUpdateHandlerTests.cs`, extend `PluginServiceRegistratorTests.cs` (hosted-service order)

**Interfaces:**
- Consumes: `InstanceGate`, `CompatResult`, `SecretsStore`, `StateStore`, `ITelegramClientFactory` (Task 4), `TelegramSender` (Task 5), `TelegramErrorClassifier.ClassifyPoll` (Task 6), `BotConnection`, `HealthService` (Task 8), `StringTable`.
- Produces:
  - `TelegramSection { long? BotId; string BotUsername = ""; ProfileState Profile = new() }`; `ProfileState { bool SetProfile; List<string> PendingCalls = [] }`.
  - `interface IServerStartup { bool HasCompleted { get; } }`; `ServerStartup(IServerApplicationHost host)` returns `host.CoreStartupHasCompleted`.
  - `interface IUpdateHandler { Task HandleAsync(Update update, CancellationToken ct); }`; `BasicUpdateHandler(BotConnection connection, HealthService health, ILogger<BasicUpdateHandler> logger)`: a `CallbackQuery` is answered at once (a "query is too old" error is ignored); `MyChatMember` → `health.RequestCheckForChat(chat id)`; everything else is ignored until plan 4's `UpdateRouter` replaces this class.
  - `ProfileCalls(StateStore state, StringTable strings, IServerApplicationHost host, ILogger<ProfileCalls> logger)`: `static readonly string[] AllCalls = ["commands", "descriptions", "adminRights"]`; `Task RunPendingAsync(ITelegramSender sender, CancellationToken ct)` runs each pending call (`commands` → `SetCommandsAsync` with `menu`, `status`, `test`, `help` and their `command.*` strings; `descriptions` → `SetDescriptionsAsync(profile.description, profile.shortDescription)` with `{server}` = `host.FriendlyName`; `adminRights` → `SetChannelAdminRightsAsync`), removes each one that succeeds, and records a failure with `state.RecordError("profile", …)` while keeping it pending. Does nothing when `SetProfile` is false.
  - `interface ISupervisorControl { void Restart(); }`.
  - `BotSupervisor(InstanceGate gate, CompatResult compat, IServerStartup startup, SecretsStore secrets, StateStore state, ITelegramClientFactory factory, BotConnection connection, HealthService health, IUpdateHandler handler, ProfileCalls profile, StringTable strings, TimeProvider time, ILogger<BotSupervisor> logger) : BackgroundService, ISupervisorControl`; `static readonly UpdateType[] AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery, UpdateType.MyChatMember]`; `void Restart()`; `internal int? LastUpdateId`.

**Session** (one per start and per `Restart()`; the next session starts only after the previous one has fully finished, including its in-flight `GetUpdates`):
1. `Incompatible` when `!compat.IsCompatible` (stays until Jellyfin restarts). `WaitingForInstance` until the gate is owned. `WaitingForStartup` until `startup.HasCompleted`, checked every 1 s. `NoToken` while the stored token is empty; wait for `Restart()`.
2. `Connecting`. `factory.Create(token)`: `ArgumentException` `"token"` → `TokenRejected` with `token.format`; `"baseUrl"` → `InvalidBaseUrl` plus `state.RecordError("bot", …)`; wait for `Restart()`.
3. `GetMeAsync`: 401 → `TokenRejected`, wait for `Restart()`; transport → `Unreachable`, backoff 1 s → 60 s, retry. Success: store `telegram.botId`/`botUsername` if they changed, `connection.Attach(sender, me.Id, me.Username)`.
4. `GetWebhookInfoAsync`: a URL is set → `WebhookConflict`; check again every 5 min; never delete it here.
5. `health.RequestCheckAll()` (once per session), and `profile.RunPendingAsync(sender, sessionToken)` on a background task whose exceptions are logged.
6. The poll loop from §9.2 step 3: `GetUpdates(offset: LastUpdateId + 1 or null, limit: 100, timeout: 50, allowedUpdates: AllowedUpdates)`; success sets `Connected` and resets the backoff; each update goes to `handler.HandleAsync` inside a catch-all (logged and skipped); `LastUpdateId = update.Id` after each.
7. Errors from `ClassifyPoll`: `TokenRejected` → state, wait for `Restart()`; `ConflictingInstance` → state, backoff 30 s doubling to 5 min; `WebhookConflict` → back to step 4; `RateLimited` → wait `RetryAfter`; `ApiError` → backoff 1 s → 60 s; `Unreachable` → state, backoff 1 s → 60 s.
- Every wait uses the injected `TimeProvider` and the session token.
- **Stop:** `StopAsync` cancels the session, waits for it, then makes one best-effort `GetUpdates(offset: LastUpdateId + 1, limit: 1, timeout: 0)` under its own 2 s `CancellationTokenSource` (skipped when `LastUpdateId` is null), and never throws.
- `LastUpdateId` lives only in memory and is reset with each session (§9.2).

- [ ] **Step 1: Write the failing tests** (`BotSupervisorTests`; a fake `ITelegramClientFactory` returns `Api.CreateClient()`; `IServerStartup` is a settable fake; the supervisor runs on an owned gate):

```csharp
[Fact] public async Task NoToken_StaysNoToken_AndNeverCallsTelegram() { }
[Fact] public async Task Incompatible_NeverCallsTelegram() { }
[Fact] public async Task WaitsForJellyfinStartup() { /* HasCompleted false → WaitingForStartup, no calls; set true → Connected */ }

[Fact]
public async Task FirstPoll_HasNoOffset_LaterPollsConfirmThePreviousBatch()
{
    // getUpdates answers [update 10, update 11], then [] forever.
    // calls[0] has no "offset"; calls[1].offset == 12; handler saw ids 10 and 11 in order.
}

[Fact]
public async Task Poll_UsesLimit100_Timeout50_AndTheThreeAllowedUpdates()
{
    // first getUpdates body: limit 100, timeout 50, allowed_updates == ["message","callback_query","my_chat_member"]
}

[Fact] public async Task TokenRejected_StopsPolling_UntilRestart() { /* getMe 401 → TokenRejected; advance 10 min → still one getMe call */ }
[Fact] public async Task WebhookSet_IsAConflict_AndIsNeverDeleted() { /* no deleteWebhook call; after 5 min a second getWebhookInfo */ }
[Fact] public async Task ConflictingInstance_BacksOffFrom30Seconds() { /* 409 terminated by other getUpdates → state; no new poll before +30 s; one after */ }
[Fact] public async Task TransportErrors_ShowUnreachable_ThenReconnect() { /* getUpdates throws HttpRequestException 3 times → Unreachable; then [] → Connected */ }
[Fact] public async Task RateLimited_WaitsRetryAfter() { }
[Fact] public async Task AThrowingHandler_SkipsTheUpdate_AndPollingContinues() { }
[Fact] public async Task ARestart_StartsWithoutAnOffset_SoTheLastBatchMayArriveAgain() { /* the handler must tolerate it: plan 4's tests cover handler idempotency */ }
[Fact] public async Task SessionStart_RequestsHealthChecks_AndRunsPendingProfileCalls() { }

[Fact]
public async Task Restart_DuringALongPoll_NeverPollsTwiceAtOnce()
{
    // getUpdates blocks until its CancellationToken fires, tracking how many calls are in flight (Interlocked).
    // Call Restart() five times, 50 ms apart; then let polls return [].
    // Assert: the maximum in-flight count was 1, and every blocked call observed cancellation.
}

[Fact] public async Task Stop_ConfirmsTheLastBatch() { /* after ids 10–11: StopAsync → final getUpdates with offset 12, limit 1, timeout 0 */ }

[Fact]
public async Task Stop_WhileTelegramHangs_ReturnsQuickly_AndDoesNotThrow()
{
    // every getUpdates hangs (await Task.Delay(Timeout.Infinite, ct)) after one batch was handled.
    // var watch = Stopwatch.StartNew(); await supervisor.StopAsync(CancellationToken.None);
    // Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5)); (2 s confirmation timeout + slack)
}
```

`ProfileCallsTests`: `AllPending_RunInOrder_AndAreCleared`, `AFailingCall_StaysPending_AndIsRecorded`, `SetProfileOff_RunsNothing`, `Descriptions_UseTheServerName`. `BasicUpdateHandlerTests`: `CallbackQuery_IsAnswered`, `TooOldCallback_IsIgnored`, `MyChatMember_RequestsACheckForThatChat`. Registrator test: the hosted services resolve in the order `InstanceGate`, `PersistenceService`, `BotSupervisor`, `HealthService`, `OutboxService`.

- [ ] **Step 2: Run to verify they fail** — `--filter-namespace '*.Bot'`. Expected: FAIL.
- [ ] **Step 3: Implement** per the session description. Use one `CancellationTokenSource` per session, linked to the stopping token; `Restart()` cancels it under a lock, and the outer loop awaits the session task before creating the next.
- [ ] **Step 4: Run all tests three times, then once on 2 CPUs** — the Task 8 Step 4 command. Expected: PASS every run.
- [ ] **Step 5: Commit** — `git commit -m "feat: bot supervisor with the own GetUpdates loop, connection states and profile calls (spec 9.2)"`

---

### Task 12: Token save, bot switch, disconnect and webhook take-over

**Files:**
- Create: `src/…/Bot/TokenSaveOutcome.cs`, `Bot/TokenSaveResult.cs`, `Bot/BotStateReset.cs`, `Bot/BotTokenService.cs`
- Modify: `PluginServiceRegistrator.cs`
- Test: `tests/…/Bot/BotTokenServiceTests.cs`, `Bot/BotStateResetTests.cs`

**Interfaces:**
- Consumes: `ITelegramClientFactory`, `TelegramSender`, `TelegramErrorClassifier`, `SecretsStore`, `StateStore`, `SettingsStore`, `ISupervisorControl` (Task 11), `StringTable`.
- Produces:
  - `enum TokenSaveOutcome { Connected, NeedsConfirm, FormatInvalid, Rejected, Unreachable, Failed }`; `sealed record TokenSaveResult(TokenSaveOutcome Outcome, string Message, string? BotUsername = null, bool WebhookDeleted = false)`.
  - `BotStateReset.ForDifferentBot(StateDocument state, QueueDocument queue, IEnumerable<string> destinationIds, string lostMessage, DateTimeOffset now)`: clears `queue.SentMessages`, `queue.FileIdCache` and `state.Telegram.Profile`, and sets every listed destination's health to `Lost` with `LastError = lostMessage`, `At = now`, `Notified = false`, `PhotosBlocked = false`. `BotStateReset.ForDisconnect(StateDocument state, QueueDocument queue)`: clears `state.Health`, `queue.SentMessages`, `queue.Outbox`, `queue.FileIdCache` and `state.Telegram.Profile`; keeps `telegram.botId`/`botUsername`. Plan 4 adds `flows`, `managers`, `detectedChats` and `setupFailures` to both.
  - `BotTokenService(ITelegramClientFactory factory, SecretsStore secrets, StateStore state, SettingsStore settings, ISupervisorControl supervisor, StringTable strings, TimeProvider time, ILogger<BotTokenService> logger)`:
    - `Task<TokenSaveResult> SaveAsync(string token, bool setProfile, bool confirmSwitch, CancellationToken ct)` — §6.1 steps 1–5; one save at a time (`SemaphoreSlim`);
    - `void Disconnect()` — §10.4 "Disconnect bot": clears `botToken` (proxy credentials kept), applies `ForDisconnect`, `supervisor.Restart()`;
    - `Task<bool> TakeOverWebhookAsync(CancellationToken ct)` — with the stored token: `DeleteWebhookAsync`, then `supervisor.Restart()`; `false` when no token is stored.

**SaveAsync, in order:** trim the token; `factory.Create` → `FormatInvalid` (`token.format`) on `ArgumentException` `"token"`, `Failed` on `"baseUrl"`; `GetMeAsync` → `Rejected` (`token.rejected`) on 401, `Unreachable` (`token.unreachable`) on a transport error, `Failed` (`token.failed`) otherwise. If `telegram.botId` is set and differs from `me.Id`: when destinations exist and `!confirmSwitch`, return `NeedsConfirm` with `Strings.Plural("token.switch", count, ("bot", me.Username))` and change nothing; otherwise apply `ForDifferentBot` with `health.botChanged`. Then `GetWebhookInfoAsync`: when a URL is set, `DeleteWebhookAsync` and report `WebhookDeleted` (the only automatic webhook deletion, §9.2). Then store the token in `secrets.json`, set `telegram.botId`/`botUsername`, set `profile = { SetProfile = setProfile, PendingCalls = setProfile ? AllCalls : [] }`, call `supervisor.Restart()`, and return `Connected` with the username. Nothing is stored before the last step.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task BadFormat_IsFormatInvalid_AndStoresNothing() { }
[Fact] public async Task Http401_IsRejected_AndStoresNothing() { }
[Fact] public async Task Unreachable_ReturnsUnreachable_AndStoresNothing() { /* getMe throws HttpRequestException → Outcome Unreachable; secrets empty; no Restart */ }

[Fact]
public async Task FirstToken_StoresTokenAndBot_QueuesProfileCalls_AndRestartsTheSupervisor()
{
    // Outcome Connected, BotUsername "tgjelly_test_bot"; Secrets.Load().BotToken == token; telegram.botId == 123456789;
    // profile.PendingCalls == ProfileCalls.AllCalls; supervisor fake saw one Restart().
}

[Fact] public async Task SetProfileOff_QueuesNoProfileCalls() { }
[Fact] public async Task SameBot_KeepsHealthSentMessagesAndTheOutbox() { }
[Fact] public async Task DifferentBot_WithDestinations_NeedsConfirm_AndChangesNothing() { /* message: "@other_bot is a different bot. It isn't in your 2 destinations, so they will need to be added again." */ }
[Fact] public async Task DifferentBot_Confirmed_ResetsBotScopedState_AndMarksEveryDestinationLost() { /* settings, outbox entries and pendingMedia-like sections untouched; health Lost with "bot changed: add @other_bot to this chat" */ }
[Fact] public async Task DifferentBot_WithoutDestinations_SwitchesWithoutAsking() { }
[Fact] public async Task AWebhook_IsDeletedOnSave_AndReported() { }
[Fact] public async Task Disconnect_ClearsTheTokenAndBotScopedState_KeepsTheBotIdentityAndProxyCredentials() { }
[Fact] public async Task ReconnectingTheSameBotAfterDisconnect_IsNotASwitch() { }
[Fact] public async Task TakeOverWebhook_DeletesItAndRestarts() { }
[Fact] public async Task ConcurrentSaves_RunOneAtATime() { }

[Fact]
public async Task NothingReturnedOrLogged_ContainsTheToken()
{
    // A capturing logger provider behind RedactingLogger<BotTokenService>; getMe answers 500 with the token in its description.
    // Assert the result's Message and every captured log line and exception text lack the token.
}
```

`BotStateResetTests`: one test per method, asserting exactly the §10.4 lists of cleared and kept sections.

- [ ] **Step 2: Run to verify they fail** — `--filter-class '*.BotTokenServiceTests'` and `'*.BotStateResetTests'`. Expected: FAIL.
- [ ] **Step 3: Implement** per the Interfaces block.
- [ ] **Step 4: Run all tests** — Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat: token save with bot-switch reset, disconnect and webhook take-over (spec 6.1, 10.4)"`

---

### Task 13: API: Status, Token, Webhook, Settings and Proxy

**Files:**
- Modify: `src/…/Api/StatusDto.cs`, `Api/TgJellyController.cs`
- Create (in `Api/`): `BotController.cs`, `SettingsController.cs`, `TokenRequest.cs`, `TokenResponse.cs`, `SettingsDto.cs`, `SettingsUpdateRequest.cs`, `ProxyRequest.cs`, `ErrorsDto.cs`, `ConflictDto.cs`, `MessageDto.cs`
- Test: `tests/…/Api/BotControllerTests.cs`, `Api/SettingsControllerTests.cs`, extend `Api/TgJellyControllerTests.cs`

**Interfaces:**
- Every controller: `[ApiController] [Route("TgJelly")] [Authorize(Policy = Policies.RequiresElevation)] [Produces(MediaTypeNames.Application.Json)]`, `public`.
- `TgJellyController` gains the constructor parameters `BotConnection`, `Outbox`, `HealthService` and `StringTable`.
- `StatusDto` gains: `ConnectionState` (string, the enum name), `ConnectionMessage` (`connection.<State>`, `{bot}` filled; the gate's own message while `WaitingForInstance`; the compat message while `Incompatible`), `BotUsername`, `HasToken`, `Counters` (`StatusCounters`), `OutboxCount`, `UnhealthyDestinations` (destinations whose health isn't OK). Never the token.
- `ErrorsDto { IReadOnlyList<SettingsError> Errors }` (400); `ConflictDto<T> { T Current }` (409); `MessageDto { string Message }`.
- `BotController(BotTokenService tokens)`:
  - `POST Token` (`TokenRequest { [Required] string Token; bool SetProfile = true; bool ConfirmSwitch }`) → 200 `TokenResponse { string Outcome; string Message; string? BotUsername; bool WebhookDeleted }` for every outcome.
  - `DELETE Token` → 204.
  - `POST Webhook/TakeOver` → 204, or 409 `MessageDto` (`destination.notConnected`) when no token is stored.
- `SettingsController(SettingsStore settings, SecretsStore secrets, ISupervisorControl supervisor)`:
  - `GET Settings` → `SettingsDto { long Revision; string PublicUrl; bool PublicUrlValid; MessageOptions Message; BatchingOptions Batching; UpdateCheckOptions UpdateCheck; string ProxyMode; string ProxyUrl; bool HasProxyUsername; bool HasProxyPassword }`.
  - `PUT Settings` (`SettingsUpdateRequest { long ExpectedRevision; [Required] string PublicUrl; [Required] MessageOptions Message; [Required] BatchingOptions Batching; [Required] UpdateCheckOptions UpdateCheck }`) → 200 `SettingsDto` | 400 `ErrorsDto` | 409 `ConflictDto<SettingsDto>`. Proxy fields are not part of the request (§11.2).
  - `PUT Proxy` (`ProxyRequest { long ExpectedRevision; [Required] string Mode; string Url = ""; string? Username; string? Password }`) → same responses. An unknown mode, or an `Http`/`Socks5` mode whose URL doesn't parse as `host:port` (after adding the scheme), is a 400 on `Proxy.Mode`/`Proxy.Url`. An omitted `Username`/`Password` keeps the stored value, and `""` clears it (§11.2). Calls `supervisor.Restart()` when anything changed.

- [ ] **Step 1: Write the failing tests** (controllers constructed directly over `BotTestHarness` services; a fake `ISupervisorControl` counts restarts):

```csharp
// TgJellyControllerTests (added)
[Fact] public void Status_ShowsConnectionState_BotUsername_AndCounts() { /* Connect() → ConnectionState "Connected", ConnectionMessage "Connected as @tgjelly_test_bot", OutboxCount, UnhealthyDestinations 1 after MarkProblem */ }
[Fact] public void Status_NeverContainsTheToken() { /* store a token; serialize the DTO with System.Text.Json; Assert.DoesNotContain(token, json) */ }

// BotControllerTests
[Fact] public async Task PostToken_ReturnsTheOutcomeAs200() { /* NeedsConfirm and Rejected both 200 with Outcome names */ }
[Fact] public void DeleteToken_Returns204_AndDisconnects() { }
[Fact] public async Task TakeOver_WithoutAToken_Is409() { }

// SettingsControllerTests
[Fact] public void Get_ReturnsSettings_ProxyForDisplay_AndPublicUrlValidity() { }
[Fact] public void Put_SavesTheSections_AndBumpsTheRevision() { }
[Fact] public void Put_OutOfRange_Is400_WithFieldErrors() { /* Batching.DigestThreshold = 1 → Errors[0].Field "Batching.DigestThreshold" */ }
[Fact] public void Put_StaleRevision_Is409_WithCurrentData() { }
[Fact] public void Put_IgnoresProxy() { }
[Fact] public void PutProxy_SavesModeAndUrl_KeepsOmittedCredentials_ClearsEmptyOnes_AndRestarts() { }
[Fact] public void PutProxy_Socks5WithoutAUrl_Is400() { }
[Fact] public void PutProxy_Unchanged_DoesNotRestart() { }
```

- [ ] **Step 2: Run to verify they fail** — `--filter-namespace '*.Api'`. Expected: FAIL.
- [ ] **Step 3: Implement** per the Interfaces block. `PUT Settings` and `PUT Proxy` use `SettingsStore.Update(request.ExpectedRevision, …)` and map `Saved` → 200, `Invalid` → 400, `Conflict` → 409 with the current data.
- [ ] **Step 4: Run all tests** — Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat: API for status, token, webhook take-over, settings and proxy (spec 11.2)"`

---

### Task 14: API: Destinations, test sends, checks and links

**Files:**
- Create: `src/…/Destinations/TestSendResult.cs`, `Destinations/DestinationTester.cs`
- Create (in `Api/`): `DestinationsController.cs`, `DestinationsDto.cs`, `DestinationDto.cs`, `HealthDto.cs`, `AddDestinationRequest.cs`, `AddDestinationResponse.cs`, `UpdateDestinationRequest.cs`, `TestResultDto.cs`, `CheckResultDto.cs`, `LinksDto.cs`
- Modify: `PluginServiceRegistrator.cs`
- Test: `tests/…/Destinations/DestinationTesterTests.cs`, `Api/DestinationsControllerTests.cs`

**Interfaces:**
- Consumes: `DestinationChecker`, `HealthService` (Task 8), `Outbox` (Task 9), `BotConnection`, `EventCatalog`, `DestinationIds`, `TopicLink` (Task 7), `SettingsStore` (incl. the `Func<…, bool>` overload).
- Produces:
  - `sealed record TestSendResult(bool Sent, string? Error, DestinationHealth Health)`; `DestinationTester(BotConnection connection, HealthService health, StringTable strings, ILogger<DestinationTester> logger)`: `Task<TestSendResult> SendTestAsync(Destination destination, CancellationToken ct)` — re-checks (`health.CheckNowAsync`, bypassing the hold), then, unless the destination is `Lost`, `NeedsPermissions` or `TopicMissing`, sends `test.message` with HTML to the chat and thread. `{groups}` = the destination's event groups in catalog order, each as `groups.<name>`, joined with `groups.separator` and HTML-escaped, or `groups.none`. A send error is classified, and health-type errors go to `health.MarkProblem`.
  - `DestinationsController(SettingsStore settings, BotConnection connection, DestinationChecker checker, HealthService health, DestinationTester tester, Outbox outbox, StringTable strings, ILogger<DestinationsController> logger)`:
    - `GET Destinations` → `DestinationsDto { long Revision; IReadOnlyList<DestinationDto> Items }`; `DestinationDto { Id, ChatId, ThreadId, Title, Kind, Enabled, Preset, Events, LibraryIds, SilentGroups, HealthDto Health }`; `HealthDto { string Status; bool PhotosBlocked; string? LastError; DateTimeOffset? At }`.
    - `POST Destinations` (`AddDestinationRequest { long ExpectedRevision; long? ChatId; string? TopicLink; string Preset = "Everything" }`) → 200 `AddDestinationResponse { string Outcome /* Added | AlreadyExists */; DestinationDto Destination; bool TestSent; string? TestError }` | 400 `ErrorsDto` | 409 `ConflictDto<DestinationsDto>`.
    - `PUT Destinations/{id}` (`UpdateDestinationRequest { long ExpectedRevision; string? Preset; string[]? Events; string[]? LibraryIds; string[]? SilentGroups; bool? Enabled }`) → 200 `DestinationDto` | 400 | 404 | 409.
    - `DELETE Destinations/{id}?leave=bool&expectedRevision=n` → 204 | 404 | 409.
    - `POST Destinations/{id}/Test` → 200 `TestResultDto { bool Sent; string? Error; HealthDto Health }` | 404.
    - `POST Destinations/{id}/Check` → 200 `CheckResultDto { HealthDto Health; string? Error }` | 404.
    - `GET Links` → `LinksDto { OpenBot = "https://t.me/<bot>?start=setup", StartAdd = "…?start=add", StartGroup = "…?startgroup=add", StartChannel = "…?startchannel&admin=post_messages+edit_messages" }`, all `""` while no bot username is known.

**POST Destinations, in order:**
1. Not connected → 400 (`Bot`, `destination.notConnected`).
2. Exactly one of `ChatId`/`TopicLink`, else 400. A link that doesn't parse → 400 (`TopicLink`, `destination.badLink`). An unknown preset → 400.
3. `checker.CheckAsync(chat, thread)`. A failed check → 400 (`ChatId`, the check's message). An exception → 400 (`ChatId`, `token.unreachable`).
4. Duplicate (§6.2): an existing destination with the same (resolved chat ID, thread) → `health.CheckNowAsync(existing)` and 200 `AlreadyExists`.
5. `settings.Update(request.ExpectedRevision, c => …)` adds `Destination { Id = DestinationIds.New(…), ChatId, ThreadId, Title, Kind, Preset, Events = EventCatalog.Expand(preset) }`, returning `false` when the chat already exists by then.
   - `Unchanged` → `AlreadyExists`.
   - `Conflict` → re-read: if the chat is a destination now, `AlreadyExists`; otherwise 409.
   - `Saved` → `health.SetChecked(newId, check)`, then `tester.SendTestAsync` (the owner's automatic test message), then 200 `Added`.

**PUT rules:**
- `Events` given → `Preset = Custom` with those events (every key must be `EventCatalog.IsKnown`).
- `Preset` given without `Events` → events expanded (`Custom` keeps the current events).
- `SilentGroups` must be catalog groups.
- `LibraryIds` must parse as Guids and are stored in `N` format.
- `Enabled` never touches health.

**DELETE:**
- Removes the destination, then `outbox.RemoveDestination` and `health.Remove`.
- `leave=true` → `LeaveChatAsync`, best effort (logged), and only if no other destination uses the same chat (another topic).

- [ ] **Step 1: Write the failing tests**

```csharp
// DestinationTesterTests
[Fact] public async Task Test_SendsTheGroupsAsItalicText() { /* AdminAlerts → text "✅ TgJelly test: this chat will receive <i>security alerts, server alerts</i>", parse_mode HTML */ }
[Fact] public async Task Test_WithNoEvents_SaysNothingYet() { }
[Fact] public async Task Test_RechecksFirst_SoAHeldDestinationRecoversAndReceivesIt() { }
[Fact] public async Task Test_ToALostChat_ReportsTheProblem_AndSendsNothing() { }

// DestinationsControllerTests
[Fact] public async Task AddByChatId_ChecksSavesAndSendsTheTestMessage() { }
[Fact] public async Task AddByTopicLink_UsesTheThread() { /* "https://t.me/c/1234567890/42" → ChatId -1001234567890, ThreadId 42; probe and test message both carry message_thread_id 42 */ }
[Fact] public async Task AddByUsernameLink_StoresTheResolvedNumericChatId() { }
[Fact] public async Task Add_WhenTheCheckFails_Is400_AndSavesNothing() { }
[Fact] public async Task Add_WhenNotConnected_Is400() { }
[Fact] public async Task Add_AnExistingChat_IsAlreadyExists_AndRechecks() { }
[Fact] public async Task Add_WithAStaleRevision_Is409_WithCurrentData() { }

[Fact]
public async Task TheSameChatAddedTwiceAtOnce_CreatesOneDestination()
{
    // Both requests carry the same ExpectedRevision; getChatMember answers after a 100 ms delay so both checks overlap.
    // var results = await Task.WhenAll(controller.Add(req, ct), controller.Add(req, ct));
    // Assert one "Added" and one "AlreadyExists"; Settings.Current.Destinations has one entry; one test message was sent.
}

[Fact] public void Put_PresetExpandsEvents() { }
[Fact] public void Put_EventsMakeItCustom() { }
[Fact] public void Put_UnknownEventKey_Is400() { }
[Fact] public void Put_UnknownId_Is404() { }
[Fact] public void Put_DisablingDoesNotTouchHealth() { }
[Fact] public async Task Delete_RemovesTheDestination_ItsQueuedEntries_AndItsHealth() { }
[Fact] public async Task Delete_WithLeave_LeavesTheChat() { }
[Fact] public async Task Delete_WithLeave_StaysWhenAnotherTopicStillUsesTheChat() { }
[Fact] public void Links_UseTheBotUsername_AndAreEmptyWithoutOne() { }
[Fact] public void Get_IncludesHealth() { }
```

- [ ] **Step 2: Run to verify they fail** — `--filter-class '*.DestinationsControllerTests'` and `'*.DestinationTesterTests'`. Expected: FAIL.
- [ ] **Step 3: Implement** per the Interfaces block and the rules above. Add the `DestinationTester` registration and its redacted logger.
- [ ] **Step 4: Run all tests three times, then once on 2 CPUs** — the Task 8 Step 4 command. Expected: PASS every run, `0 Warning(s)`.
- [ ] **Step 5: Commit** — `git commit -m "feat: destinations API with add by ID or topic link, automatic test message, checks and links"`

---

### Task 15: CI end to end: stub Bot API, smoke steps, F8 and Q7

The roadmap's deliverable, proved inside real Jellyfin: connect a bot, add a chat by ID, receive the test message.

**Files:**
- Create: `tests/integration/botapi_stub.py`, `tests/integration/test_botapi_stub.py`
- Modify: `tests/integration/smoke.py`, `tests/integration/test_smoke.py`, `.github/workflows/ci.yml`, `docs/superpowers/specs/2026-10-16-tgjelly-v1-design.md` (§15 Q7 answer, after the CI run)

**Interfaces:**
- `botapi_stub.py` (stdlib only):
  - `class StubBotApi` with `start(host="0.0.0.0", port=0) → int` (the bound port; a `ThreadingHTTPServer` on a daemon thread), `stop()`, `calls() → list[dict]` (`{"method": str, "body": dict}`), and `python3 botapi_stub.py --port N` to run it standalone.
  - Requests are `/bot<token>/<method>`. JSON and form bodies are parsed; for multipart only the field names are recorded.
  - Answers:
    - `getMe` → `{"id": <token prefix as int>, "is_bot": true, "first_name": "Smoke", "username": "smoke_bot"}`;
    - `getWebhookInfo` → empty URL;
    - `getUpdates` → `[]` after 1 s;
    - `getChat` → a supergroup titled "Smoke group" for negative IDs, a private chat for positive ones;
    - `getChatMember` → an administrator with `can_post_messages` and `can_edit_messages`;
    - `sendMessage`/`sendPhoto` → a message with an increasing `message_id`;
    - `deleteWebhook`, `setMyCommands`, `deleteMyCommands`, `setMyDescription`, `setMyShortDescription`, `setMyDefaultAdministratorRights`, `sendChatAction`, `leaveChat`, `answerCallbackQuery`, `editMessageText`, `editMessageCaption` → `true` (edits → a message);
    - anything else → HTTP 404 `{"ok": false, "error_code": 404, "description": "Not Found"}` (§14.3).
- `smoke.py` gains `--stub` and `--expect-incompatible`.
  - **With `--stub`:**
    - start `StubBotApi` on a free port;
    - create the container with `--add-host=host.docker.internal:host-gateway -e TGJELLY_BOT_API_BASE_URL=http://host.docker.internal:<port>`;
    - after the plan-1 checks: `POST TgJelly/Token {"Token": "123456789:AAEhBP0av28KrBd9xL4Zq3Wm7Tn5Ys1Cv6QpZ", "SetProfile": true}` → `Outcome == "Connected"`;
    - wait (60 s) for Status `ConnectionState == "Connected"`;
    - `GET TgJelly/Settings` for the revision;
    - `POST TgJelly/Destinations {"ExpectedRevision": rev, "ChatId": -1001234567890, "Preset": "AdminAlerts"}` → `Outcome == "Added"`, `TestSent == true`;
    - the stub recorded a `sendMessage` to `-1001234567890` whose text contains `TgJelly test`;
    - within 30 s, a `setMyCommands` call.
  - **With `--expect-incompatible`:** after login, `GET TgJelly/Status` answers 200 with `Compatible == false`, and nothing else is checked.
- `ci.yml`:
  - the four existing smoke rows run with `--stub`;
  - a fifth row (`jellyfin/jellyfin:12.2` with `tgjelly_1.0.0.0.zip` and `--expect-incompatible`) checks F8;
  - the build job gets a step "List publish output (spec §15 Q7)": `ls -1 artifacts/publish/net9.0 artifacts/publish/net10.0`.

- [ ] **Step 1: Write the failing tests** (`test_botapi_stub.py`):

```python
class StubTests(unittest.TestCase):
    def setUp(self):
        self.stub = botapi_stub.StubBotApi()
        self.port = self.stub.start(host="127.0.0.1", port=0)
        self.addCleanup(self.stub.stop)

    def call(self, method, body=None):
        req = urllib.request.Request(f"http://127.0.0.1:{self.port}/bot123456789:AAEhBP0av28KrBd9xL4Zq3Wm7Tn5Ys1Cv6QpZ/{method}",
                                     data=json.dumps(body or {}).encode(), headers={"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=5) as r:
                return r.status, json.loads(r.read())
        except urllib.error.HTTPError as e:
            return e.code, json.loads(e.read())

    def test_get_me_uses_the_token_prefix_as_the_bot_id(self):
        status, body = self.call("getMe")
        self.assertEqual((200, 123456789, "smoke_bot"), (status, body["result"]["id"], body["result"]["username"]))

    def test_unknown_methods_are_404_with_a_description(self):
        self.assertEqual((404, "Not Found"), (self.call("sendDice")[0], self.call("sendDice")[1]["description"]))

    def test_calls_are_recorded_with_their_bodies(self):
        self.call("sendMessage", {"chat_id": -1001234567890, "text": "TgJelly test"})
        self.assertIn({"method": "sendMessage", "body": {"chat_id": -1001234567890, "text": "TgJelly test"}}, self.stub.calls())

    def test_get_updates_returns_empty_after_about_a_second(self):
        start = time.monotonic()
        self.assertEqual([], self.call("getUpdates")[1]["result"])
        self.assertGreaterEqual(time.monotonic() - start, 0.9)
```

Add to `test_smoke.py`: `test_stub_docker_args` — a new pure helper `stub_docker_args(port) == ["--add-host=host.docker.internal:host-gateway", "-e", f"TGJELLY_BOT_API_BASE_URL=http://host.docker.internal:{port}"]`.

- [ ] **Step 2: Run to verify they fail** — `python3 -m unittest discover -s tests/integration -p 'test_*.py'`. Expected: FAIL, `ModuleNotFoundError: No module named 'botapi_stub'`.
- [ ] **Step 3: Implement** the stub, the smoke steps and the workflow changes.
- [ ] **Step 4: Run the local checks** — `python3 -m unittest discover -s tests/integration -p 'test_*.py' -v && python3 -m py_compile tests/integration/smoke.py tests/integration/botapi_stub.py`. Expected: PASS, `OK`.
- [ ] **Step 5: Commit** — `git commit -m "ci: stub Bot API, end-to-end token and destination smoke, F8 check, publish listing"`
- [ ] **Step 6: Run CI** (outward-facing: **ask the owner** whether pushing `feat/plan2-telegram-core` and follow-up fix pushes are approved). Push, then watch the run until it finishes (`gh run watch --exit-status <id>` in the background). Expected: the build job and all five smoke jobs pass; each `--stub` job logs the token, destination and test-message checks as `ok -`. Then record the Q7 answer from the "List publish output" step in spec §15 (which DLLs besides ours and `Telegram.Bot.dll` appear in `bin`) and commit it: `git commit -m "docs: answer spec 15 Q7 from the CI publish listing"`.

---

### Task 16 (owner-gated, optional before plan 4): Record the Q9 error strings

§15 Q9 asks for Telegram's exact descriptions of an HTML parse error and of "can't initiate conversation". Only a real bot can answer it.

- [ ] **Step 1: Ask the owner** for a throwaway test bot's token, given as a file path, never pasted into the chat, plus their own Telegram user ID, and confirm they agree to two test calls. If they decline, skip this task: the classifier already matches several variants, and plan 4 can do it with the manual checklist (§14.4).
- [ ] **Step 2:** With a one-off script in the session scratchpad (not committed), read the token from the file and make two calls:
  - `sendMessage(chat_id=<owner id>, text="<b>x", parse_mode="HTML")` to the owner, who has started the bot;
  - `sendMessage` to a user ID that has never started the bot, e.g. the bot's own ID.

  Print only the `description` fields, and never the token or a URL.
- [ ] **Step 3:** Put the two strings into Appendix A (replacing the "to be confirmed" notes) and into `TelegramErrorClassifierTests.Delivery`. Run the classifier tests and expect PASS, then commit: `git commit -m "docs: record Telegram's exact parse-error and initiate-conversation descriptions (spec 15 Q9)"`.

---

## Self-check against the spec (done while writing this plan)

| Spec | Where |
|---|---|
| §9.1 client, proxy modes, base-URL override, `RetryThreshold = 0` | Task 4 |
| §9.2 supervisor: waits, getMe, webhook, own poll loop, in-memory offset, errors table, startup re-check, shutdown confirmation | Task 11 (re-check: Task 8's `RequestCheckAll`) |
| §9.3 queues, priority, pacing, caps + rolling summary, TTL, retries, held entries, edits + merging, file_id cache | Tasks 9, 10 |
| §9.4 classifier rows 1–15, health, re-checks (startup, 30 min, `my_chat_member`, Check/Test), recovery | Tasks 6, 8, 10, 11, 14 (manager messages: plan 4) |
| §6.1 token flow steps 1–6 (API side), webhook deletion only on save | Task 12; Task 13 endpoints |
| §10.2 secrets never returned | Tasks 12, 13 (`Status_NeverContainsTheToken`) |
| §10.3 `telegram`, `health`, `status.lastErrors/counters/publicUrlFlag`, `outbox`, `sentMessages`, `fileIdCache` | Tasks 1, 8, 9, 11 |
| §10.4 token change (same / different bot), disconnect | Task 12 |
| §11.2 Status, Token, Webhook/TakeOver, Settings, Proxy, Destinations (+Test, Check), Links | Tasks 13, 14 (Strings, Templates, Libraries, DetectedChats, Diagnostics, Dismiss: plans 3–5) |
| §12 logging redaction, at-least-once outbox | Tasks 2, 10 (graceful drain: plan 3) |
| §14.1 classifier, Outbox, health, token change, poll loop | Tasks 6, 8–12 |
| §14.2 Telegram fixtures | Tasks 6, 8 (JSON fixtures), Task 5 (`FakeBotApi`) |
| §14.3 stub and the token/destination part of end to end | Task 15 (media and `server.started` parts: plan 3) |
| §15 Q7, Q9 | Tasks 15, 16 |
| Plan-1 review minors 1–8 | Task 1 (1–7), Task 15 (8) |

## Roadmap: plans 3–5 (updated)

- **Plan 3 — Notification pipeline:**
  - **From the original roadmap:** intake consumers and watchers, both intake queues, MediaReadiness, the Router (with preset upgrades on load), the Aggregator, the Renderer (layouts, posters, links, `<tg-time>`, plurals), UpdateChecker, `server.started`, §12's 5-second graceful drain, the end-to-end media and `server.started` smoke steps against the stub.
  - **Also, because plan 2 deferred them:**
    - the render model on Outbox entries and re-rendering edits from it;
    - the `⏱` delayed-delivery line;
    - `LinkBuilder` with the PublicUrl suggestion in `GET Settings`;
    - `GET Libraries`.
  - Settles §15 Q8.
- **Plan 4 — Bot UX:**
  - **From the original roadmap:** `UpdateRouter` (replacing `BasicUpdateHandler`), `FlowEngine`, native-picker onboarding, forum topics, menus, detected chats (`DetectedChats` endpoints), managers and private messages about new destination problems (`health.notified`), fallback gating (the manual-block flag in `GET Destinations`), the bot work queue.
  - **Also:** the bot-scoped sections it adds go into `BotStateReset`.
  - Settles §15 Q1–Q4, plus Q9 if Task 16 was skipped.
- **Plan 5 — Dashboard, templates, release:** unchanged: the full page, `GET Strings`, notice dismissal (including `loadWarnings` and `publicUrlFlag`), Diagnostics with redaction, Scriban templates (`Scriban.dll` added to the whitelist), README and `docs/templates.md`, `release.yml`, the BaseUrl smoke variant, the §14.4 checklist. Settles §15 Q5–Q6.
