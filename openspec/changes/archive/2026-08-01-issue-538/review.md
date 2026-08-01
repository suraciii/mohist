# Review — issue-538 (log path eliminates WorkflowRun State full-load read)

Re-review after the fix commit `250f02242` addressed the prior pass's F1/F2.
Reviewed against the issue's acceptance criteria and the plan artifacts under
`openspec/changes/issue-538/` (`proposal.md`, `design.md`, `specs/`, `tasks.json`).
Build + full test suite run locally at current HEAD: `npm test` is green
(Server UnitTests 1736, SpecTests 3637, ArchTests 51, Workflow.Definition 175,
Cli 1509, Runner 1510, Slack 8); `TreatWarningsAsErrors` clean.

## Prior findings — resolution status

### F1 (stale design-doc gap bullet) — RESOLVED

`design/workflow/run-state.md` no longer lists the "日志路径把整载读当廉价查询"
gap bullet. `proposal.md`'s promised Impact ("the gap item moves to closed") is
now delivered: the 现状差距 section no longer misdescribes the log path as a
full-load reader, since the upload and query paths now read the run-work
projection. Confirmed at `design/workflow/run-state.md:82-84` (bullet removed;
remaining gaps unaffected).

### F2 (`SingleOrDefaultAsync` throw risk) — RESOLVED

`WorkflowRunWorkProjection.ResolveTaskIdAsync`
(`Infrastructure/Data/Workflow/WorkflowRunWorkProjection.cs:87`) now uses
`FirstOrDefaultAsync`, matching the prior `ResolvePublishScopeAsync` first-match
semantics and removing the latent `InvalidOperationException` if two map rows
ever shared a `WorkId`. No regression in the read tests.

## What is solid (unchanged from prior review, still holds)

- **All three `State` deserializations on the log path are gone.**
  `TaskLogService` depends only on `IWorkflowRunWorkProjection`; the
  `WorkflowRunQuerier` and `Workflow.Domain.Run` imports are removed. The
  interface exposes no State-deserializing member, so the no-`State` invariant
  is type-system-enforced.
- **Projection read-model is correct** — run-wide `(TaskId, WorkId)` map with
  `EffectiveWorkId = WorkId ?? Id`, plus the single active-work identity; matches
  the old `FindActiveWork` semantics (`WorkflowRun.Work.cs:65-91,216-239`).
- **Write-maintenance on the single funnel + delete** —
  `WorkflowRunStore.StageRunAsync`/`DeleteAsync` rebuild/clear the projection
  (`WorkflowRunStore.cs:126-208`); covered by `WorkflowRunStoreProjectionSpecs`.
- **Backfill upgrader sound** — preflight + single-transaction write, leaves
  `State`/`ETag` untouched, ordered after #536 in `DatabaseInitializer.cs`;
  covered by `WorkflowRunWorkProjectionDataUpgraderSpecs`.
- **Structural no-`State` read invariant tested** at the implementation layer
  (`WorkflowRunWorkProjectionTests` — SQL interceptor asserts no projection read
  references `State`) and the service layer (`TaskLogServicePersistThenPublishSpecs`
  — fake projection, incl. the `ProjectIdLookups == 0` assertion proving
  `GetProjectIdAsync` is skipped when `taskId` is null).
- Acceptance, fan-out scope, empty-page-on-miss, and the agent-job ownership
  branch are all preserved.

## Issue acceptance criteria — all met

- taskId↔workId mapping + active-work gate read the projection, not State. ✅
- Both full-load points identified and eliminated (upload active-work gate +
  publish-scope; query taskId→workId). ✅
- Log external contract unchanged (acceptance, fan-out, pagination). ✅
- Regression coverage asserts the log path no longer triggers State
  deserialization. ✅

## Verdict

Both prior findings are resolved, no new issues were introduced, and the change
fully meets the issue's acceptance criteria and the `tasks.json` task graph.

<promise>PASS</promise>
