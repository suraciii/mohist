## Context

AgentRunnerService (`packages/cli/src/services/agent-runner-service.ts`, 818 lines) currently manages concurrency through three scattered in-memory structures:

1. `activeAgents: Map<issueId, RunningAgent>` — tracks running pipelines (line 72)
2. `pendingGates: Map<issueNumber, PipelineGateInfo>` — tracks issues awaiting approval (line 73)
3. `conflictResolutionInProgress: Set<issueId>` — in `api/issues.ts` (line 46), not even in AgentRunnerService

The API layer (`api/issues.ts`, 2930 lines) duplicates ~30 lines of `isRunning()` + `maxConcurrentAgents` checks per endpoint (`/start`, `/approve`, `/reopen`, `/rebase`, `/propose`, `/retry`, `/rerun`). The `conflictResolutionInProgress` check at `/rebase` is the only place it's checked — `startPipeline` never checks it, creating a race window.

All state is in-memory. Server restart loses everything; recovery logic (`recoverIssues`) rebuilds partial state from issue `approval_state` but cannot reconstruct the queue.

**Existing DB patterns**: Versioned inline migrations in `db/migrations.ts` (current schema v17). Repos are plain classes taking `DatabaseManager` in constructor. Wired through `StateManager` getters passed to services in `server/index.ts`.

## Goals / Non-Goals

**Goals:**
- Unify all per-issue mutation operations through a single `enqueue()` entry point
- Guarantee per-issue serialization: at most one task per issue at any time
- Persist queue state to SQLite so it survives server restarts
- Replace API-layer concurrency checks with a single `enqueue()` call returning 202
- Remove `activeAgents` Map, `pendingGates` Map, and `conflictResolutionInProgress` Set

**Non-Goals:**
- MergeQueue integration (Check-stage approve → merge remains independent)
- SSE push notifications for queue state changes (Phase 4)
- CLI `mo queue` command (Phase 4)
- Explore sessions (read-only, no queue)
- Preemption of running tasks (high-priority only affects pending ordering)

## Decisions

### D1: Queue inside AgentRunnerService (Scheme C), not a standalone service

The task queue logic lives inside `AgentRunnerService` as new methods (`enqueue`, `cancel`, `cancelAll`, `getQueueStatus`, private `schedule`). No new `TaskQueueService` class.

**Rationale:** The queue is tightly coupled to pipeline execution. AgentRunnerService already owns `executePipeline()`, `forceStop()`, and all agent lifecycle. Extracting a standalone service would require bidirectional dependencies (queue → runner to execute, runner → queue to report completion).

**Alternatives considered:**
- **Scheme A: Standalone `IssueTaskQueueService`** — cleaner separation but creates circular dependency with AgentRunnerService. Would need event-based communication, adding complexity for no real gain.
- **Scheme B: Queue as middleware** — too abstracted from the actual execution semantics; would make per-issue serialization harder to enforce.

### D2: DB table `issue_task_queue` as source of truth, in-memory index for scheduling

The SQLite table is the authoritative state. An in-memory `Map<issueId, TaskRecord>` tracks pending + running tasks for fast scheduling lookups. Every state transition writes to DB first, then updates the in-memory index.

**Rationale:** DB-first ensures crash recovery. The in-memory index avoids querying the DB on every `schedule()` call when checking which issue has a running task.

**Alternatives considered:**
- **Pure in-memory queue with periodic DB sync** — simpler but loses tasks on crash between syncs.
- **DB-only, no in-memory index** — correct but `schedule()` would need `SELECT` queries to check per-issue running state on every slot-free event.

### D3: Approval gate = task completion, not task suspension

When a pipeline reaches an approval gate (`waiting-design-review`, `waiting-review`), the task is marked `completed` and the slot is released. When the user approves, a new `resume-pipeline` task is enqueued.

**Rationale:** Approval gates can last hours/days. Holding a slot during that time wastes concurrency capacity. The existing code already does this conceptually — `pendingGates` exists precisely because the running agent "completes" and the gate state is stored in the issue's `approval_state` DB field.

**Trade-off:** Two DB rows per approved stage (one `start-pipeline`/`resume-pipeline` that completes at the gate, one `resume-pipeline` that starts on approval). Acceptable overhead.

### D4: Rebase owns conflict resolution internally

The `rebase` task type includes conflict resolution as a sub-step. The current 150-line conflict resolution chain in `api/issues.ts` (lines 2275-2384) moves into a private method of AgentRunnerService called during rebase task execution.

**Rationale:** Conflict resolution is never triggered independently — it only happens as part of rebase. Making it a sub-step eliminates the `conflictResolutionInProgress` Set entirely, since the rebase task occupying the per-issue slot implicitly prevents concurrent operations.

### D5: No deduplication at enqueue time

`enqueue()` accepts duplicate task types for the same issue. Deduplication happens at execution time — if the task is no longer relevant (e.g., issue already in `done` stage), it's skipped with `result: "skipped"`.

**Rationale:** Enqueue-time deduplication requires understanding task semantics (is a `resume-pipeline` after a `start-pipeline` a duplicate or a follow-up?). Execution-time skip is simpler and harmless since skip is fast and releases the slot immediately.

### D6: Scheduler is event-driven, not polling

`schedule()` is called after: (1) `enqueue()`, (2) task completion, (3) task failure, (4) server startup recovery. No background polling loop.

**Rationale:** The set of events that can free a slot or add a pending task is small and known. Event-driven scheduling is simpler and has zero idle cost.

### D7: Migration as schema v18 in existing migrations.ts

New table added as `migrateToVersion18()` following the existing inline migration pattern. No migration framework introduced.

**Rationale:** Consistent with all 17 previous migrations. The project uses a single `migrations.ts` with sequential version numbers.

## Risks / Trade-offs

**[Per-issue serialization reduces throughput for high-frequency operations on same issue]** → Mitigation: Priority insertion allows urgent operations (e.g., `resume-pipeline` after approve at priority 10) to jump ahead of lower-priority pending tasks. In practice, users rarely queue multiple operations for the same issue.

**[202 response changes break existing CLI consumers]** → Mitigation: Phase 2 keeps old 200 response fields as deprecated additions alongside new `taskId`/`status` fields for one release. CLI updated in same PR.

**[DB write on every state transition adds latency]** → Mitigation: SQLite writes are <1ms. The in-memory index is updated synchronously; DB write is fire-and-forget within the same async context. If the DB write fails, log the error but don't crash — the in-memory state is the operational truth for scheduling.

**[Recovery on restart may incorrectly mark tasks as failed]** → Mitigation: Check `approval_state.status === 'awaiting'` on the issue before marking as failed. Issues at approval gates get their tasks marked as completed, not failed.

**[Large refactoring surface — ~2930-line issues.ts + ~818-line agent-runner-service.ts]** → Mitigation: Phase approach (Phase 1: DB + repo + queue logic, Phase 2: API integration, Phase 3: cleanup) limits blast radius per phase. Each phase is independently testable.

## Migration Plan

**Phase 1 — DB + Repo + Queue Logic (no API changes)**
1. Add `issue_task_queue` table as schema v18 in `db/migrations.ts`
2. Create `db/issue-task-queue-repo.ts` (standard repo pattern)
3. Wire repo through `StateManager` → `AgentRunnerService`
4. Add queue data structures to AgentRunnerService:
   - `private runningSlots = new Map<issueId, TaskRecord>()`
   - `private pendingQueues = new Map<issueId, TaskRecord[]>()`
5. Implement `enqueue()`, `cancel()`, `cancelAll()`, `getQueueStatus()`, `schedule()`
6. Implement `executeTask()` dispatcher (routes to `executePipeline()` or rebase logic)
7. Add recovery logic in `recoverFromQueue()` (called during startup)
8. Unit tests for all queue operations

**Phase 2 — API Integration**
1. Replace `/start` handler: remove `isRunning()` + `maxConcurrentAgents` checks → `enqueue(issueId, 'start-pipeline', payload)`
2. Replace `/approve` handler → `enqueue(issueId, 'resume-pipeline', payload)`
3. Replace `/reopen` handler → `enqueue(issueId, 'resume-pipeline', payload)`
4. Replace `/rebase` handler: move conflict resolution into rebase task executor → `enqueue(issueId, 'rebase', payload)`
5. Replace `/propose` handler → `enqueue(issueId, 'start-pipeline', payload)`
6. Replace `/force-stop` → `cancelAll(issueId)`
7. Add `GET /issues/:number/queue` endpoint
8. Add `DELETE /issues/:number/queue/:taskId` endpoint
9. Update `/retry` and `/rerun` to use enqueue where appropriate

**Phase 3 — Cleanup**
1. Remove `activeAgents` Map from AgentRunnerService
2. Remove `pendingGates` Map from AgentRunnerService
3. Remove `conflictResolutionInProgress` Set from `api/issues.ts`
4. Remove `startPipeline()` and `resumePipeline()` public methods (replaced by enqueue)
5. Remove `isRunning()` and `getMaxConcurrentAgents()` from public API
6. Update `getStatus()` return type
7. Update `recoverIssues()` to use `recoverFromQueue()`

**Phase 4 — Future (not in this change)**
1. SSE push for queue state changes
2. CLI `mo issue queue` command
3. Web UI queue status panel

**Rollback:** Each phase is independently revertible via git. Phase 1 adds new code without removing anything. Phase 2 API changes can be toggled with a feature flag if needed. Phase 3 is pure deletion — revert restores old code.

## Open Questions

- Should `/retry` and `/rerun` also go through `enqueue()`, or remain direct calls? Current proposal treats them as enqueue candidates (they start/resume pipelines), but they have extra pre-processing (checkpoint clearing, status resets). Recommend: Phase 2 enqueues them too, with the pre-processing happening inside the task executor.
- Should completed/failed tasks be cleaned up from the DB periodically, or kept forever for audit? Recommend: keep forever initially, add cleanup in Phase 4 if needed.
