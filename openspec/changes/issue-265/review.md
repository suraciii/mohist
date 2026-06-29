# Review Report

## Result: FAIL

## Repaired Items

(none)

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Failure.cs`
  Evidence: `RerunFromStage` treats any stage in `ReachedStageIds` as eligible (`WorkflowRun.Failure.cs:144-169`) and then scans active work only in `[targetIdx..end]` (`WorkflowRun.Failure.cs:170-181`) before switching `CurrentStageId` to the target (`WorkflowRun.Failure.cs:204`). After a successful backward rerun, this lets a caller immediately jump to a previously reached later stage while the newly restarted earlier stage still has pending/running work. Example: complete through `integrate`, call `rerun-from-stage(plan)` so `plan` is restarted and `build`/`integrate` are fresh, then call `rerun-from-stage(integrate)`. The second call is accepted because `integrate` is in `ReachedStageIds`, but it ignores active `plan` work because `plan` is before the target. That can orphan or bypass earlier active control state, violating the issue requirement that active work and stage control remain consistent after the operation. The current tests explicitly assert later lifetime reachability remains selectable (`RerunFromStageSpecs.cs:194-207`) but do not cover the active earlier-work case. [disallowed:product-behavior]
  SuggestedAction: Either make eligibility/progress relative to the current valid control frontier, or, if lifetime reachability is intended, reject a target when any stage before it is not a completed valid progress stage or has active work. Add regression coverage for repeated rerun-from-stage calls where a prior backward rerun has left earlier pending/running work.
  Verification: Add a domain or grain test for `rerun-from-stage(plan)` followed by `rerun-from-stage(integrate)` before `plan` completes, and assert the second operation is rejected with state unchanged and no orphan work/lock side effects.
  Status: open

- [ID: item-2]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Workflow/Services/WorkflowEventQuerier.cs`
  Evidence: Invalidated control-history filtering infers invalidation points from only the already-limited event slice returned by `_events.ListAsync(workflowRunId, limit, ct)` (`WorkflowEventQuerier.cs:42-44`). The filter only marks a rerun when it sees a duplicate `StageStarted` in that slice (`WorkflowEventQuerier.cs:61-73`). With a small `limit`, or any page that includes old invalidated task/check events and the new rerun `StageStarted` but excludes the original `StageStarted`, the rerun marker is treated as the first start and `validFromEventId` remains empty, so old invalidated task/check events are returned. This violates the acceptance criterion that timeline reads must not surface invalidated old attempt history. Existing tests use `limit=200` (`WorkflowRerunFromStageApiSpecs.cs:168,195`), which masks this pagination/windowing edge case. [disallowed:product-behavior]
  SuggestedAction: Determine invalidation watermarks from the full workflow event history, a separate marker query, or persisted attempt/invalidation metadata before applying the requested response limit. Add a regression test with a low limit that excludes the original `StageStarted` but includes invalidated task/check events around the rerun marker.
  Verification: Call `/api/workflow-runs/{wrId}/events?limit=<small>` and `/api/projects/{projectId}/issues/{number}/events?limit=<small>` after a rerun-from-stage and assert invalidated task/check/stage control events are still omitted.
  Status: open

- [ID: item-3]
  Severity: test-gap
  Scope: regression coverage
  Evidence: The new tests cover the main happy path, unknown/never-reached stage, active work inside the invalidation range, lock release, runtime variables, API status codes, and CLI body shape. They do not cover two important edge cases exposed by the implementation: repeated rerun-from-stage calls that make a lifetime-reached later stage selectable while earlier restarted work is still active, and low-limit timeline/event reads where the original `StageStarted` is outside the queried window.
  SuggestedAction: Add targeted regression tests for the two scenarios above before accepting the candidate.
  Verification: New tests should fail on the current snapshot and pass after item-1/item-2 are fixed.
  Status: open

## Follow-up Items

- [ID: item-4]
  Severity: follow-up
  Scope: `openspec/changes/issue-265/design.md`, `openspec/changes/issue-265/tasks.json`, `packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.cs`
  Evidence: The implementation adds a persisted `WorkflowRun.ReachedStageIds` field (`WorkflowRun.cs:46`) and tests lifetime reachability after a backward rerun, while `design.md` explicitly rejected a high-water/lifetime field and `tasks.json` says no new persisted field. This is not a product failure by itself, but it creates traceability drift between the reviewed candidate and the workflow artifacts.
  SuggestedAction: Update the design/task artifacts to reflect the final semantics, or change the implementation back to the position/current-frontier model. This should be resolved together with item-1 because the behavior choice affects correctness.
  Status: follow-up

## Pre-existing or Out-of-scope Items

(none)

Verification run: `npm test` passed. The .NET suite reported 3090 passed, 13 skipped; web Vitest reported 2941 passed, 1 skipped; runner Vitest reported 786 passed, 23 skipped. `git diff --check master...HEAD` also passed.

<promise>FAIL</promise>
