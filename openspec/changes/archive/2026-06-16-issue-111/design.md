## Context

`mo update` is the CLI command that builds and redeploys Mohist from local source. Its current flow stops the runner, builds and restarts the server, waits for readiness silently, then rebuilds and restarts the runner. Failure or interruption after runner stop leaves the runner stopped, breaking workflows. The Web UI's system update status can also drift — a `waiting-for-reconnect` job persists indefinitely even after the runtime has moved on.

The implementation spans three layers:
- **CLI** (`SourceCodeUpdater` in `MohistCliCommands.Update.cs`): orchestrates publish/build, service restart, readiness waiting, and runner lifecycle.
- **Server** (`SystemUpdateService` + `FileSystemSystemUpdateStore`): manages Web-triggered update jobs, persisted to `~/.mohist/system-update.json`.
- **Web** (`packages/web/src/entities/settings/`): polls update status and renders progress.

These layers are currently independent — the CLI update does not report outcomes to the server, and the server's status endpoint does not reconcile stale states.

## Goals / Non-Goals

**Goals:**
- Restructure `mo update` into observable product-level stages with progress feedback during long waits.
- Implement a recovery contract: runner restore on failure, timeout, or Ctrl-C interruption.
- Add post-update runtime consistency verification (CLI binary, server identity, web assets, runner, skill assets).
- Report a single outcome: ready, recovered with warnings, or failed with specific unavailable capability.
- Reconcile stale `waiting-for-reconnect` states in the server's update status endpoint.
- Persist CLI update outcomes so the Web UI can display them.
- Align CLI and Web update paths on product-level stages and outcome labels.

**Non-Goals:**
- Do not replace `mo update` with a new mechanism.
- Do not redesign service installation or background service management.
- Do not add a `mo doctor` command or broad diagnostics surface.
- Do not auto-kill unrelated processes.

## Decisions

### D1: Stage machine architecture for the CLI updater

Introduce an `UpdateStage` enum and an `UpdateContext` state object that tracks current stage, runner-was-running flag, warnings, and observed failures. The `UpdateAllAsync` method drives a stage sequence with explicit stage transitions rather than chained method calls. Each stage outputs a product-level label before executing, and updates the context on success or failure.

**Rationale:** The current linear flow has recovery logic baked into `RestoreRunnerAfterFailedUpdateAsync` but lacks signal handling and stage visibility. A stage machine decouples stage execution from outcome reporting and makes interruption/recovery a first-class concern.

**Alternatives considered:**
- Chain async delegates: simpler but harder to reason about recovery and interruption.
- Separate orchestrator class: adds indirection without benefit given the single update command.

### D2: Ctrl-C handling via CancellationToken + catch

Use the `CancellationToken` from System.CommandLine's `InvocationContext.GetCancellationToken()`, which is cancelled on SIGINT/SIGTERM. Wrap each cancellable operation in try/catch for `OperationCanceledException` and transition to the recovery stage instead of propagating the exception.

**Rationale:** System.CommandLine already wires console cancel to the token. Catching at stage boundaries gives us clean recovery entry points. No need for `Console.CancelKeyPress` or manual signal handling.

**Risk:** Some sub-operations (external process calls, service commands) may not respect the token promptly. → Mitigation: set aggressive timeouts on individual operations; the recovery stage is best-effort regardless.

### D3: Runner tracking via pre-update status check

Before stopping the runner, query `IServiceInstaller` for the runner's running state and record it in the update context. This determines whether recovery should attempt runner restore. The check is a snapshot — if the runner state changes during the update window, recovery still attempts to start it (best-effort is safe: starting an already-running service is harmless).

**Rationale:** The spec requires recovery only when the runner was running before update. Tracking this explicitly avoids restoring a runner that was already intentionally stopped.

### D4: Staleness reconciliation in `GetLatestStatusAsync`

When `GetLatestStatusAsync` fetches a job with status `waiting-for-reconnect`, compare the running server git hash (from `_getSystemInfo`) against the job's `SourceHead`. If the running hash is non-empty and differs, transition the job status to `superseded` and persist. Only perform this check for `waiting-for-reconnect` jobs — active `running` jobs still belong to the current server process.

**Rationale:** The stale state problem occurs when a Web-triggered update restarts the server, the old server process sets the job to `waiting-for-reconnect`, and then the user runs `mo update` from CLI which builds and restarts the server again. The new runtime has a different git hash but the old Web job still shows `waiting-for-reconnect`. Comparing hashes detects this drift without requiring cross-process signaling.

**Alternative considered:** Use a generation counter or timestamp. Rejected because hashes are already the identity signal used elsewhere in the system; no new data model needed.

### D5: CLI outcome persistence via POST to server

After the CLI update reaches its final outcome (ready/recovered/failed), and if the server is reachable, the CLI sends `POST /api/system/update/outcome` with a `SystemUpdateOutcome` payload containing job id, status, stage, outcome label, and any warnings/failures. The server persists this via the same `ISystemUpdateStore`, marking any older Web-triggered `waiting-for-reconnect` jobs as `superseded`.

**Rationale:** The server's store is the single source of truth for update status. Having the CLI write directly to `system-update.json` would bypass the lock and could race with a concurrent Web update. POSTing to the server ensures serialization.

**Risk:** If the server is not reachable at the end of CLI update, the outcome is only shown at the terminal but not persisted. → Acceptable: the CLI already reports the outcome; Web persistence is a bonus for visibility.

### D6: Runtime consistency verification as a final stage

Add a "Verifying workflow runtime" stage that checks:
1. CLI binary callable via `mo --version` (compares against published binary or just verifies it runs).
2. Server identity: `GET /api/system/info` and compare `running.gitHash` against source HEAD.
3. Web assets: `GET /` and verify HTML contains `/assets/*` references (reuse existing readiness logic).
4. Runner connected: `GET /api/system/info` and check `services.runner` state.
5. Managed skill assets: filesystem check that `~/.mohist/cli/skill-data/manifest.json` exists.

Each check produces a pass/warn/fail result. The stage aggregates these into the final outcome.

**Rationale:** The spec requires stating whether Mohist is ready to run workflows post-update. Individual component checks give the user actionable information when something is wrong.

### D7: Shared stage and outcome labels

Define canonical stage names and outcome labels as constants shared between CLI and server response types:

| Stage | CLI label | Web/API label |
|-------|-----------|---------------|
| CLI update | "Updating CLI" | (CLI-only) |
| Runner prep | "Preparing workflow runner" | (CLI-only) |
| Build | "Updating Mohist Server" | "Building" |
| Restart | (part of above) | "Restarting server" |
| Readiness | "Waiting for Mohist to become usable" | "Waiting for reconnect" |
| Runner restore | "Restoring workflow runner" | "Restoring runner" |
| Verification | "Verifying workflow runtime" | "Verifying runtime" |

Outcome labels: `succeeded`, `recovered`, `failed`.

**Rationale:** The spec requires shared semantics. The CLI uses more conversational labels; the API uses compact identifiers. Both map to the same conceptual stages.

### D8: `SystemUpdateJobState` extensions

Add `Status = "superseded"` and `Status = "recovered"` to the valid status values. Add optional `Outcome` field to capture the tri-state outcome label. Add optional `UnavailableCapability` for failed outcomes that name the specific missing capability.

The existing `IsActive()` method already gates concurrency on `running` and `waiting-for-reconnect` — `superseded` and `recovered` are terminal states and do not block new updates.

## Risks / Trade-offs

- **[Risk] Recovery is best-effort**: If `systemctl --user start mohist-runner.service` fails during recovery, the user must act manually. → Mitigation: print the exact command to run, plus the runner's known state.
- **[Risk] CLI outcome persistence races with concurrent Web update**: If a Web update starts while CLI is verifying, the CLI's POST may be rejected or overwritten. → Mitigation: The CLI outcome endpoint bypasses the lock check and always persists. If a Web update is genuinely active, the status endpoint will show whichever persisted last — both are truthful about their respective attempts.
- **[Risk] Git hash comparison for staleness may be empty**: At startup, the server may not yet know its git hash. → Mitigation: only supersede when the running hash is non-empty. An empty hash means the new runtime hasn't fully initialized; `waiting-for-reconnect` is still accurate.
- **[Trade-off] Stage machine adds ~150 lines**: The structured stage approach is more code than the current chained methods but makes recovery, interruption, and output formatting systematic and testable.

## Migration Plan

1. **Deploy**: All changes are delivered in a single `mo update` cycle. The new CLI binary will contain the updated `SourceCodeUpdater`; the new server binary will contain the staleness reconciliation and new endpoint.
2. **Rollback**: Running an older `mo update` binary is safe — it will not use the new stages or recovery, and will not POST outcomes. The server's staleness reconciliation is backward-compatible: it only adds a `superseded` status transition for existing `waiting-for-reconnect` jobs; no existing data is mutated incorrectly.
3. **Data migration**: `system-update.json` format is extended (new status values, new optional fields). Old files are read correctly by the new code. New files written with `superseded` or `recovered` statuses will be read as unrecognized status by old code, which will treat them as non-active (safe — no update blocking).

## Open Questions

- **Should the CLI persist outcome even when the server wasn't running before the update?** If the user runs `mo update` while the server is stopped, the server won't be available for POST at the end. Could write a sidecar file for the server to pick up on next start. Defer to implementation — start with server-must-be-reachable, and add sidecar if user feedback demands it.
- **Should the Web UI recovery path include runner restore?** The current Web-triggered update flow in `RunUpdateAsync` restarts the runner after successful server restart but does not restore the runner on failure. The spec for server-daemon requires this. The Web update path uses a fire-and-forget `Task.Run` — adding recovery requires the background task to check runner state before server restart and attempt restore on failure. This is low-risk but adds complexity to the async model. Include in scope per spec requirement.
