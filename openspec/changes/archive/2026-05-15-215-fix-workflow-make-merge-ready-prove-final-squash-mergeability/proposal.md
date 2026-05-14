## Why

Check-stage `merge-ready` currently reports PASS from weak rebase/worktree status facts, so users can approve an issue that Mohist's final Integrate squash merge will later reject. This change is needed to restore trust in the approval surface by making `merge-ready` prove the same mergeability that Integrate depends on, before side-effectful delivery begins.

## What Changes

- Replace the current `merge-ready` inference based on fast-forward and active rebase-conflict status with a read-only squash-merge preflight equivalent to Mohist's final Integrate merge semantics.
- Fail `merge-ready` when the current issue branch cannot be cleanly squash-merged into the current base branch, even if no rebase is in progress and `conflictingFiles` is empty.
- Record structured mergeability evidence including base/head/merge-base SHAs, target branch, checked merge strategy, `canMerge`, and conflict files.
- Bind Check approval to the current mergeability snapshot so approval is rejected when the base branch or candidate head has changed since `merge-ready` passed.
- Add an Integrate preflight before spec sync/archive or other side-effectful delivery work when the approved mergeability snapshot is missing or stale.
- Preserve the actual Integrate squash merge as the final authority and report structured conflict evidence if a race occurs after preflight.
- Add regression coverage for the #207 failure class where `merge-ready` must fail for a branch whose final squash merge conflicts while ordinary worktree conflict state is empty.

## Capabilities

### New Capabilities


### Modified Capabilities

- pipeline-model
- workflow-engine
- workflow-run
- workflow-definition
- worktree-manager
- http-api
- web-ui

## Impact

- Affects Check-stage merge readiness in `packages/cli/src/workflow/checks/merge-ready-check.ts` and the older/internal `merge-readiness` path so the user-visible gate uses final squash-merge semantics rather than rebase-conflict absence.
- Affects `packages/cli/src/git/worktree-manager.ts` and `packages/cli/src/workflow/stage-context.ts` to add a non-mutating merge preflight that can inspect base/head/merge-base SHAs, run an equivalent squash merge safely, clean up temporary state, and return conflict files.
- Affects Check approval construction and validation in `packages/cli/src/workflow/base-stage-runner.ts` and `packages/cli/src/api/issues.ts` because approval must carry and revalidate the mergeability snapshot in addition to the review snapshot.
- Affects Integrate ordering in `packages/cli/src/workflow/integrate-stage-runner.ts` so stale or missing mergeability evidence is refreshed before `integrate:spec-sync`, `integrate:archive-change`, or `integrate:merge` mutate project state.
- Affects persisted check/output evidence through WorkflowRun, check suites, stage execution records, logs, CLI, API, and UI surfaces that display `merge-ready` status and diagnostics.
- Affects tests around Check-stage gates, approval staleness, Integrate preflight, final merge failure reporting, and git worktree merge simulation.
- No new external dependencies are expected.
