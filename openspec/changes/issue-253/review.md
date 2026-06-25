# Review Report

## Result: FAIL

## Repaired Items

（无）

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Runner/Grains/RunnerGrain.cs:174`
  Evidence: `ReportWorkflowResultAsync` rejects any workflow result whose `(workflowRunId, workId)` is not present in the in-memory `_outstandingWorkflowWorks` dictionary, returning `untracked` before contacting `IWorkflowGrain`. The dictionary is only populated by the current `RunnerGrain` activation in `PollOneWorkflowAsync` (`packages/server/src/Mohist.Server/Runner/Grains/RunnerGrain.cs:445`) and is not persisted or rebuilt from `WorkflowRun.WorkDelivery`. If the runner grain deactivates, the silo restarts, or the runner process reconnects and reports a previously polled work item, the result is silently dropped and the workflow remains running forever. This violates the issue's requirement that the runner-loss/report path be a reliable executor-side closeout and introduces a data-safety regression in the normal report path. [disallowed:product-behavior-change]
  SuggestedAction: Keep the outstanding set for runner-loss synthesis, but allow valid workflow reports to be translated and forwarded when the workflow grain still has matching active work, or persist/recover outstanding workflow work on activation/heartbeat repair. Add a regression test that polls workflow work, deactivates/reactivates `RunnerGrain`, then reports the original `WorkResult` and verifies the workflow advances.
  Verification: `npm test -- --filter RunnerOutstandingWorkSpecs` passes, but it only covers same-activation outstanding tracking and runner-loss synthesis; it does not cover reactivation/reconnect report recovery.
  Status: open

- [ID: item-2]
  Severity: warning
  Scope: `packages/runner/src/core/types.ts:34`
  Evidence: The acceptance criteria require runner TS `WorkItem` to mirror the server domain work item shape (`task`/`checks`, declaration/templates, no rendered execution context). The candidate instead introduces `DomainWorkItem` for that shape, while the exported `WorkItem` at `packages/runner/src/core/types.ts:99` remains the rendered dispatch envelope with `workflowRunId`, `workId`, `variables`, `projectId`, `issueNumber`, `outputs`, and other execution fields. The runtime may intentionally still consume rendered dispatches, but this does not satisfy the stated public contract/name alignment and leaves two competing "work item" concepts in the runner package. [disallowed:public-contract-change]
  SuggestedAction: Either rename the rendered runner execution envelope away from `WorkItem` and make `WorkItem` the domain mirror, or update the accepted spec/issue contract to explicitly permit `DomainWorkItem` plus rendered `WorkItem`. Add a type-level or serialization contract test covering the intended shape.
  Verification: Content inspection and grep show `DomainWorkItem` is the domain mirror, while `WorkItem` remains the rendered envelope and `connection.ts` still returns `Promise<WorkItem | null>` from rendered `WorkDispatchResponse`.
  Status: open

- [ID: item-3]
  Severity: test-gap
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/Runner/Grain/RunnerOutstandingWorkSpecs.cs:25`
  Evidence: New supervision tests cover live same-activation tracking, report removal, and unregister synthesis, but not the critical recovery path created by moving authority from `WorkflowGrain` timers to `RunnerGrain` in-memory outstanding work. The design explicitly identifies silo restart/runner disappearance as a risk, and the implementation adds another related risk: legitimate reports after runner-grain reactivation are rejected as `untracked`. This gap allowed item-1 to ship. [disallowed:test-coverage-change]
  SuggestedAction: Add tests for runner grain deactivation/reconnect and, if reminder persistence is deferred, document and test the exact accepted boundary: normal result reports must still be accepted after runner-grain reactivation, while runner-loss synthesis may require the follow-up.
  Verification: `npm test -- --filter RunnerOutstandingWorkSpecs` passed 4 tests; none deactivate or reactivate `RunnerGrain` between poll and report.
  Status: open

## Follow-up Items

- [ID: item-4]
  Severity: follow-up
  Scope: `openspec/changes/issue-253/tasks.json:23`
  Evidence: All tasks still have `passes: false` after the implementation commits, even though the candidate contains code and tests for T-001 through T-005. This is workflow metadata quality, not a product behavior defect.
  SuggestedAction: Update task completion metadata before integrate if Mohist tooling relies on it for traceability.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-5]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Runner/Grains/RunnerGrain.cs:79`
  Evidence: Runner-loss detection still uses a grain timer and in-memory `_lastHeartbeat`; the issue acceptance permits this only if reminder + persisted heartbeat is tracked as follow-up. I did not find a referenced follow-up issue number in the reviewed artifacts, but the design calls it an independent robustness bug.
  SuggestedAction: Create/link the follow-up issue for persistent reminder-based runner-loss detection before final acceptance.
  Status: out-of-scope

<promise>FAIL</promise>
