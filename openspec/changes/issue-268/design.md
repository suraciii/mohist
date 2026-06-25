## Context

Today the runner materializes a workspace per workflow run under `<runnerRoot>/<project>/workspaces/issue-<N>` and writes an identity-only marker at `<workspacePath>/.mohist/workspace.json` (`issueId`, `issueNumber`, `workflowRunId`). The only reclamation path is a manual `POST /api/projects/{p}/issues/{n}/cleanup` → server → SignalR `RemoveWorkspace` on the assigned runner, which guards on `isUnderRunnerRoot` before `deleteDirectory`. There is:

- **No runner-side registry** of which workspaces this runner has materialized. The only per-workspace truth is the on-disk marker; nothing records `materializedAt`/`terminalAt`.
- **No terminal-state awareness on the runner.** `RunnerHost` polls (`POST /api/runner/{id}/poll`) and reports (`POST /api/runner/{id}/report`); the report response already carries `workflowStatus` (`host.ts:15` `ReportResult`) but the runner never reads it. `RunnerSignalRClient` handles only RPCs (`GetDiff`, `RemoveWorkspace`, `MaterializeWorkspace`, …) — no workflow lifecycle push.
- **No retention/budget config** anywhere (`grep` for retention/CleanupPolicy/StorageBudget is empty).
- Server-side terminal events exist (`EventCatalog.ReverseDns.WorkflowRunCompleted/Stopped/Failed`, emitted by `WorkflowGrain`) but `WorkflowGrain.OnWorkflowStoppedAsync` comments that "the workspace cleanup service subscribes to `.completed`" — that subscriber does not exist yet.

Constraints:
- Automatic deletion is irreversible; active/pending/paused/awaiting-approval and identity-mismatched directories must never be touched.
- Server stays the source of workflow lifecycle facts; it must not scan runner filesystems or own a deletion queue.
- Runner is TypeScript; server is ASP.NET Core + Orleans.

## Goals / Non-Goals

**Goals:**
- Runner-local active workspace registry (survives restart) recording identity + lifecycle phase + timestamps.
- Event-driven terminal detection with a server-state convergence backstop for missed events (queries only `active` entries).
- Retention-window eviction and storage-budget eviction (earliest-terminal-first) with pre-delete path + marker guards.
- Server exposes cleanup policy and keeps terminal status reachable; no server-side deletion.

**Non-Goals:**
- No deletion of workflow runs, issues, events, artifacts, sessions, DB records, or repo cache.
- No new manual-cleanup entry; no archive-issued trigger; no mtime-based completion inference.
- No cross-runner coordination (each runner owns its own registry and only cleans workspaces it materialized under its own `runnerRoot`).

## Decisions

### D1. Registry is a runner-local persisted JSON file, keyed by `workflowRunId`

Store the registry at `<runnerRoot>/.mohist/runner-state/workspaces.json` (a sibling of the `repos/` and `<project>/workspaces/` trees, already excluded from git per-workspace but this file lives above any workspace). Shape:

```ts
interface WorkspaceRegistryEntry {
  issueId: string | null
  issueNumber: number
  workflowRunId: string
  workspacePath: string        // absolute, resolved at materialize time
  phase: "active" | "eligible"
  materializedAt: string       // ISO
  terminalAt: string | null    // ISO, set on first terminal observation
}
```

Loaded into memory at `RunnerHost` startup; mutations write-through atomically (temp file + rename). Keyed by `workflowRunId` (already the unique run identity used by the marker and the workflow grain).

**Alternatives considered:**
- *SQLite per runner* — heavier dependency, no query needs justify it at this scale (tens to low-hundreds of entries). JSON is trivially inspectable and consistent with the existing marker style.
- *Keyed by issue number* — rejected: an issue can be re-run (`workflowRunId` changes) and the marker/path is reused; the run id is the stable unique key.

### D2. Terminal detection: primary = new SignalR push; backstop = batch status query on reconnect/interval

**Primary (event path):** add a server→runner SignalR method `ReceiveWorkflowRunStatus({ workflowRunId, status })`. The server invokes it on the owning runner when a run transitions to `completed`/`stopped`/`failed`. Routing: the workflow grain already emits terminal events onto the bus; a thin server-side subscriber resolves the assigned runner (via `RunnerGrain` assignment state) and invokes the method. The runner's handler transitions the matching registry entry to `eligible` and stamps `terminalAt` (idempotent — already-eligible entries are not re-stamped).

**Backstop (convergence):** add `POST /api/runner/{runnerId}/workflow-runs/status` accepting `{ workflowRunIds: string[] }` and returning `{ [runId]: status }` for those runs only. The runner calls it:
- once on startup/reconnect (`onReconnected` already exists in `RunnerSignalRClient`), and
- on a periodic timer (e.g. every N minutes), restricted to `phase === "active"` entries.

This satisfies "query the server only for registry entries still marked active — no full-history scan."

**Alternatives considered:**
- *Read `workflowStatus` from the existing report response* — insufficient: it only fires when this runner reports a work item, so externally-stopped or server-failed runs that the runner didn't report would be missed with no backstop. Kept as a cheap *additional* signal (the runner MAY also transition on report-status), but not the primary path.
- *Poll-only (no push)* — would force frequent polling of all active entries and adds latency to eviction; the push path makes the common case immediate while the periodic query handles the tail.
- *Runner subscribes to the event bus directly* — rejected: the runner is a stateless HTTP/SignalR client, not a bus consumer; routing through the server keeps the server as the single lifecycle authority.

### D3. Cleanup policy delivered via the runner poll response

Extend `WorkDispatchResponse` (`packages/server/.../RunnerRoutes.cs`, `packages/runner/src/core/types.ts`) with an optional `cleanupPolicy?: { retentionDays: number | null, storageBudgetBytes: number | null, storageTargetWatermarkBytes: number | null }`. The server includes it on every poll response; on idle polls (204) the runner relies on the last-seen policy plus periodic refresh.

**Alternatives considered:**
- *Dedicated `GET /api/runner/{id}/config`* — cleaner separation but adds a round trip and another endpoint for a small, rarely-changing object; piggybacking on poll (already happening every second) gives free, continuous propagation.
- *Environment-only config on the runner* — rejected: the proposal requires the server to be the policy source so multi-runner deployments share one policy.

When policy fields are null/unlimited, the corresponding eviction is disabled (retention unlimited ⇒ no age eviction; budget null ⇒ no budget eviction).

### D4. Eviction pass runs on a timer inside `RunnerHost`

Add a `runCleanupLoop(signal)` alongside `runWorkerPool` and `runSelfCheck`. On each tick (e.g. every 1–5 min) it:
1. Computes eligible set (`phase === "eligible"`).
2. **Retention:** remove entries where `now - terminalAt > retentionDays` (when retention enabled).
3. **Budget:** if `du`-style usage over budget, sort eligible by `terminalAt` ascending and remove until usage ≤ watermark (when budget enabled).
4. Each removal calls a shared `safeRemove(entry)` that runs the pre-delete guards (D5) before `deleteDirectory`, then deletes the registry entry.

A lock/`Set<workflowRunId>` prevents concurrent removal of the same entry and prevents colliding with an in-flight `executeAndReport` for that run (cross-checked against the worker pool's in-flight keys).

**Alternatives considered:**
- *Evict inline in the terminal-status handler* — couples push handling to disk I/O and budget math; a dedicated loop keeps the hot path cheap and makes retention math (which needs `now`) natural.
- *OS-level cron* — out of scope; the runner owns its lifecycle.

### D5. Pre-delete guards reuse existing primitives

`safeRemove(entry)` reuses, unchanged:
- `isUnderRunnerRoot(this.runnerRoot, entry.workspacePath)` (`runner-signalr.ts:521`) — abort if not contained.
- `readMarker(workspacePath)` + compare `workflowRunId === entry.workflowRunId` (`workspace.ts:450`) — abort on missing/mismatched marker.

Only if both pass → `deleteDirectory(workspacePath)` then remove the registry entry. Any abort leaves both directory and registry entry intact (the entry stays `eligible`; a future tick re-evaluates). This deliberately reuses the manual-cleanup containment check rather than duplicating logic.

### D6. Marker stays exactly as-is

`issueWorkspaceMarker()` (`workspace.ts:437`) continues to write only `issueId`, `issueNumber`, `workflowRunId`. No timestamp fields are added. Lifecycle timestamps live exclusively in the registry (D1). The `materialize()` path gains one new side-effect: after writing the marker, register/refresh the entry in the registry. `verify()` (re-use of an existing workspace) refreshes `materializedAt` and ensures the entry exists.

### D7. Registry registration points

- On `WorkspaceManager.materialize()` success → upsert registry entry `active`, `materializedAt = now`.
- On `WorkspaceManager.verify()` success (workspace reused) → ensure entry exists, refresh `materializedAt`; do not downgrade an `eligible` entry back to `active` unless a new run id is observed (handled by the marker identity already differing).
- On manual `RemoveWorkspace` success → delete the registry entry (keeps registry consistent with reality; manual cleanup already deletes the dir).

## Risks / Trade-offs

- **[Risk] Runner deletes a workspace still needed by an in-flight agent/work item** → Mitigation: the cleanup loop cross-checks the worker pool's in-flight keys (`ownerKind:ownerId:workId`) and the registry phase (`eligible` only); active entries are never eligible. The terminal transition only happens on a genuine terminal status from the server.
- **[Risk] Stale registry entries after manual deletion or runner migration** → Mitigation: `safeRemove` tolerates already-missing directories (treat missing path as "already removed", delete the entry); convergence never scans the filesystem, only reconciles `active` entries against server status.
- **[Risk] SignalR push lost (runner offline at terminal moment)** → Mitigation: the periodic + on-reconnect convergence query (D2 backstop) is the authoritative catch-all; the push is a latency optimization only. Correctness does not depend on push delivery.
- **[Risk] `du`/usage computation is expensive on large trees** → Mitigation: compute usage lazily and only when budget is configured and exceeded threshold; cache between ticks. Acceptable since the loop runs at minute cadence, not per-poll.
- **[Risk] Two runners sharing a `runnerRoot`** → Mitigation: out of scope (each runner has its own root); the registry is per-root and only this runner's materializations are tracked. Documented as a non-goal.
- **[Trade-off] Registry as JSON vs DB** → chose JSON for inspectability and simplicity; if entry counts ever grow into the thousands, revisit.

## Migration Plan

1. **Server first:** add `cleanupPolicy` to `WorkDispatchResponse`, the batch status endpoint, the terminal-event→runner SignalR routing. These are additive; runners ignore unknown fields. Deployable independently.
2. **Runner:** add registry (load/save), registration hooks in `materialize`/`verify`, the `ReceiveWorkflowRunStatus` handler, the convergence query, the cleanup loop with guards. Ship with policy fields defaulting to disabled (null) so behavior is opt-in — no workspaces are removed until a policy is configured.
3. **Enable:** set retention/budget in server config once runners are rolled out. Because eviction only acts on `eligible` entries that pass both guards, the ramp is safe; existing manual cleanup is untouched.
4. **Rollback:** set policy back to disabled (null). Already-evicted workspaces are gone (irreversible by design), but no further removal occurs; the registry remains and converges normally. To fully revert, stop the new runner build — the registry file is harmless leftover state.

## Open Questions

- **Exact cadence of the convergence query and cleanup loop** — propose 5 min for convergence, 2–5 min for cleanup; confirm against real workspace sizes during implementation.
- **Whether the report-response `workflowStatus` should also drive a transition** — leaning yes as a free extra signal, but need to confirm it reliably reflects terminal status at report time (currently unused/unverified).
- **Server config surface for the policy** — appsettings section vs per-project setting. Proposal says "server exposes policy"; needs a decision on scope (system-wide vs per-project) during implementation.
