## Context

The task-log read path resolves work identity and active-work membership by full-loading
`WorkflowRun.State`. `TaskLogService` does this in three private methods, each calling
`WorkflowRunQuerier.LoadAsync` → `JSON.Deserialize<WorkflowRun>` over the whole `State` blob
(averaging ~390 KB, peaking ~4.4 MB):

- `ResolveWorkIdAsync` (query path) — `taskId → workId`, iterating every task in every stage.
- `IsActiveWorkAsync` (upload path) — active-work membership via `run.FindActiveWork(workId, runnerId)`.
- `ResolvePublishScopeAsync` (upload path) — `workId → taskId` + `projectId`; a single upload
  request thus deserializes `State` **twice** for the same run.

What each site actually needs is tiny: the run-wide `taskId ↔ workId` correspondence and the
single current active-work identity (`workId` + owning `workerId`). `design/workflow/run-state.md`
already forbids using a full `State` load as a metadata lookup; this change closes that gap on
the log path. `TaskLogService` is architecturally barred from calling the workflow grain
("performs no grain calls" — TaskLog is review evidence, independent of status adjudication),
so the answer must be a projection, not an in-memory grain read.

Established precedents in `Infrastructure.Data.Workflow`:

- **Computed columns** on `WorkflowRunRow` mirror scalar State fields (`Status`,
  `AssignedWorkerId`, `MetadataProjectId`, `ReadySince`, `IssueNumber`) so queries filter/order
  without deserializing `State`.
- **`DispatchSnapshotStore`** — a dedicated, write-maintained table queried without `State`,
  with per-run cleanup in `WorkflowRunStore.DeleteAsync`.
- `WorkflowRunStore.StageRunAsync` is the **single write funnel**: both `SaveAsync` overloads
  route through it; it receives the in-memory `WorkflowRun` and the `DbContext`, so it is the
  natural place to maintain a derived projection in the same transaction as `State`.

`TaskRun` identity: `Id` (required) is the timeline taskId; `WorkId` (nullable) is the work id;
`WorkerId` is set when claimed/running. `MakeTask` (`TaskRun.cs`) never sets `WorkId`, so a
**pending** task has `WorkId == null`. `StartTask` (`WorkflowRun.Task.cs`) sets `task.WorkId =
workId`, and `MarkTaskRunningAsync` (`WorkflowWorkLifecycle.cs`) passes `workId = logicalTaskId`
selected by `t.Id == logicalTaskId` — so every **claimed** (Running/Completed/Failed) task has
`WorkId == Id`. The runner uploads with the dispatched workId, which is `item.Id == t.Id`. **The
taskId↔workId correspondence is therefore the identity function today** for every task that has
logs. Active work is at most one per run (the current stage's `Running` task claimed by the
assigned worker, or active checks); the task set is run-wide, including completed tasks in earlier
stages (logs remain queryable for completed tasks).

## Goals / Non-Goals

**Goals:**

- Eliminate all three `State` deserializations on the task-log read path (upload ×2, query ×1).
- Introduce a write-maintained projection exposing the run-wide `taskId ↔ workId` mapping and
  the current active-work identity, queryable without deserializing `State`.
- Preserve the task-log external contract exactly (acceptance, fan-out scope, pagination,
  empty-page-on-miss behavior).

**Non-Goals:**

- Changing the task-log HTTP/wire contract or the runner's upload/query semantics.
- Optimizing the **status** read path — that is issue #539 (sibling, same epic).
- Removing `WorkflowRunQuerier.LoadAsync` / `WorkflowRunStore.LoadAsync` entirely — other
  callers (dispatch, report) legitimately need the full run.
- Touching the agent-job ownership branch of `TaskLogService` — it already queries
  `IAgentJobStore` and never loaded `State`.

## Decisions

### D1 — Projection storage: a task-map table plus active-work columns on the run row

Two distinct data shapes are projected, each in its natural form:

- **`WorkflowRunTaskMap`** — a child table, one row per `TaskRun` across all stages:
  `(WorkflowRunId, TaskId, WorkId)`, indexed on `(WorkflowRunId, TaskId)` and
  `(WorkflowRunId, WorkId)`. `WorkId` stored as the effective work id (`TaskRun.WorkId ?? Id`).
  Its **primary job is run-scoped task membership** — answering "does this `taskId` belong to
  this run, and has it been dispatched?" — which is the fact the query path needs to distinguish
  a real task from an unknown id. `WorkId` is carried alongside for robustness; because the
  correspondence is identity today (`WorkId == Id` for claimed tasks), `taskId → workId` and
  `workId → taskId` resolve to the same value, but storing `WorkId` keeps the projection correct
  if a future change lets `WorkId` diverge from `Id`.
- **`ActiveWorkId` / `ActiveWorkerId`** — nullable stored columns on `WorkflowRunRow`,
  holding the single current active work (a task workId **or** the checks workId
  `checks-<stage>`) and its owning worker; null when no work is active. This serves
  active-work membership in one indexed run-row read.

`projectId` for the publish scope is already satisfied by the existing
`WorkflowRunRow.MetadataProjectId` computed column — no new field.

**Alternatives considered:**

- *SQLite computed columns from `json_extract`.* Rejected: the active-work identity requires
  navigating `stages[currentStageId].tasks[?(@.status=='running')]` (array search by predicate);
  the taskId↔workId mapping is one-to-many. SQLite JSON1 cannot express either as a generated
  column, and a computed column cannot represent a collection.
- *`json_extract` at query time (no C# deserialize).* Rejected: it still reads the whole
  `State` blob from the page and parses it in SQL on every query — cheaper than STJ but not
  zero, and it defeats the clean "never touch `State` on the read path" invariant the spec
  requires. Also complex, hard-to-test SQL over nested arrays.
- *A single unified table with an `IsActive` flag and a synthetic `TaskId IS NULL` row for
  checks.* Rejected as the primary choice: it conflates the id-correspondence map with the
  current-work pointer and forces a synthetic row for the checks case. Kept the two shapes
  separate; the map stays a pure id correspondence.

### D2 — Maintenance on write, in the `State` transaction, via the single funnel

Projection rows/columns are recomputed from the in-memory `WorkflowRun` inside
`WorkflowRunStore.StageRunAsync` and staged in the same `DbContext`/transaction as `State`:

- Build the map from `run.Stages` → all `Tasks` → `(run.Id, task.Id, task.WorkId ?? task.Id)`;
  replace the run's existing map rows (delete-then-insert) so the table is an exact snapshot.
- Set `ActiveWorkId`/`ActiveWorkerId` from the run's current active work
  (`run.Assignment?.WorkerId` + the current stage's running task / active checks), null when none.

Because both `SaveAsync` overloads funnel through `StageRunAsync`, and `SaveEventsAsync` wraps
the work in `BeginTransactionAsync`, the projection commits atomically with `State`. On delete,
`DeleteAsync` clears the run's map rows (mirroring the existing
`_dispatchSnapshotStore.DeleteForRunAsync` call) — or a cascading FK does it.

**Write-time obligation covers every `State` writer, not just `StageRunAsync`.** `StageRunAsync`
is the single *runtime* write funnel, but cold-start data upgraders write `State` directly and
bypass it (e.g. #536's `WorkflowRunStateDataUpgrader`). Per `design/workflow/run-state.md`
("migration is a write-time obligation, not a read-time obligation"), any path that rewrites
`State` — runtime save **or** a cold-start upgrader — MUST refresh this projection in the same
transaction. The map/active-work computation is extracted into one shared helper used by both
`StageRunAsync` and the backfill upgrader (D4) so there is a single author; a future
`State`-rewriting migration calls the same helper instead of leaving the projection stale.

**Trade-off:** every `State` save now rewrites N tiny map rows. This is acceptable: writes
(state transitions) are far less frequent than the high-frequency log/status reads this removes,
map rows are three short strings, and N (tasks per run) is small. Diffing instead of
replace-on-save was considered and rejected as premature complexity.

### D3 — Read API behind a narrow `IWorkflowRunWorkProjection` abstraction

`TaskLogService` currently depends on the **concrete** `WorkflowRunQuerier`
(`TaskLogService.cs:34,41`), whose `LoadAsync` deserializes `State`. To make the no-`State`
invariant both structural and testable, introduce a narrow
`IWorkflowRunWorkProjection` interface carrying only the projection reads — **no** `LoadAsync`:

- `ResolveWorkIdAsync(workflowRunId, taskId)` → `string?` (map table).
- `ResolveTaskIdAsync(workflowRunId, workId)` → `string?` (map table).
- `IsActiveWorkAsync(workflowRunId, workId, runnerId)` → `bool` (run-row active-work columns).
- `GetProjectIdAsync(workflowRunId)` → `string?` (existing `MetadataProjectId` column), or fold
  `projectId` into the active-work read to keep upload to one run-row read + one map read.

The concrete implementation lives in `Infrastructure.Data.Workflow` (on or beside
`WorkflowRunQuerier`, registered in DI). `TaskLogService`'s dependency swaps from
`WorkflowRunQuerier` to `IWorkflowRunWorkProjection`. Because the interface exposes no
`State`-deserializing member, `State` deserialize becomes **structurally unreachable** from the
task-log service — the invariant is enforced by the type system, not by a test hoping a call
wasn't made. `TaskLogService`'s three private methods are rewritten to call these members; the
public `AppendAsync` / `QueryByTaskIdAsync` signatures and behavior are unchanged. This also
removes the upload's double-`State`-load redundancy (two full deserializations → two indexed
lookups for the same run).

### D4 — Backfill existing rows via a cold-start C# data upgrader

Existing rows have `State` but no projection. A terminal run that is never saved again must
still serve historical log queries, so the map **must** be populated at deploy time, not lazily
(lazy population would re-introduce a read-path `State` deserialize and violate the spec).
A cold-start C# data upgrader in the database-initialization flow (precedent:
`WorkflowDispatchSnapshotDataUpgrader`, and #536's `WorkflowRunStateDataUpgrader`) iterates each
`WorkflowRuns` row once, deserializes `State`, computes the map + active work, and writes the
projection — idempotent (skips rows already populated), batched in a single transaction,
ordered after #536's State-format upgrader. It runs before the server accepts requests, per
`run-state.md`'s rule that an unfinished-migration database must not enter service.

### D5 — Testing the no-`State`-deserialize invariant

Because `TaskLogService` depends only on `IWorkflowRunWorkProjection` (D3), which has no
`State`-deserializing member, the invariant is structural at the service boundary: a spec test
substitutes a **fake `IWorkflowRunWorkProjection`** and drives upload (active + inactive work)
and query (hit + miss), asserting acceptance, publish-scope stamping, null-taskId no-fan-out,
and empty-page-on-miss behavior — with `State` deserialize unreachable by construction. (The
existing `TaskLogService*Specs` that built a real `WorkflowRunQuerier` over a migrated test DB
move onto the fake; no concrete-querier faking is required.) Separately, the concrete projection
implementation gets a unit test asserting its reads consult only projection columns (e.g. a
deserialization spy/counter on the `JSON.Deserialize<WorkflowRun>` path is not invoked), covering
the implementation layer the service fake does not reach. Further unit tests cover projection
write-maintenance (given an in-memory run with tasks across stages + an active task, after
`StageRunAsync`+`Save` the map rows and active-work columns match) and the backfill upgrader's
idempotence.

## Risks / Trade-offs

- [Projection drift from `State`] -> Every `State`-writing path refreshes the projection in the
  same transaction (runtime `StageRunAsync` **and** cold-start upgraders), all via one shared
  map/active-work helper; no second author of the projection.
- [Write amplification on the hot write path] -> Tiny rows, small N, writes ≪ reads; net cost
  reduction. Optimize with diffing only if measured.
- [Backfill misses/corrupts legacy or in-flight rows] -> Cold-start upgrader is idempotent with
  preflight (deserialize via the current model; any failure aborts before service, per
  `run-state.md`); active runs rehydrate and re-save on first transition after deploy anyway.
- [New hand-maintained stored columns on `WorkflowRunRow`] -> The row's other derived fields are
  *computed*; `ActiveWorkId`/`ActiveWorkerId` are the first explicitly-written mirror columns.
  Nullable so a stale/missing value reads as "no active work"; maintained only in `StageRunAsync`
  and documented; backfill covers the initial population.
- [Rollback] -> Pre-release, no version-compat constraint (per `AGENTS.md`); rollback is
  reverting the commit + dropping the migration. The old `LoadAsync` path is removed, so there
  is no in-place fallback — noted as intentional.

## Migration Plan

1. **EF Core migration** — add the `WorkflowRunTaskMap` table (with `(WorkflowRunId, TaskId)`
   and `(WorkflowRunId, WorkId)` indexes and a cascade-FK to `WorkflowRuns`) and the nullable
   `ActiveWorkId` / `ActiveWorkerId` columns on `WorkflowRuns`.
2. **C# data upgrader** (DB-init flow, after #536's State-format upgrader) — populate the
   projection for every existing row from `State`, idempotently, in batched transactions.
3. **Deploy** — server starts, upgrader runs to completion before the service accepts requests,
   then `TaskLogService` serves reads entirely from the projection.
4. **Rollback** — revert the commit and drop the migration/table/columns; there is no read-path
   fallback to the old full load (intentional, given the active-dev / no-compat stance).

## Open Questions

- `WorkflowRunTaskMap` row set: confirm it carries **all** tasks across all stages (including
  completed ones in earlier stages), since logs remain queryable for completed tasks. (Current
  read code iterates all stages → yes.)
- Whether to fold the upload's run-row read (active-work membership + `projectId`) into a single
  query, leaving the map read (workId → taskId) separate — minor, decided at implementation.
- Naming: `WorkflowRunTaskMap` vs `WorkflowRunWorkProjection`; column names
  `ActiveWorkId`/`ActiveWorkerId` — finalize to match existing row conventions.
