## Context

The IssueDetailPage sidebar currently has three mutually exclusive buttons: Start (draft), Close (active), Resume (interrupted). When an agent is running (`status=active, isAgentRunningOnThis=true`), the user sees only a Close button — no visibility into which pipeline stage is executing, which build task is in progress, or when the agent last produced output. Pipeline runs take minutes; this blind spot forces users to `mo attach` or tail logs to understand what's happening.

The `AgentStatus` API (`GET /api/agent/status`) returns `activeAgents[]` with only `{ issueId, issueNumber, projectId }` — no stage/round/task metadata. `RunningAgent` in `AgentRunnerService` holds a bare `Promise<void>` with no progress state and no child process reference.

Three layers need to change: (1) backend progress data plumbing from `WorkflowController`/`RalphExecutor` into `RunningAgent`, (2) new Force Stop API, (3) frontend progress panel replacing the dead-zone Close button.

## Goals / Non-Goals

**Goals:**
- Extend `RunningAgent` and `AgentStatus` API with mutable progress metadata (stage, round, task progress, last-activity timestamp)
- Wire `WorkflowController` and `RalphExecutor` to update progress in-band as pipeline executes
- Add `POST /api/issues/:number/force-stop` endpoint that kills the child process and sets issue to `interrupted`
- Replace IssueDetailPage Close button with a live progress panel + Force Stop when agent is running
- Add 5s defensive timeout to `acp-session.ts` `cleanup()`

**Non-Goals:**
- Pause/resume (graceful SIGTERM + state save) — deferred to Issue #7
- SSE-based progress push — polling is sufficient for progress metadata (already 5s refetch)
- Progress persistence across server restarts — in-memory only, lost on restart

## Decisions

### D1: Progress update via callback on RunningAgent (not EventBus)

`WorkflowController` receives a `onProgress` callback from `AgentRunnerService` (closure over the `RunningAgent` object). Progress updates are direct mutations on the mutable `RunningAgent.progress` object — no intermediate event bus hop.

**Why:** The progress data only needs to flow `WorkflowController → RunningAgent → getStatus() API`. EventBus would add overhead and the data is inherently request-scoped (lost on server restart, not useful to other subscribers). The existing `plan_round_start` and `ralph_loop_progress` events continue for SSE/UI streaming — progress tracking is a separate, simpler read path.

**Alternatives considered:**
- EventBus for progress: would work but adds unnecessary indirection. Progress is polled via `getStatus()`, not pushed via SSE.
- Separate ProgressService: over-engineering for what is a mutable struct on `RunningAgent`.

### D2: WorkflowControllerOptions extended with `onProgress` callback

`WorkflowControllerOptions` gets a new optional field:
```ts
onProgress?: (update: { stage?: string; roundType?: string; roundIndex?: number; taskProgress?: { completed: number; total: number } | null }) => void
```

`AgentRunnerService.executePipeline()` creates this callback as a closure over the `RunningAgent` it's about to store, and passes it to `WorkflowController`. The callback updates `RunningAgent.progress` and sets `lastActivityAt = new Date().toISOString()`.

**Why:** Keeps the wiring point simple — one closure, zero new interfaces. `WorkflowController` already receives options in its constructor; adding one more callback fits the pattern.

### D3: Progress update sites in WorkflowController

Three update sites, all calling `this.onProgress?.()`:
1. **`run()` switch cases** — on each stage entry (`case Stage.Plan:`, `case Stage.Build:`, `case Stage.Review:`), emit `{ stage: "plan"|"build"|"review" }`
2. **`runPlanStage()` round loop** — at each `roundState.type = round.type` / `roundState.index = index` assignment (line ~136-137), emit `{ stage: "plan", roundType, roundIndex }`. Also in self-review round entry (line ~232-233).
3. **`runPipelineBuildStage()` RalphExecutor** — via `RalphExecutorContext.onTaskComplete` or a new `onProgress` field. After each task completion, read tasks.json to compute `{ completed: N, total: M }` and emit `{ stage: "build", taskProgress: { completed, total } }`.

The Review stage (`runPipelineReviewStage`) updates are analogous to Plan — set `{ stage: "review", roundType, roundIndex }`.

**Why minimal reads:** For Build task progress, we already have `activeCompletedTaskIds` accumulating in the `onTaskCompleted` closure (line ~590-594). We can compute `{ completed: activeCompletedTaskIds.length, total }` directly — no need to re-read tasks.json. The `total` is available from the initial tasks snapshot at line ~534.

### D4: Force Stop via SIGKILL on child process ref stored in RunningAgent

`RunningAgent` gains `childProcess?: ChildProcess`. The child process is captured in `executePipeline()` — specifically, `WorkflowController` needs to expose the current process via a callback or shared reference.

**Implementation:** `WorkflowController` gets an `onChildProcess?: (proc: ChildProcess) => void` callback in its options. Each stage that spawns an ACP session (`runPlanStage`, `runPipelineReviewStage`) calls this after `createAcpConnection`. For `runPipelineBuildStage`, the `RalphExecutor` internally spawns coder sessions via `runAcpSession`; each session spawns its own process. We store the **latest** child process — the one most likely to be active when Force Stop is called.

When `forceStop(issueId)` is called: `childProcess.kill('SIGKILL')`, remove from `activeAgents`, set issue status to `interrupted`, clear `pendingGates`/`waitingQuestions`, emit `agent_stopped` event.

**Why SIGKILL:** SIGTERM requires the child to handle it gracefully (close streams, clean up). The ACP subprocess may be stuck. SIGKILL is immediate and reliable. The pipeline's `finally` block in `executePipeline` still runs after the promise rejects, cleaning up `activeAgents`.

**Alternatives considered:**
- AbortController: would require threading a signal through all layers. SIGKILL is simpler.
- Store proc in `createAcpConnection` return value: already partially available via closure in `acp-session.ts`, but needs to be bubbled up to `WorkflowController`.

### D5: Frontend polling (not new SSE event) for progress

`useAgentStatus` already polls at 5s intervals. Progress metadata is piggybacked on the existing `GET /api/agent/status` response. No new SSE events needed.

**Why:** Progress updates at 5s granularity are sufficient (pipeline stages take 30s+). Adding SSE events for progress would require new event types, registration sync across 4 arrays, and RAF throttling — all for data that's already being polled.

### D6: cleanup() timeout via Promise.race

In `acp-session.ts`, wrap the existing `cleanup()` body:
```ts
const cleanup = async () => {
  const cleanupPromise = Promise.allSettled([
    stream.readable.cancel().catch(() => {}),
    stream.writable.abort().catch(() => {}),
  ]);
  const timeoutPromise = new Promise<void>((resolve) => {
    setTimeout(() => {
      log.warn('Cleanup timed out after 5s, forcing kill');
      resolve();
    }, 5000);
  });
  await Promise.race([cleanupPromise, timeoutPromise]);
  ensureKill();
};
```

**Why Promise.race:** Minimal change, no new dependencies. The `.catch(() => {})` on each stream op already prevents rejection propagation, so the timeout only fires if the streams genuinely hang.

## Risks / Trade-offs

- **[RunningAgent.progress is mutable shared state]** → Single writer (pipeline async), multiple readers (`getStatus()`). JS is single-threaded so no data race, but `progress` may show slightly stale data between writes. Acceptable — 5s polling makes this irrelevant.
- **[SIGKILL leaves zombie ACP processes]** → `ensureKill()` already runs a 5s delayed SIGKILL as a safety net. The pipeline's `finally` block deletes from `activeAgents`. Orphan processes are the same risk as today for crashes. Mitigation: Force Stop calls `ensureKill()` + the existing process cleanup in `acp-session.ts`.
- **[Child process ref may be undefined between stages]** → There are brief windows (stage transitions, before first ACP spawn) where `childProcess` is `undefined`. Force Stop during these windows should still succeed by: (1) removing from `activeAgents`, (2) setting issue to `interrupted`, (3) the running promise will reject on its own when it tries to use a closed connection. This is acceptable.
- **[Progress not persisted]** → On server restart, progress is lost. The issue's `stage` field in SQLite already tells which stage was running; `recoverableIssues` in AgentStatus handles this. Progress detail within a stage is ephemeral by design.

## Migration Plan

No migration needed — all changes are backward-compatible API additions:
- `AgentStatus.activeAgents[].progress` is a new field (clients that don't use it are unaffected)
- `POST /api/issues/:number/force-stop` is a new endpoint (404 if not registered, but we register it)
- Frontend changes are purely additive (new component in sidebar)

Deployment: single deploy, no phased rollout needed. The change is self-contained within the server + web bundle.

## Open Questions

- Should `agent_stopped` SSE event be added to the `ALL_EVENT_TYPES` / `AGENT_DETAIL_EVENTS` arrays for frontend real-time detection, or is polling sufficient? Currently assuming polling is enough since `useAgentStatus` already runs at 5s.
- For the child process ref in Build stage: `RalphExecutor` spawns multiple sequential coder sessions (one per task). Each has its own process. Storing only the latest means Force Stop kills the currently-executing task's process. Previous completed tasks' processes are already dead. This is correct behavior — just calling it out explicitly.
