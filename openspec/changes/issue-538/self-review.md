# Self-Review (pass 2) — issue-538 (log path eliminates WorkflowRun State full-load read)

Re-review of the plan after the pass-1 fixes. pass-1 found F1–F4; this pass verifies each fix
landed and scans for new issues. Artifacts reviewed: `proposal.md`, `specs/`, `design.md`,
`tasks.json`, against the issue and the live code.

## Verdict

All pass-1 findings are genuinely resolved and no new blocking issue was introduced. The plan
is **ready to build**. One non-blocking documentation nit remains (noted below).

## Pass-1 findings — resolution status

### F1 (identity mapping) — RESOLVED

- `design.md` Context (lines 31-40) now states, with code evidence, that the taskId↔workId
  correspondence is identity today: `MakeTask` never sets `WorkId` (pending → null);
  `StartTask` sets `task.WorkId = workId`; `MarkTaskRunningAsync` passes `workId = logicalTaskId`
  selected by `t.Id == logicalTaskId`, so claimed tasks have `WorkId == Id`.
- `design.md` D1 (lines 67-75) reframes `WorkflowRunTaskMap`'s primary job as **run-scoped task
  membership**, with `WorkId` carried for robustness against a future divergence.
- `run-work-projection/spec.md` (lines 7-13, 28-38) replaces the misleading "fallback" scenario
  with accurate identity-for-dispatched-tasks + well-defined-for-unset scenarios.

Verified against `TaskRun.cs:197-215`, `WorkflowRun.Task.cs:20`, `WorkflowWorkLifecycle.cs:116,123`.
Consistent and correct.

### F2 (no-deserialize testability) — RESOLVED

- `design.md` D3 (lines 128-149) introduces a narrow `IWorkflowRunWorkProjection` interface
  (no `LoadAsync` member) that `TaskLogService` depends on instead of the concrete
  `WorkflowRunQuerier`; State deserialize is therefore **structurally unreachable** from the
  service, enforced by the type system.
- `design.md` D5 (lines 163-177) specifies a fake `IWorkflowRunWorkProjection` for behavior +
  a deserialization-spy unit test for the concrete implementation layer.
- `tasks.json` T-001 delivers + registers the interface and the shared helper; T-003 swaps the
  dependency and uses the (now achievable) fake. Acceptance criteria are concrete and testable.

This resolves the earlier "fake a concrete class" impossibility (`TaskLogService.cs:34,41`;
existing specs use `new WorkflowRunQuerier(factory)`).

### F3 (maintenance contract for non-StageRunAsync State writers) — RESOLVED

- `design.md` D2 (lines 114-121) states the projection is a write-time obligation on **every**
  State-writing path (runtime `StageRunAsync` AND cold-start upgraders), via one shared
  map/active-work helper — mirroring `design/workflow/run-state.md`.
- Risks bullet (lines 181-183) and `tasks.json` T-002 notes (line 44) updated consistently; the
  backfill upgrader is flagged as a State-reading path that must populate the projection in the
  same transaction.

### F4 (projectId for checks) — RESOLVED

- `task-log-stateless-read/spec.md` (lines 21-30, 38-43) clarifies `projectId` resolves **only**
  with a `taskId`; an unmappable/checks workId yields a null scope (both absent), matching
  today's `ResolvePublishScopeAsync`-returns-null behavior. T-003 acceptance (line 53) matches.

## Remaining non-blocking observation (not a must-fix)

**Stale phrase in the Risks section.** `design.md` risk bullet (lines 189-192) still says the
active-work columns are "maintained only in `StageRunAsync`." After F3 this is no longer literal:
they are also written by the backfill upgrader (T-002) and, per D2, by any future State-rewriting
migration — all through the shared helper. The bullet *understates* a mitigation that is actually
stronger than described (more maintainers, one helper), so no risk is left unaddressed; the
authoritative D2 and `tasks.json` are correct. Suggest rewording to "maintained via the shared
helper on every State-writing path (runtime save + cold-start upgraders)." Does not block building.

## What is solid

- Scope/non-goals correctly bound the work; status path deferred to #539; agent-job branch
  untouched; `LoadAsync` retained for legitimate full-run callers.
- Storage decision (child table + active-work columns) consistent with precedents; rejected
  alternatives (computed columns / read-time `json_extract` / unified flagged table) correctly
  dismissed.
- Structural no-`State` invariant (D3) is a clean, type-system-enforced design.
- Task graph is a valid DAG (`T-001 → T-002 → T-003`); T-003-after-T-002 prevents breaking log
  queries for terminal runs; every task has verifiable, test-inclusive acceptance criteria.
- Backfill discipline (idempotent, preflight, ordered after #536, before-service) matches
  `run-state.md`; correctly avoids touching `State`/ETag.
- `tasks.json` is valid JSON; all `passes=false`; dependencies point only to lower-priority tasks.

<promise>PASS</promise>
