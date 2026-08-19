# Review

## Verdict

**FAIL**

## Must-Fix Findings

### M-1: Downstream tasks can be claimed while a required lane has no durable pass

`VerificationLaneGate.IsClaimableLaneTask` returns `true` for every non-lane task (`packages/server/src/Mohist.Server/Workflow/Services/VerificationLaneGate.cs:120-123`). `WorkflowRun.NextWork` applies that predicate only to the first pending task (`packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Work.cs:57-66`). Consequently, a lane-enabled build with a pending downstream task such as GitHub PR `push`, but with a missing lane attempt or another non-passing lane that has no pending retry task, exposes `push` for dispatch. `CanAdvanceBuildStage` still returns false, but that check runs only after the downstream task has already executed, so it does not protect the side effect.

This violates the issue acceptance criteria that every required lane must have an observable pass before workflow advancement and that recovery must not permit downstream push/review/merge effects before all lanes pass. It also violates the lane specification's all-pass gate for a missing lane. Add a regression covering a lane-enabled build with a missing/non-passing lane and a pending downstream task, then make ordered dispatch block downstream tasks until the complete lane catalog has durable pass outcomes while preserving pre-lane orchestration and lane-recovery helper dispatch.

## Dimension Sweep

- **Issue acceptance criteria re-read before reviewing the diff — checked, no issue.** The canonical issue body and comment were read first; the review target is the bounded six-lane verification and recovery change.
- **Coverage — FAIL.** The six built-in commands, per-lane budgets, durable lane projection, snapshot binding, timeout classification, ordered lane recovery, stale-report fencing, and downstream clean/recovery flows are covered. The missing/non-passing lane plus pending downstream-task path in M-1 is not covered and is not protected by the current dispatch gate.
- **Correctness — FAIL.** M-1 permits a downstream side effect before all required lanes have durable pass evidence.
- **Consistency with surrounding codebase and conventions — checked, no separate issue.** The implementation reuses existing task attempts, report fencing, workflow snapshots, Runner timeout results, and the result journal without changing the generic task status protocol, resource containment, or Runner slot policy.
- **Tests — FAIL for the changed contract.** Verification completed successfully for the focused suites: Server UnitTests `2962/2962`, issue-625 binding/recovery specs `12/12`, and focused Runner suites `64/64`, plus Runner production/test typechecks. None of these tests asserts that a pending downstream task remains blocked when a required lane is missing or non-passing.

## Observations

- `WorkflowQuerier.GetStatusAsync` still resolves the live profile (`packages/server/src/Mohist.Server/Workflow/Services/WorkflowQuerier.cs:41-58`). For an old pre-snapshot run whose build stage is not initialized, `WorkflowStatusMapper.MapTasks` can therefore display the post-activation six-lane definition even though stage initialization uses the retained aggregate definition. `VerificationLanes` remains null and the run is not made to wait, so this is a status accuracy issue rather than a must-fix for the issue gate.
- `packages/runner/tests/workflow-profile.spec.ts` writes hard-coded virtual profile fixtures in `readProfile` rather than loading the two built-in YAML files. The Server profile tests do exercise the actual parsed built-ins, so this is a test-maintenance and coverage limitation, not an additional acceptance failure.

<promise>FAIL</promise>