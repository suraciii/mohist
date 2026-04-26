## Why

Server restart after crash or deployment causes the pipeline to re-run entire stages from scratch, discarding completed intermediate work (e.g., plan stage's proposal, specs, design). This wastes LLM tokens, adds minutes of delay, and frustrates users who see finished artifacts regenerated identically.

## What Changes

- Add a `pipeline_checkpoint` table to persist stage-internal sub-step progress (which plan rounds completed, which build tasks passed).
- Write a checkpoint after each sub-step completes in `WorkflowController.runPlanStage()` and `runPipelineBuildStage()`.
- On stage entry, read checkpoint and skip already-completed sub-steps, resuming from the first incomplete one.
- Clear checkpoint when a stage completes or an issue finishes.
- Change `recoverIssues()` to mark orphaned issues as `interrupted` (new status) instead of `blocked`, preserving stage and checkpoint so `reopen` can resume from the exact interruption point.
- Frontend displays `interrupted` status with a "pipeline was interrupted, click to resume" hint.

## Capabilities

### New Capabilities

- `stage-checkpoint`: Persist and read stage-internal progress (plan rounds, build tasks) so pipeline can resume mid-stage after interruption.

### Modified Capabilities

- `reopen-resume`: Reopen of an `interrupted` issue SHALL use checkpoint to resume from the exact sub-step, not restart the stage from scratch. Remove the "reset stage to Draft" behavior for interrupted issues.
- `pipeline-model`: Pipeline stages SHALL support checkpoint-based resumption semantics — a stage is re-entrant and idempotent with respect to already-completed sub-steps.

## Impact

- `packages/cli/src/workflow/workflow-controller.ts` — checkpoint read/write, round-skip logic in `runPlanStage()`
- `packages/cli/src/openspec/ralph-executor.ts` — checkpoint integration for build task progress
- `packages/cli/src/services/agent-runner-service.ts` — `recoverIssues()` marks `interrupted` instead of `blocked`
- `packages/cli/src/types/index.ts` — `IssueStatus` gains `Interrupted`
- `packages/cli/src/db/` — new `pipeline-checkpoint-repo.ts`, migration for `pipeline_checkpoint` table
- `packages/cli/src/api/issues.ts` — reopen handles `interrupted` status with checkpoint-aware resume
- `web/src` — UI displays interrupted state with resume prompt
