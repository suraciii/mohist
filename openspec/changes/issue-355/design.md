## Context

Mohist loads `~/.mohist/config.jsonc` through `AddMohistConfigFile` (`packages/server/src/Mohist.Server/Infrastructure/Config/MohistConfigurationExtensions.cs`), which today bypasses .NET's native config pipeline. It hand-strips JSONC comments (`StripJsoncComments`, `:34`) and feeds a one-shot `AddJsonStream` memory stream (`:27`). The advertised `reloadOnChange` parameter (`:12`) is dead — `AddJsonStream` has no file watcher. As a result, editing `config.jsonc` (e.g. bumping `WorkspaceCleanup.StorageBudgetBytes`) forces `mo update server` (full `dotnet build` + restart) before the change is observable.

Verified current state (code):

- **`AddMohistConfigFile` has no fault tolerance.** It does `File.ReadAllText` → `StripJsoncComments` → `AddJsonStream`, with **no try/catch** (`:19-27`). A malformed `config.jsonc` throws `FormatException` out of `builder.Build()` in `Program.cs:62` and crashes startup. The issue body references "existing `AddJsonStream` + try/catch" fault-tolerance — that try/catch does not exist; the spec's "malformed file shall not abort startup" requirement is therefore a **new** behavior to implement, not a regression to preserve.
- **`AddJsonFile` parses JSONC natively.** `Microsoft.Extensions.Configuration.Json`'s internal `JsonConfigurationFileParser` constructs its `JsonDocumentOptions` with `CommentHandling = Skip` + `AllowTrailingCommas = true`. Empirically confirmed (.NET 10): a `config.jsonc` with `//` line comments, `/* */` block comments, and a trailing comma loads cleanly through `AddJsonFile` with no preprocessing. `StripJsoncComments` is solving a non-problem and is the thing blocking hot reload.
- **`StripJsoncComments` has two other call sites**, both in `ConfigService` (`packages/server/src/Mohist.Server/Infrastructure/Config/ConfigService.cs`):
  - `ReadConfigFile()` (`:243`) — reads/parses existing config. Already wrapped in try/catch that returns an empty dict on failure (`:249-253`).
  - `WriteConfigFileAsync()` (`:266`) — read-modify-write round trip. Already wrapped in try/catch that falls back to a fresh `JsonObject` (`:271-274`).
  Both migrate cleanly to `System.Text.Json`'s built-in comment skipping; their existing fault-tolerance is preserved.
- **Options binding already supports change tokens.** `services.Configure<CleanupPolicyOptions>(configuration.GetSection(...))` (`MohistServiceRegistration.cs:116`) wires change-token propagation automatically — but nothing flows because the source never reloads.
- **The consumption point is a singleton snapshot.** `GET /api/runner/{id}/config` (`RunnerRoutes.cs:139`) takes `IOptions<CleanupPolicyOptions>` — bound once at startup. Even if the source reloaded, this consumer would not see the new value.
- **`Program.cs` calls `AddMohistConfigFile()` twice** — once on the main builder (`:11`) and once on the `BuildAlternateApp` fallback builder (`:111`, the OTLP-port-bind-failure recovery path). Both must benefit from the change; both call sites are signature-preserving.
- **Test fixture pre-wires a temp `_configPath`** (`MohistIntegrationFixture.cs:118`) but only feeds it to the injected `ConfigService` — `AddMohistConfigFile()` in `Program.cs` still reads the real `~/.mohist/config.jsonc`. Existing `CleanupPolicyOptions` tests do not rely on the file source at all; they override via `services.Configure<CleanupPolicyOptions>(opts => {...})` in `ConfigWebApplicationFactory.ConfigureTestServices` (`RunnerConfigApiSpecs.cs:493-501`).

Constraints / stakeholders: per `design/testing.md`, tests must not depend on real filesystem-watcher latency (flaky) or real wall-clock — reload in tests must be triggered deterministically, not by editing a file and polling. Per `design/architecture.md`, this is pure infrastructure (config loading + options injection); no domain concept changes. AGENTS.md states the project is in active development with no version-compatibility obligation.

## Goals / Non-Goals

**Goals:**

- Switch `AddMohistConfigFile` to the native `AddJsonFile(configPath, optional: true, reloadOnChange: true)` path so editing `config.jsonc` triggers an `IConfiguration` reload via a real `PhysicalFileProvider` watcher — no `mo update server` required for config changes.
- Wire the previously-dead `reloadOnChange` parameter into the real `AddJsonFile` call (public signature unchanged).
- Remove `StripJsoncComments` entirely and migrate its two `ConfigService` call sites to native `System.Text.Json` comment-skipping, preserving read/round-trip behavior.
- Upgrade the `GET /api/runner/{id}/config` consumption point from `IOptions<>` to `IOptionsSnapshot<>` so each request re-reads the currently-bound options.
- Make config-load and reload failures non-fatal — a malformed `config.jsonc` or watcher error must not block startup or crash a running server (a new behavior the current code does not provide).

**Non-Goals** (per proposal):

- No change to config-item semantics (e.g. `WorkspaceCleanup` field meanings) — only loading and consumption mechanisms.
- No fix for the idle-dispatch gap (`/poll` returning 204 without `cleanupPolicy`) — that is issue #359.
- No new config format/source (still `~/.mohist/config.jsonc` + `MOHIST__*` env overrides).
- No audit log or UI visualization of config changes.
- No comment-preservation on write-back (`JsonNode` does not carry comment positions — separate problem).
- No `ConfigService` rewrite beyond the two `StripJsoncComments` call sites.

## Decisions

### D1. `AddMohistConfigFile` switches to native `AddJsonFile`; `StripJsoncComments` is deleted from this path.

`AddMohistConfigFile` becomes:

```csharp
return builder.AddJsonFile(configPath, optional: true, reloadOnChange: reloadOnChange);
```

JSONC comments (`//`, `/* */`) and trailing commas are handled by the framework's `JsonConfigurationFileParser` (verified: `CommentHandling = Skip`, `AllowTrailingCommas = true`). `optional: true` preserves the existing "missing file is tolerated" early-return semantic (today's `if (!File.Exists) return builder`). `reloadOnChange` is the parameter that already exists on the signature (`:12`) — it is now wired for real instead of ignored.

`StripJsoncComments` is deleted outright from this file. Its `public` visibility (kept so `ConfigService` could share it) is no longer needed; D2 retires the remaining two callers.

Alternatives considered:

- **Keep `StripJsoncComments` as a defensive layer in front of `AddJsonFile`** — rejected: it is dead weight (the framework already skips comments), double-parses, and re-introduces a stream-based path that defeats the watcher. Keeping "just in case" violates the spec's "no hand-rolled stripper on the load path" requirement.
- **Add an explicit reload command / background polling service** — rejected (issue body): native hot reload is strictly less code and matches the local-first "edit and it takes effect" UX.

### D2. `ConfigService.ReadConfigFile` / `WriteConfigFileAsync` migrate to native `System.Text.Json` comment-skipping.

- `ReadConfigFile` (`ConfigService.cs:242-247`): replace `StripJsoncComments` + `JsonDocument.Parse(cleaned)` with `JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })`. The surrounding try/catch (`:249-253`) is preserved — a genuinely malformed (not merely commented) file still returns an empty dictionary.
- `WriteConfigFileAsync` (`ConfigService.cs:265-274`): replace `StripJsoncComments` + `JsonNode.Parse(cleaned)` with `JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })`. The surrounding try/catch fallback to `new JsonObject()` is preserved.

Both edits drop one line each; no new branching. After these, `MohistConfigurationExtensions.StripJsoncComments` has zero callers and is deleted (D1).

Alternatives considered:

- **Route `ConfigService` reads through `IConfiguration` instead of re-parsing the file** — rejected: `ConfigService` reads the raw `Mohist:Config:*` subtree and flattens it into `JsonNode` values (`FlattenJson`, `:334`), and the env-var override layer (`GetConfigValue`, `:363`) intentionally consults env → file → `IConfiguration` in that priority order. Re-routing would invert that priority and change observable config-resolution behavior — out of scope.
- **Keep `StripJsoncComments` for `ConfigService` only** — rejected: `System.Text.Json`'s `CommentHandling = Skip` does the same job with less code, and the spec explicitly forbids old/new coexistence.

### D3. `GET /api/runner/{id}/config` reads `IOptionsSnapshot<CleanupPolicyOptions>`, not `IOptions<>`.

`RunnerRoutes.cs:139` handler parameter: `IOptions<CleanupPolicyOptions>` → `IOptionsSnapshot<CleanupPolicyOptions>`. `IOptionsSnapshot` is scoped and re-binds from the current `IConfiguration` on each request, so a reload triggered by editing `config.jsonc` is reflected on the next call with no server restart. The handler body (`ToCleanupPolicyDto(cleanupPolicyOptions.Value)` wrapped in `RunnerConfigResponse`) is unchanged; the wire contract is identical (same 200, same `cleanupPolicy` shape, same present-null semantics — covered by `options-live-consumption` spec).

Choice of `IOptionsSnapshot` over `IOptionsMonitor`:

- The handler is request-scoped — `IOptionsSnapshot` is the textbook fit (re-read per request, no change-token subscription to manage).
- `IOptionsMonitor` is reserved for singleton consumers that need the latest value outside a request scope (the existing `HermesWebhookClient` / `HermesIssueNotificationHandler` pattern). The runner config endpoint has no such need.
- The spec explicitly allows either; `IOptionsSnapshot` is the simpler choice.

Verified compatibility with existing tests: `ConfigWebApplicationFactory.ConfigureTestServices` overrides values via `services.Configure<CleanupPolicyOptions>(opts => {...})` (`RunnerConfigApiSpecs.cs:493-501`). `IOptionsSnapshot<T>` rebuilds each request through `OptionsFactory<T>`, applying every registered `IConfigureOptions<T>` — so test-injected values still win, in the same registration order as today. **The existing `RunnerConfigApiSpecs` suite passes unchanged.**

Alternatives considered:

- **`IOptionsMonitor<CleanupPolicyOptions>`** — equally correct, more machinery (change-token subscription) for no benefit at a request-scoped handler. Rejected.
- **Inject `IConfiguration` directly and rebind inline** — rejected: bypasses the options pipeline (`PostConfigure`, validation) and is less idiomatic.

### D4. Fault tolerance is delivered via `OnLoadException` on the JSON source (covers both startup and runtime reload).

The spec requires (a) malformed `config.jsonc` at startup must not abort server start, and (b) a malformed runtime reload must not crash a running server. Today neither is satisfied (no try/catch). Both are delivered with one mechanism: after `AddJsonFile(...)`, retrieve the `JsonConfigurationSource` and set:

```csharp
source.OnLoadException = ctx =>
{
    log.LogWarning(ctx.Exception, "Failed to load/reload Mohist config file {Path}", configPath);
    ctx.Ignore = true;
};
```

`FileConfigurationProvider` invokes `OnLoadException` for **both** the initial `Build()` load and any watcher-triggered reload. Setting `ctx.Ignore = true` makes the provider fall back to empty (initial load) or last-known-good (reload) instead of throwing. This uniformly delivers the spec's "best-effort source, failures degrade to defaults / last-known-good" requirement without a custom `IConfigurationSource`.

`optional: true` independently covers the "missing file" case (the existing early-return semantic). `OnLoadException` covers the "present but malformed" case at both startup and runtime.

Alternatives considered:

- **Pre-parse the file with `JsonDocument.Parse` before adding the source; skip adding on failure** — works for startup but not for runtime reloads, and double-reads. Rejected in favor of the single `OnLoadException` hook.
- **Wrap `builder.Build()` in try/catch in `Program.cs`** — too broad (masks unrelated build failures) and does not protect runtime reloads. Rejected.
- **A custom `IConfigurationSource` / provider wrapping `AddJsonFile`** — over-engineering; `OnLoadException` is the framework-provided seam.

### D5. Tests trigger reload deterministically via `IConfigurationRoot.Reload()` — never via the real watcher.

Per `design/testing.md` hard constraint #2/#3, tests must not depend on real `FileSystemWatcher` debounce latency or real wall-clock polling. The design's testing contract:

- **JSONC parsing** (unit, `Speed=Unit`): call `AddMohistConfigFile(builder, path: tempJsonc)` directly on a `ConfigurationBuilder` with a temp file containing `//`/`/* */`/trailing commas; assert all keys bind. `ConfigService.ReadConfigFile` / `WriteConfigFileAsync` migration covered by extending `ConfigServiceSpecs` with commented-input cases. No watcher involved — pure parse assertions.
- **Reload propagates to the consumer** (spec, `Speed=Integration`): the hot-reload spec wires a temp JSONC file as an additional `AddJsonFile(temp, reloadOnChange: true)` source from the integration factory's `ConfigureAppConfiguration` (later sources override `Mohist:WorkspaceCleanup:*`), rewrites the temp file, then calls `((IConfigurationRoot)config).Reload()` and asserts the next `GET /api/runner/{id}/config` returns the new value. **`Reload()` is the deterministic trigger** — it forces every provider (including the JSON source) to re-`Load()` synchronously, so the test never waits on the OS watcher. Empirically verified (.NET 10): rewrite + `Reload()` → `IOptionsSnapshot` returns the new value immediately.
- **`reloadOnChange` is wired, not dead** (unit): structural assertion that the source registered by `AddMohistConfigFile` is a `JsonConfigurationSource` with `ReloadOnChange == true`. This proves the wiring without exercising the OS watcher.
- **Fault tolerance** (unit): assert `AddMohistConfigFile` does not throw and the builder builds when the temp file is malformed (covered by `OnLoadException`); assert `ConfigService.ReadConfigFile`/`WriteConfigFileAsync` return empty/fresh-object on malformed input (existing try/catch, unchanged).

Rationale: the production *trigger* is the OS file watcher, which is .NET framework behavior (`PhysicalFileProvider`) we do not re-test. Our tests prove the product guarantee — "when the source reloads, the consumer sees the new value" — deterministically by triggering the reload ourselves. This is the same philosophy as `FakeTimeProvider`: do not test the framework's timing; test the logic that reacts to it.

Alternatives considered:

- **`File.WriteAllText` + poll the endpoint until the value changes** — rejected: flaky (watcher debounce is wall-clock latency), violates testing.md #2/#3.
- **Abstract `AddJsonFile` behind a `IConfigSourceFactory` interface to inject a fake in tests** — rejected: introduces a production-side abstraction purely for testability when `Reload()` already provides a deterministic seam.

## Risks / Trade-offs

- **[Blast radius spans every config key — all flow through this source]** → mitigated by D1/D4: the source stays `optional` + `OnLoadException`, so a regression degrades to defaults/env vars rather than crashing; the JSONC-parsing unit test + the existing `ConfigServiceSpecs` / options specs cover the breadth.
- **[File-watcher behavior varies across filesystems (network mounts, container bind mounts, macOS FSEvents debounce)]** → mitigated by D4: watcher creation or notification failure either is swallowed by `PhysicalFileProvider` (which falls back to polling) or surfaces as a non-fatal reload error caught by `OnLoadException`. In the worst case the user falls back to `mo update server` — identical to today's behavior, never worse.
- **[Malformed config previously crashed startup; now it silently degrades — a user with a typo gets no force-restart signal]** → mitigated: D4 logs the parse problem as a warning on every load/reload; the user observes missing keys / defaults and can find the logged error. The improvement (no crash) strictly dominates the prior behavior.
- **[`IOptionsSnapshot` requires a request scope; injecting it into a singleton consumer throws]** → not a risk here: the runner config endpoint is a request-scoped minimal-API delegate. No singleton consumes `CleanupPolicyOptions` today (`MohistServiceRegistration.cs:116` is the only binding).
- **[`StripJsoncComments` is `public`; external callers may exist]** → verified: the only references in `src/` and `tests/` are the two `ConfigService` call sites (D2) and the `OtelOptions.cs` doc comment (updated to remove the reference). The class is internal server infrastructure, not a public API.
- **[`PhysicalFileProvider` watcher holds the file handle for the process lifetime]** → low impact on a single-host local-first server; the real `~/.mohist/config.jsonc` is single-writer (CLI/user). Accepted.

## Migration Plan

1. **`MohistConfigurationExtensions.cs`** — replace the `AddJsonStream` body with `AddJsonFile(configPath, optional: true, reloadOnChange: reloadOnChange)`; set `OnLoadException` on the resulting `JsonConfigurationSource`; delete `StripJsoncComments`. Remove the now-unused `using System.Text.Json` / `Microsoft.Extensions.Configuration.Json` if unreferenced after the edit (add `Microsoft.Extensions.FileProviders.Physical` only if needed — it is pulled transitively by the Web SDK).
2. **`ConfigService.cs`** — `ReadConfigFile`: `JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })`; `WriteConfigFileAsync`: `JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })`. Drop the two `StripJsoncComments` calls.
3. **`RunnerRoutes.cs`** — handler parameter `IOptions<CleanupPolicyOptions>` → `IOptionsSnapshot<CleanupPolicyOptions>` (`:139`). Update the `Microsoft.Extensions.Options` import if the alias changes.
4. **`Program.cs`** — no change (call sites `:11` and `:111` are signature-preserving; both the main host and the OTLP-fallback `BuildAlternateApp` host inherit the new behavior).
5. **Tests** — add JSONC-parsing unit cases to `ConfigServiceSpecs`; add a `reloadOnChange`-wired structural unit case for `AddMohistConfigFile`; add a hot-reload spec under `Specs/Runner/Config/` (or extend `RunnerConfigApiSpecs`) that writes a temp JSONC, calls `IConfigurationRoot.Reload()`, and asserts the endpoint returns the new value; add a malformed-file fault-tolerance case. The existing `RunnerConfigApiSpecs` suite passes unchanged (verified in D3).
6. **Deploy** — `mo update server` (standard). No DB migration, no wire-contract change. Editing `config.jsonc` takes effect on the next `/config` request after the watcher fires (typically sub-second on local disk).
7. **Rollback** — revert the commit; `mo update server` again. `config.jsonc` content is untouched by this change (only read), so rollback restores the prior "restart required" behavior with zero data migration. A config written via `WriteConfigFileAsync` after the change is still plain JSON (the `JsonNode` output is unchanged) and remains readable by the old `StripJsoncComments` path.

Verification gates: `npm test` (server — C# `TreatWarningsAsErrors` is the lint), focused `dotnet test` on `ConfigServiceSpecs` / `RunnerConfigApiSpecs` / the new hot-reload spec.

## Open Questions

- **Where does the hot-reload spec live?** Lean: a new `Specs/Runner/Config/ConfigHotReloadSpecs.cs` (matches the `options-live-consumption` capability, keeps the watcher/`Reload()` harness separate from the pure-projection `RunnerConfigApiSpecs`). Alternative: extend `RunnerConfigApiSpecs` with a single reload case. Decide at implementation time based on how much harness code the temp-file + `Reload()` setup needs.
- **Should the hot-reload spec reuse `MohistIntegrationFixture` or a per-test `ConfigHarness`?** The reload test needs a JSONC file source wired from `ConfigureAppConfiguration`, which the shared fixture does not expose today. Lean: a small per-test harness (like the existing `ConfigHarness` in `RunnerConfigApiSpecs.cs:374`) rather than widening the shared collection fixture.
- **Log category for `OnLoadException`** — emit via the `ILoggerFactory` resolved from the builder (if available at that point) or a plain `Console.Error` line matching the `OtelPortBindingLog` precedent? Lean: `Console.Error` (the extension is a static builder hook without a logger in scope), consistent with `Program.cs`'s other startup diagnostic writes.
