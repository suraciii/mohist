## Why

Editing `~/.mohist/config.jsonc` (e.g. bumping `WorkspaceCleanup.StorageBudgetBytes` 10G → 64G) currently forces a full `mo update server` (dotnet build + restart) before the change is observable, turning a lightweight ops action into a rebuild. The root cause is that `AddMohistConfigFile` bypasses .NET's native hot-reload path: it hand-strips JSONC comments (`StripJsoncComments`) and feeds a one-shot `AddJsonStream` memory stream that has no file watcher. The `reloadOnChange` parameter it advertises is a dead parameter never wired to anything. This is self-inflicted — `AddJsonFile` parses JSONC natively (`System.Text.Json` defaults to `ReadCommentHandling = Skip` + `AllowTrailingCommas`), so the preprocessing layer both costs us hot reload and solves a non-problem.

## What Changes

- Switch `AddMohistConfigFile` to the native `AddJsonFile(configPath, optional: true, reloadOnChange: true)` path, giving `IConfiguration` a real `PhysicalFileProvider` watcher that emits change tokens on edits.
- Wire the previously-dead `reloadOnChange` parameter into the real `AddJsonFile` call (no behavior change to the public signature).
- Remove `StripJsoncComments` entirely — JSONC comments (`//`, `/* */`) and trailing commas are handled natively by `AddJsonFile` / `System.Text.Json`.
- Migrate the two remaining `StripJsoncComments` call sites in `ConfigService` (`ReadConfigFile`, `WriteConfigFileAsync`) to `JsonNode.Parse` with `JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }`, preserving the current read/round-trip behavior without the hand-rolled stripper.
- Upgrade the `CleanupPolicyOptions` consumption point in `GET /api/runner/{runnerId}/config` (`RunnerRoutes.cs`) from the singleton snapshot `IOptions<CleanupPolicyOptions>` to `IOptionsSnapshot<CleanupPolicyOptions>` (per-request re-bind), so reloaded values reach the runner without a restart.
- Establish fault tolerance for the config source: a malformed `config.jsonc` or watcher failure must not block server startup or crash a running server (a new behavior — the current `AddJsonStream` path has no try/catch and a malformed file aborts `builder.Build()`).

## Capabilities
- `config-hot-reload`: The `config.jsonc` configuration source reloads on file change without a server restart; JSONC comments and trailing commas are parsed natively by the standard JSON pipeline (no custom preprocessing); reload/parse failure degrades gracefully without blocking startup. Covers `AddMohistConfigFile`, removal of `StripJsoncComments`, and the two `ConfigService` read/write call-site migrations.
- `options-live-consumption`: Options consumers read the latest reloaded values instead of a startup-time singleton snapshot. Covers switching the runner config endpoint (`GET /api/runner/{id}/config`) from `IOptions<CleanupPolicyOptions>` to `IOptionsSnapshot<CleanupPolicyOptions>`.

## Impact

- **Code**:
  - `packages/server/src/Mohist.Server/Infrastructure/Config/MohistConfigurationExtensions.cs` — replace `AddJsonStream` body with `AddJsonFile`; delete `StripJsoncComments`.
  - `packages/server/src/Mohist.Server/Infrastructure/Config/ConfigService.cs` — `ReadConfigFile` and `WriteConfigFileAsync` adopt `JsonNode.Parse` + `JsonDocumentOptions { CommentHandling = Skip }`.
  - `packages/server/src/Mohist.Server/Api/RunnerRoutes.cs` — `/config` handler parameter `IOptions<CleanupPolicyOptions>` → `IOptionsSnapshot<CleanupPolicyOptions>`.
  - `packages/server/src/Mohist.Server/Program.cs` — call sites unchanged (signature preserved).
- **Dependencies**: No new packages. `AddJsonFile` is available via the `Microsoft.NET.Sdk.Web` shared framework already in use.
- **APIs**: No wire-contract change to `GET /api/runner/{id}/config`; only the freshness of the returned `cleanupPolicy` improves.
- **Risk**: The blast radius spans every configuration key (all flow through this source). File-watcher behavior varies across filesystems (network mounts, containers); the proposal establishes non-fatal reload/load failure handling (the current code has no try/catch and a malformed file aborts startup). Tests must avoid real filesystem-watcher latency (per `design/testing.md`, use injectable providers/fake time, not wall-clock + sleep).
