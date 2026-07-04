## Why

`SystemUpdateService.StartAsync` launches the update as a fire-and-forget `Task.Run` with no process-lifecycle registration (`SystemUpdateService.cs:86`). The task body (`RunUpdateAsync`) is healthy in-process — it owns a try/catch/finally that persists `failed` and releases the lock — but nothing survives the process. If the server is killed/crashes/loses power while a job is `running` or `waiting-for-reconnect`, two pieces of state linger on disk: the job's active `Status` in `~/.mohist/system-update.json`, and the `~/.mohist/system-update.json.lock` file. After restart, `TryAcquireLockAsync` rejects forever (the persisted latest is still "active", and `TryCreateLockFile`'s `FileMode.CreateNew` fails because the lock file exists), so every subsequent `StartAsync` returns `update_in_progress` and the only recovery is manually deleting the `.lock` file. The update subsystem needs to self-heal on startup rather than silently lock up.

## What Changes

- Add a one-shot startup reconciler (registered as an `IHostedService`/`BackgroundService` whose `StartAsync` runs once, mirroring `EpicReconciliationService`'s hosting pattern) that, on server startup, loads the latest persisted job:
  - If `Status` ∈ {`running`, `waiting-for-reconnect`} **and** `UpdatedAt` predates the current process start time, mark it `failed` (reason = interrupted by process restart) and release the lock for that job.
  - Otherwise leave it untouched.
- Source the process start time through an injectable abstraction (not a wall-clock/process-info read) so tests can drive it via fake time, consistent with the `TimeProvider` injection landed in #356.
- Ensure the stale on-disk `.lock` file is actually cleared. `FileSystemSystemUpdateStore.ReleaseLockAsync` only deletes the lock when its in-memory `_lockOwnerJobId == jobId`, and `_lockOwnerJobId` is process-local — so after a restart it is `null` and a plain `ReleaseLockAsync(staleJobId)` no-ops. The store needs a path to drop a lock whose owner is no longer present, so that the reconciled-to-`failed` job no longer blocks the next `TryAcquireLockAsync`.
- `SystemUpdateService.StartAsync` and `RunUpdateAsync` behavior is unchanged. The running update flow is not touched.
- No change to lock-file semantics/path, no cross-process resumption of a half-finished build, and no `waiting-for-reconnect` timeout policy.

## Capabilities

- `stale-update-job-recovery`: The server recovers the system-update subsystem after a process restart that interrupted an active update. On startup, a job that is still `running`/`waiting-for-reconnect` but whose `UpdatedAt` precedes the current process start time is transitioned to `failed` (reason records the restart interruption) and its lock is released so a new update can begin. A fresh active job whose `UpdatedAt` is at or after the process start time is left in place, and any terminal job (`succeeded`/`failed`/`recovered`/`superseded`/`cancelled`) is never modified. The release actually frees the on-disk lock, not just the in-memory flag, so the next `StartAsync` can acquire it.

## Impact

- **Server** (`packages/server`):
  - `SystemInfo/` — new startup reconciler type (e.g. a `BackgroundService`/`IHostedService`), plus a small injectable abstraction for "current process start time". The reconciler depends only on `ISystemUpdateStore`, `TimeProvider`, the new start-time abstraction, and a logger.
  - `SystemInfo/FileSystemSystemUpdateStore.cs` — add a way to release a lock whose owning job is no longer active in the current process (the existing `ReleaseLockAsync(jobId)` no-ops post-restart because `_lockOwnerJobId` is process-local), so the reconciled job's `.lock` file is removed and `TryCreateLockFile` can succeed afterward.
  - `Infrastructure/Hosting/MohistServiceRegistration.cs` — `AddHostedService` for the reconciler; register the process-start-time abstraction (default implementation reads actual process start, overridable for tests).
- **Tests** (`packages/server/tests`): new spec(s) in `Specs/SystemSpecs/` using fake time + an injected process start time to assert (a) stale active job → `failed` + lock released + new `StartAsync` succeeds; (b) fresh active job (`UpdatedAt ≥ process start`) untouched; (c) terminal job untouched. No real time/process/filesystem dependencies (fake store + fake time, matching `SystemUpdateServiceSpecs`).
- **Web / runner / CLI**: none. No HTTP contract or DTO change; recovery is server-internal.
- **Risk** (low): purely additive startup behavior; the in-flight update path is untouched, and the heuristic (`UpdatedAt < process start`) is a recovery signal, not a new invariant.
