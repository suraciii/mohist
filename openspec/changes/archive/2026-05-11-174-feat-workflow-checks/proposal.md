## Why

`runChecksPhase` currently stops at the first non-pass result, so users only see the first broken check in a stage instead of the full health picture. This slows repair into a trial-and-error loop and is especially costly now that stage checks mix artifact completeness, merge/review decisions, and approval state in the same visible boundary.

## What Changes

- Change stage check execution from first-failure short-circuiting to collecting the current phase's check results before deciding how to handle failures.
- Preserve `user-approval` as a read-only verifier of existing approval state: `approved` passes, `awaiting` or missing stays pending, and `rejected` fails.
- Treat pending approval as an awaiting-approval stop, not as a repairable failure.
- Run configured fix tasks only for failed or errored non-approval checks, record those fix task results explicitly, and re-run the repaired checks.
- Preserve the existing stage-local repair sequencing contract after a fix so later checks still run against the updated state rather than being skipped forever.
- Surface final phase output as a complete diagnostic summary showing passed, failed, pending, and repaired checks together with the repair attempts that were made.

## Capabilities

### New Capabilities

- None

### Modified Capabilities

- `workflow-engine` — Check-phase semantics change from first-failure reporting to full phase result collection with explicit post-collection repair handling and approval-pending treatment.
- `pipeline-model` — Stage stop conditions and visible check evidence change so a stage can present complete check results while still pausing locally for approval or failed repairable checks.

## Impact

- Core sequencing changes in `packages/cli/src/workflow/base-stage-runner.ts`, especially `runChecksPhase`, failure dispatch, fix-task execution, and recheck continuation.
- Existing stage runners such as `packages/cli/src/workflow/plan-stage-runner.ts` and `packages/cli/src/workflow/check-stage-runner.ts` keep their business rules and failure policies, but rely on the new collection-first execution semantics.
- Approval behavior remains centered in `packages/cli/src/workflow/checks/user-approval-check.ts` and existing approval-state handling; this change does not move approval request creation into checks.
- Persisted check visibility in stage execution and stage state flows must continue to represent complete per-phase results, including pending approval and fix task history.
- No new external dependencies or public APIs are expected; regression coverage should focus on multi-failure phases, approval-pending phases, and fix-then-recheck ordering.
