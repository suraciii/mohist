## Context

Four API endpoints (close, reopen, approve, reject) guard against running agents via `agentRunner.isRunning(issueId)` → 409. The error message says "wait for it to complete or pause first" but no stop/pause API exists. The `RunningAgent` entry stores a `Promise<void>` from `executePipeline()`; the pipeline internally creates a `createAcpConnection()` which spawns an `opencode acp` subprocess managed by `ClientSideConnection`. The `AcpConnection` interface already exposes `close()` which calls `cleanup()` (stream cancel + SIGTERM/SIGKILL). However, neither `AcpConnection` nor the child process are reachable from `AgentRunnerService` — the connection is scoped inside `WorkflowController.run()`.

**Key constraint**: The `activeAgents` Map stores `{ issueId, issueNumber, promise, projectId }`. The `promise` field is the fire-and-forget pipeline promise. There is no reference to the ACP connection or child process at this level.

## Goals / Non-Goals

**Goals:**
- Allow users to stop a running agent via `POST /api/issues/:number/stop`
- Allow `force` query param on close/reopen/approve/reject to auto-stop then proceed
- Clean up all in-memory state (`activeAgents`, `pendingGates`, `waitingQuestions`) on stop
- Set issue status to `blocked` after stop so it can be reopened

**Non-Goals:**
- Graceful pause (wait for current round to complete) — too complex for M1, stop is sufficient
- Persisting stop/cancel state across server restarts
- Cancellation at the `WorkflowController` stage level (partial progress save)

## Decisions

### D1: Stop mechanism — kill child process directly via stored AbortController

Add an `AbortController` to the `RunningAgent` record. The `executePipeline()` method wraps `pipeline.run()` in `Promise.race([pipeline.run(...), abortSignal])`. When `stop()` is called, signal the AbortController → the pipeline catches the abort error → existing `finally` block cleans up `activeAgents`. Additionally, `WorkflowController` needs to propagate the signal to `createAcpConnection` so the ACP `prompt()` call is interrupted.

**Alternative considered:**

- **Store ACP connection reference in RunningAgent**: Would require plumbing the connection out of `WorkflowController.run()` back to `AgentRunnerService`. Violates the current encapsulation — `WorkflowController` owns the connection lifecycle. Rejected due to tight coupling.
- **Kill the child process by PID stored in RunningAgent**: Requires storing the PID when spawned. Fragile — PID reuse, process trees. Rejected.
- **Call `AcpConnection.close()` from outside**: `close()` is scoped inside `createAcpConnection()` closure. No external reference. Would need to restructure the connection API.

### D2: `force` query parameter on existing endpoints

Add `force` query param parsing to close/reopen/approve/reject handlers. When `force=true` and agent is running, call `agentRunner.stop(issueId)` first, await it, then proceed. When no agent is running, `force` is a no-op.

**Alternative considered:**

- **Auto-stop without `force` flag**: Changing the default behavior could surprise users who legitimately want to wait. Rejected — explicit opt-in is safer.
- **Separate stop-then-act client-side**: Requires two API calls. If stop succeeds but act fails, state is inconsistent. Atomic server-side is cleaner.

### D3: Issue status after stop → `blocked`

Set issue status to `blocked` (same as pipeline failure path). This is the existing pattern — `blocked` issues can be reopened via `POST /api/issues/:number/reopen`.

**Alternative considered:**

- **New `stopped` status**: Adds a new status value that all status-checking code must handle. Over-engineering for M1. `blocked` already conveys "pipeline halted, needs user intervention".

## Risks / Trade-offs

- **[Pipeline promise continues briefly after abort]** → The `pipeline.run()` async function may continue executing for a tick after `AbortSignal` fires. The `finally` block handles cleanup regardless. If it writes artifacts after abort, they may be partial — acceptable since the issue is `blocked` and will be re-driven on reopen.
- **[Race: agent finishes between isRunning check and stop call]** → `stop()` checks `activeAgents.has(issueId)` again internally. If agent already finished, returns `false`. Caller treats as success (agent is no longer running, desired state achieved).
- **[ACP cancel may fail if session already ended]** → Wrapped in try/catch. The `ensureKill()` fallback sends SIGTERM → SIGKILL to the child process regardless.

## Migration Plan

No migration needed — purely additive API. Existing endpoints retain backward-compatible behavior (409 without `force`). Deploy by rebuilding `packages/cli` and restarting the server.

## Open Questions

None.
