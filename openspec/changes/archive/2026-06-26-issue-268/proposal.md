## Why

Runner workspace directories accumulate indefinitely under `runnerRoot`. Today the only way to reclaim space is a per-issue manual `POST /cleanup`, so finished runs linger forever and `runnerRoot` grows without bound. We need the runner to automatically reclaim workspace for runs that have reached a terminal state, driven by workflow lifecycle events with a server-state convergence backstop — without ever touching active/pending/paused/awaiting-approval workspaces, workflow history, issue history, or identity-mismatched directories.

## What Changes

- Introduce a **runner-local active workspace registry** that records each workspace this runner has materialized (`issueId`, `issueNumber`, `workflowRunId`, `materializedAt`) and transitions entries to cleanup-eligible once the owning workflow run reaches a terminal state (completed/stopped/failed). This registry is runner runtime state, not domain truth.
- Drive terminal-state detection primarily from **workflow run lifecycle events** pushed to the runner; add a **compensating convergence pass** that, on runner restart/reconnect/missed-event, queries the server only for registry entries still marked active (no full-history scanning).
- Add **retention policy** cleanup: once a workspace has been eligible longer than the configured retention window, remove it.
- Add **storage budget policy** cleanup: when runner workspace usage exceeds the configured budget, evict the earliest-terminated eligible workspaces first until usage drops below the target watermark.
- Enforce **pre-delete safety guards** on every automatic removal: the target path must resolve under `runnerRoot`, and the on-disk `.mohist/workspace.json` marker's `workflowRunId` must match the registry entry. Any mismatch, missing marker, or active/pending/paused/awaiting-approval state aborts the removal.
- Keep the workspace **marker minimal** — only `issueId`, `issueNumber`, `workflowRunId`. No `createdAt`/`finishedAt`/`lastSeenAt` is written to the marker; lifecycle timestamps live in the registry.
- Expose a server-side **cleanup policy** (retention window, storage budget/target watermark) that the runner reads; the server continues to provide only policy + workflow status/events and does **not** scan, schedule, or perform runner filesystem deletion.
- Preserve the existing **manual cleanup** entry (`POST /issues/{N}/cleanup` → runner `RemoveWorkspace`) unchanged in user semantics; automatic cleanup is an additional runner-side mechanism.
- **Non-goal**: no deletion of workflow runs, issues, events, artifacts, sessions, or DB records; no repo-cache reclamation; no archive-issued trigger; no directory-mtime-based completion inference.

## Capabilities

### New Capabilities
- `runner-workspace-cleanup`: Runner-local workspace lifecycle and automatic garbage collection — active workspace registry, workflow-terminal convergence (event-driven with server-state backstop), retention-window eviction, storage-budget eviction with earliest-first ordering, pre-delete path/identity safety guards, and the rule that the marker stays identity-only while lifecycle timestamps live in the registry.

### Modified Capabilities
- `http-api`: Server SHALL expose a workspace cleanup policy (retention window, storage budget and target watermark) for the runner to read, and SHALL keep workflow run terminal status reachable by the runner for convergence. The server SHALL NOT scan, queue, or perform runner filesystem deletion.

## Impact

- **Runner** (`packages/runner`): new active-workspace registry (in-memory + persisted to runner local state), terminal-state event handling in `RunnerSignalRClient` (`packages/runner/src/server/runner-signalr.ts`) and convergence in `RunnerHost` (`packages/runner/src/runtime/host.ts`), periodic cleanup pass (retention + budget) with pre-delete guards reusing `isUnderRunnerRoot` / `readMarker` / `hasSameMarker` from `packages/runner/src/runtime/workspace.ts`. Marker writer (`issueWorkspaceMarker`) stays at its current 3 fields.
- **Server** (`packages/server`): new cleanup-policy config surface and its exposure to the runner (e.g., via poll response or a dedicated config read); ensure `WorkflowRunCompleted`/`WorkflowRunStopped` events (`Infrastructure/Events/EventCatalog.cs`) are delivered to the owning runner. No new server-side deletion path.
- **Web / CLI**: no user-facing cleanup UX changes; existing manual cleanup in `packages/web/src/entities/issue/api/client.ts` and the server `POST /cleanup` route (`Api/WorkspaceRoutes.cs`) remain as-is. Cleaned workspaces continue to surface as "workspace unavailable".
- **Tests**: runner registry transitions, event-driven and convergence-path terminal detection, retention eviction ordering, budget eviction to watermark, pre-delete guard rejections (out-of-root, marker mismatch, active state), and confirmation that manual cleanup semantics are unchanged.
- **No breaking changes** to external APIs; automatic cleanup is additive and confined to the runner's own filesystem under `runnerRoot`.
