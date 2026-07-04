## Context

`SystemUpdateService.StartAsync` launches the update as a fire-and-forget `Task.Run` (`SystemUpdateService.cs:86`). The task body (`RunUpdateAsync`, `SystemUpdateService.cs:474`) is healthy **in-process**: its `try/catch/finally` (`:522`) persists a terminal status and calls `ReleaseLockAsync`. Nothing, however, survives the process. If the server is killed/crashes while a job is `running` or `waiting-for-reconnect`, two pieces of state linger on disk:

1. The job's active `Status` in `~/.mohist/system-update.json`.
2. The `~/.mohist/system-update.json.lock` file.

After restart, `FileSystemSystemUpdateStore.TryAcquireLockAsync` (`FileSystemSystemUpdateStore.cs:59`) refuses forever: it short-circuits when `SystemUpdateService.IsActive(latest)` (`:68`), and even if it didn't, `TryCreateLockFile` (`:71`, `FileMode.CreateNew`) fails because the lock file already exists. Every subsequent `StartAsync` returns `update_in_progress` (`SystemUpdateService.cs:62`), and the only recovery today is manually deleting the `.lock` file.

`#356` already injected `TimeProvider` into `SystemUpdateService` and registered `TimeProvider.System` as a singleton (`MohistServiceRegistration.cs:89`), which gives us a clean seam to source comparable time without touching the wall clock in logic.

There is one additional trap (called out by the proposal): `FileSystemSystemUpdateStore.ReleaseLockAsync` (`:84`) only deletes the lock file when its **in-memory** `_lockOwnerJobId == jobId` (`:89`). That field is process-local, so it is `null` in a freshly started process — a plain `ReleaseLockAsync(staleJobId)` after restart **no-ops** and leaves the lock file on disk. Marking the job `failed` is not enough; the reconciler must also drive a path that actually removes the on-disk lock.

Constraints:

- The in-flight update path (`StartAsync` / `RunUpdateAsync`) is healthy and must stay untouched (a `SourceAudit_*` test even pins `_store.ReleaseLockAsync(` to only `PersistTransitionAsync` / `RunUpdateAsync`).
- The reconciler must not depend on `SystemUpdateService` so the in-flight path stays decoupled.
- All time logic must go through injected `TimeProvider` + an injectable process-start-time seam (no wall clock / `Process.StartTime` read inside the reconciler); tests drive fake time.

## Goals / Non-Goals

**Goals:**

- On server startup, detect a job left `running`/`waiting-for-reconnect` by an interrupted prior process and transition it to `failed` with a reason that records the restart, so the subsystem no longer reports a phantom in-progress update.
- Actually free the on-disk `.lock` file for that job so the next `StartAsync` can acquire the lock and begin a new update.
- Leave fresh active jobs (`UpdatedAt >= process start`) and all terminal jobs untouched.
- Keep the change purely additive: new startup reconciler + new lock-release path + a small injectable process-start-time seam. No change to the running update flow, the lock semantics, or the lock file path.

**Non-Goals:**

- Do not convert the fire-and-forget `Task.Run` into an awaitable/cancellable background service (larger refactor; in-process path is already correct).
- Do not resume a half-finished build across processes — only "mark failed + release lock".
- Do not add a `waiting-for-reconnect` timeout/auto-abandon policy (separate reliability concern).

## Decisions

### D1. One-shot `IHostedService` whose `StartAsync` runs once, synchronously, before the host accepts requests

A one-shot startup reconciler is implemented as a direct `IHostedService` (not `BackgroundService`) whose `StartAsync(CancellationToken)` does the work and returns. The generic host **awaits** every `IHostedService.StartAsync` before it starts the request pipeline, so recovery completes before any `StartAsync` HTTP call can arrive. This eliminates the window in which a too-early request would still see `update_in_progress`.

**Alternatives considered:**

- `BackgroundService` overriding `StartAsync` to do work and skip `ExecuteAsync` — works but is surprising: `BackgroundService.StartAsync` normally kicks off `ExecuteAsync` as a fire-and-forget. Overriding it to do work synchronously reads as a misuse of the base type.
- `BackgroundService` whose `ExecuteAsync` runs a single pass and returns — **rejected**: `ExecuteAsync` is fire-and-forget; the host does not await it before serving. That reintroduces exactly the race this fix exists to close (recovery not finished when the first update request arrives).

### D2. New `ISystemUpdateStore.ReleaseStaleLockAsync(jobId)` — additive, content-matched, no in-memory gate

Add a second store method instead of loosening `ReleaseLockAsync`:

```csharp
Task ReleaseStaleLockAsync(string jobId, CancellationToken cancellationToken = default);
```

In `FileSystemSystemUpdateStore` this reuses the existing `ReleaseLockFile(jobId)` helper (which reads the lock file and deletes it only when its content matches `jobId`, `:174-189`) **without** requiring the process-local `_lockOwnerJobId` to match. It is safe by construction: only the job whose id is literally written inside the lock file gets its file deleted, so a concurrent fresh acquisition under a different `jobId` cannot be clobbered. The in-memory `_locked`/`_lockOwnerJobId` are already `false`/`null` at startup, so there is nothing extra to clear.

**Alternatives considered:**

- Loosen `ReleaseLockAsync` to drop the `_lockOwnerJobId` gate entirely — **rejected**. It changes the semantics that `SystemUpdateService`'s in-process flow relies on, risks the `SourceAudit_ReleaseLockAsyncOnlyInSharedHelpersAndRunUpdateFinally` regression test, and conflates two responsibilities (in-process ownership release vs. post-restart cleanup).
- A "force delete" method that ignores content — **rejected**. It could delete a lock file freshly acquired by a new job in a concurrent startup race; the content match is the cheap, correct guard.

The in-memory fake store used in specs mirrors the same behavior: `ReleaseStaleLockAsync` releases when the lock is currently held by `jobId` (preserving the `WaitForUnlockAsync` test signal), and is otherwise idempotent.

### D3. Process start time via a dedicated injectable abstraction

Introduce a single-purpose seam rather than reusing `TimeProvider` or capturing `now` at DI build time:

```csharp
public interface IProcessStartTimeProvider { DateTimeOffset GetStartTime(); }
```

The default production implementation reads the actual process start time (e.g. `Process.GetCurrentProcess().StartTime` converted to UTC) — the only place a real process-info read is allowed. The reconciler itself never touches `DateTimeOffset.UtcNow`, `Environment.TickCount`, or process info directly; it consumes both `TimeProvider` (for the `failed`/`CompletedAt` timestamps) and `IProcessStartTimeProvider` (for the stale threshold). Registered with `TryAddSingleton` so tests substitute a fake.

**Alternatives considered:**

- Capture `TimeProvider.GetUtcNow()` once at DI registration and inject it as a `DateTimeOffset` — **rejected**: registration time ≠ process start (registration happens after host bootstrap), and it hides the intent behind a raw `DateTimeOffset` parameter rather than a named abstraction.
- Extend `TimeProvider` with a start-time concept — **rejected**: `TimeProvider` is a BCL type; we should not subclass it to carry process metadata that only one consumer needs.

### D4. Stale signal is `Status ∈ active AND UpdatedAt < process start` (strict)

The reconciler reuses the existing `SystemUpdateJobState.ActiveStatuses` list (`running`, `waiting-for-reconnect`) and treats a job as stale only when its `UpdatedAt` **strictly precedes** the process start time. A job whose `UpdatedAt >=` process start was written by this process (or a concurrent writer) and is left untouched. Terminal statuses (`SystemUpdateJobState.TerminalStatuses`) are never modified regardless of `UpdatedAt`. This is a recovery heuristic, not a new invariant — the spec enshrines the strict-`<` rule and the "terminal never touched" guarantee.

### D5. Reconciler constructs the transition itself; it does not call into `SystemUpdateService`

`SystemUpdateRecoveryService` depends only on `ISystemUpdateStore`, `TimeProvider`, `IProcessStartTimeProvider`, and `ILogger`. For a stale job it builds the next state inline (`latest with { Status = "failed", Stage = "Failed", Reason = "interrupted by process restart", CompletedAt = time.GetUtcNow(), … }`, appending a log entry and bumping `UpdatedAt`), then `SaveAsync` + `ReleaseStaleLockAsync(jobId)`. The reason text is the literal `"interrupted by process restart"` (the example in the spec; stable for assertions). Decoupling from `SystemUpdateService` keeps the in-flight update path and its private helpers (`PersistTransitionAsync`, `CreateFailedTransition`) out of the recovery code path, satisfying the spec's dependency rule.

### D6. Placement and registration

- Reconciler type + the `IProcessStartTimeProvider`/default pair live under `packages/server/src/Mohist.Server/SystemInfo/` (the system-update slice, next to `SystemUpdateService` and `FileSystemSystemUpdateStore`). Hosted services in this codebase live next to their feature (`EpicReconciliationService` under `Events/Hosting`); the reconciler is system-update-specific, so `SystemInfo/` is its home, not generic `Infrastructure/Hosting/`.
- Registration in `MohistServiceRegistration.ConfigureMohistServices`:
  - `services.AddHostedService<SystemUpdateRecoveryService>();`
  - `services.TryAddSingleton<IProcessStartTimeProvider, ProcessStartTimeProvider>();`
- Hosted services are intentionally **not** picked up by the `ISingletonService` convention (`AddMohistConventionalServices` registers `AsSelf` singletons), so the explicit `AddHostedService` matches `EpicReconciliationService` / `AttachmentCleanupService` / `StagePopulationSnapshotService`.

### D7. Tests: one new spec file, fake-driven, in `Specs/SystemSpecs/`

New `Specs/SystemSpecs/SystemUpdateRecoverySpecs.cs` (same slice as `SystemUpdateServiceSpecs.cs`). It is a Spec (product capability: `stale-update-job-recovery`) constructed by directly `new`-ing the reconciler with the existing `InMemoryUpdateStore` fake + `FakeTimeProvider` + a fake/inlined `IProcessStartTimeProvider` + `NullLogger` — `Speed=Unit` trait, no real fs/process/time. Coverage maps 1:1 to the spec scenarios:

- stale `running` / stale `waiting-for-reconnect` → `failed` (reason records restart) + lock released (assert via store).
- no persisted job → no-op.
- after recovery, a fresh `StartAsync` acquires the lock (`Started = true`, not `update_in_progress`) — exercised against `SystemUpdateService` wired to the same recovered store, mirroring `SystemUpdateServiceSpecs.CreateService`.
- fresh active job (`UpdatedAt >=` process start) untouched; terminal job untouched.
- the on-disk lock-release path is verified with the existing `FileSystemSystemUpdateStore` temp-file pattern (`CreateFileSystemStore(statePath)`), asserting the `.lock` file is gone and a follow-up `TryAcquireLockAsync` on a new store instance succeeds — the only place a real (temp) file is used, consistent with the existing `FileSystemStore_*` specs.

## Risks / Trade-offs

- **[The reconciler no-ops on lock release if the lock file content ≠ stale jobId]** → Mitigation: `ReleaseStaleLockAsync` is idempotent and content-matched, mirroring `ReleaseLockFile`. If the file was already removed or holds a different owner, nothing is deleted; the persisted `failed` state alone is enough to unblock `TryAcquireLockAsync` (the `IsActive(latest)` check in `TryAcquireLockAsync` will pass), and `TryCreateLockFile` succeeds because the file is gone (or held by the legitimate new owner, in which case `TryCreateLockFile` correctly fails and the caller retries).
- **[Clock skew between the dead process's `UpdatedAt` write and the new process's start time]** → Mitigation: in production both go through `TimeProvider.System` (same monotonic-ish wall clock on a single host). The strict-`<` comparison has a wide margin in practice (a job interrupted by a restart was last updated many seconds before the new process boots). Worst case of a skewed-but-fresh job being wrongly marked failed is a one-time recovery of a job that would have needed an operator anyway; it never corrupts build output.
- **[Startup-time I/O blocks the host from serving requests]** → Mitigation: the reconciler does at most one `GetLatestAsync` + one `SaveAsync` + one lock-file delete — bounded, fast, and only runs once. This is strictly better than the current behavior (subsystem wedged until a human deletes the lock file).
- **[Race: a second concurrent `mo update` arrives while recovery is mid-flight]** → Mitigation: recovery runs inside `IHostedService.StartAsync`, which the host completes **before** opening the HTTP listener, so no request can race with it. In-process concurrency is already serialized by the store's `SemaphoreSlim`.

## Migration Plan

No schema, contract, or DTO change; no data migration. Deployment is automatic on the next `mo update server`:

1. New build includes `SystemUpdateRecoveryService`, `IProcessStartTimeProvider` + default impl, and the new `ReleaseStaleLockAsync` store method.
2. On the first post-restart boot of the new server, if a job was left active by the interrupted previous process, the reconciler marks it `failed` ("interrupted by process restart") and removes its lock file; otherwise it is a no-op.
3. Operators who previously hit the wedge and worked around it by deleting `~/.mohist/system-update.json.lock` no longer need to do anything.

Rollback: revert the build. Because recovery only ever transitions a stale active job to `failed` (a terminal state already produced by the existing healthy in-process failure path) and deletes a lock file whose owning job is gone, a rollback leaves the subsystem in a state equivalent to what the manual workaround produced. No state needs to be restored.

## Open Questions

- Default `IProcessStartTimeProvider` source: `Process.GetCurrentProcess().StartTime.ToUniversalTime()` is the obvious read; confirm there is no `.NET 11`-preferred API (e.g. an `Environment.ProcessStartTime`-style property) before implementing. Behavioral outcome is identical either way.
