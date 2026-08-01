# Self-Review — issue-538 (log path eliminates WorkflowRun State full-load read)

Reviewer role: critiquing `proposal.md`, `specs/`, `design.md`, `tasks.json` against the
issue and the live code. Findings only; no files changed except this one.

## Verdict

The architecture is sound and well-grounded in existing precedents (computed columns,
`DispatchSnapshotStore`, the `StageRunAsync` write funnel, cold-start upgrader discipline).
But the plan has precision/completeness gaps that will mislead or block the implementer, so it
is **not ready to build as-is**. Details below.

## Findings

### F1 — The taskId↔workId "mapping" is currently an identity mapping; the design never says so

Evidence:
- `WorkflowRun.Task.cs:20` — `StartTask` does `task.WorkId = workId`.
- `WorkflowWorkLifecycle.cs:116,120,123` — `MarkTaskRunningAsync` sets `workId = logicalTaskId`
  and selects the task by `t.Id == logicalTaskId`. So a claimed task has `task.WorkId == task.Id`.
- `TaskRun.cs:197-215` — `MakeTask` never sets `WorkId`, so only **pending** tasks have
  `WorkId == null`; every dispatched (Running/Completed/Failed) task has `WorkId == Id`.
- The runner uploads with the dispatched workId, which is `item.Id == t.Id` (`WorkflowWorkLifecycle.cs:159`).
  So uploaded workId == `TaskRun.Id` == the `taskId` in the query URL.

Consequence: in current practice the projected `taskId ↔ workId` correspondence is the **identity
function**. `design.md` proposes a general mapping table and `run-work-projection/spec.md` asserts
a "workId falls back to Id" rule without recognizing that workId is already Id for every task that
has logs. This matters because:

- The table's *real* job is **run-scoped task membership / claimed-status** (does this taskId
  belong to this run, and has it been dispatched), not a non-trivial id translation. The design
  should state this so the implementer doesn't store a "translation" that is secretly identity and
  so the fallback rule in the spec isn't misread as changing behavior.
- `run-work-projection/spec.md` "effective work id falls back to task id" is technically only
  reachable for **pending** tasks (which have no logs), making the scenario misleading.

**Must fix:** `design.md` D1 should acknowledge the mapping is identity today, justify carrying
workId for robustness, and reframe the table's purpose as run-scoped task membership. The spec's
fallback scenario should be clarified or dropped.

### F2 — The no-deserialize test approach presumes a faking mechanism that does not exist

`design.md` D5 and `tasks.json` T-003 acceptance say: inject a "fake `WorkflowRunQuerier` that
records/throws on `LoadAsync`" to assert the log path never deserializes State. But:

- `WorkflowRunQuerier` is a **concrete class** (`Infrastructure/Data/Workflow/WorkflowRunQuerier.cs:7`),
  not an interface, and `TaskLogService` depends on the concrete type (`TaskLogService.cs:34,41`).
- Existing specs instantiate it directly over a real migrated DB:
  `TaskLogServicePersistThenPublishSpecs.cs:36` and `TaskLogServiceSpecs.cs:29` both do
  `new WorkflowRunQuerier(factory)`.

So the positive assertion "LoadAsync is never invoked" cannot be made with the current shape — a
real-DB querier has no spy hook, and a behavior-passing test would NOT catch a regression that
falls back to `LoadAsync` (it would still work, just slowly). The plan's headline acceptance
criterion is therefore unachievable as written.

**Must fix:** `design.md`/`tasks.json` must specify how the invariant is positively asserted —
e.g. extract an `IWorkflowRunWorkProjection` (or interface/virtualize the querier) so a fake can
fail-closed on `LoadAsync`, or define an equivalent structural guard. This is an interface +
DI decision that belongs in the plan, not discovered at implementation time.

### F3 — Projection maintenance contract omits non-`StageRunAsync` State writers (cold-start upgraders)

`design.md` D2 calls `StageRunAsync` the "single write funnel" and keys all maintenance to it.
`StageRunAsync` is the single **runtime** write funnel (`WorkflowRunStore.cs:51,70`), but cold-start
State-rewriting upgraders write `State` directly and bypass it — #536's `WorkflowRunStateDataUpgrader`
is a live example (per `design/workflow/run-state.md`). The initial deploy is handled by T-002
backfill ordered after #536, but the **ongoing contract** is unstated: any *future* State-rewriting
migration will leave the projection stale relative to `State` until the next runtime save.

`run-state.md` already establishes "migration is a write-time obligation, not a read-time
obligation." The projection maintenance contract should extend the same way: any path that writes
`State` (runtime save OR cold-start upgrader) MUST also refresh the projection in the same
transaction.

**Must fix:** `design.md` D2/D4 should state explicitly that the projection is a write-time
obligation on **every** State-writing path, not only `StageRunAsync`, mirroring `run-state.md`.

### F4 (minor) — projectId resolution for checks uploads silently diverges (harmless, but spec claims "identical")

For a checks-work upload, current `ResolvePublishScopeAsync` (`TaskLogService.cs:225-237`) finds no
`TaskRun` whose `WorkId == checks-<stage>` and returns `null`, so the envelope's `projectId` is null
(`AppendAsync` reads `scope?.ProjectId`). The design reads `projectId` from `MetadataProjectId`
unconditionally, which would populate it for checks. It is **not observable** (a null `taskId`
already gates fan-out in `ShouldNotifyTaskLog`), but `task-log-stateless-read/spec.md` claims
"identical to today's behavior."

**Should fix:** clarify in the spec that `projectId` is only meaningful alongside a resolved
`taskId`, or read `projectId` only when the workId maps to a task, to keep the "identical" claim
literally true.

## What is solid (no change needed)

- Scope and non-goals correctly bound the work to the log path; the status path is correctly
  deferred to #539; the agent-job branch is correctly left alone.
- Precedents are accurately cited and the chosen storage (child table + active-work columns) is
  consistent with them; the rejected alternatives (computed columns / read-time `json_extract`) are
  correctly dismissed.
- Task decomposition is a valid DAG (`T-001 → T-002 → T-003`, T-003 depends on both); the
  T-003-after-T-002 ordering correctly prevents breaking log queries for terminal runs.
- Backfill discipline (idempotent, preflight, ordered after #536, before-service) matches
  `run-state.md`; correctly does not touch `State`/ETag.

## Summary of fixes a fix-task should apply

1. **F1** — `design.md`: acknowledge the taskId↔workId mapping is identity today; reframe the table
   as run-scoped task membership + claimed-status with workId carried for robustness; clarify/drop
   the spec fallback scenario.
2. **F2** — `design.md` + `tasks.json`: specify the injectable abstraction (or equivalent guard) that
   makes "LoadAsync never invoked" positively assertable; make T-003's acceptance achievable.
3. **F3** — `design.md`: extend the projection maintenance contract to every State-writing path
   (runtime save + cold-start upgraders), per `run-state.md`.
4. **F4** — `specs/task-log-stateless-read/spec.md`: clarify `projectId` is meaningful only with a
   resolved `taskId` (or tie its read to task resolution).

<promise>FAIL</promise>
