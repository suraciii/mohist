## Why

When all build tasks are recovered from a pipeline checkpoint (server restart after crash), `RalphExecutor` skips every task via `skipTaskIds` but never increments its `completed` counter. The `zero_work` guard in `workflow-controller.ts` then fires (`completed === 0 && total > 0`), marking the issue as permanently `blocked` — even though all tasks genuinely passed. This makes any issue unrecoverable after a restart if the crash occurred after all tasks completed but before checkpoint cleanup.

## What Changes

- **RalphExecutor short-circuit**: When `skipTaskIds` covers all tasks, return a successful `RalphLoopResult` immediately with `completed` reflecting recovered count, skipping the main execution loop entirely.
- **allTasksPassed guard refinement**: Only treat `allTasksPassed` as corrupted when there are no `skipTaskIds`. When recovering from checkpoint, all-pass is the expected state — not corruption.
- **zero_work condition narrowing**: In `workflow-controller.ts`, distinguish "no work was done" from "all work was recovered from checkpoint" by checking whether `skipTaskIds` covered the full task set.
- **Checkpoint consistency cleanup**: After reading checkpoint in `runPipelineBuildStage`, verify alignment with `tasks.json`. If checkpoint tasks are already `passes=true` in the file, delete the redundant checkpoint before executing.

## Capabilities

### New Capabilities

- `checkpoint-full-recovery`: Short-circuit path in RalphExecutor when all tasks are recovered from checkpoint, returning correct completed count and skipping the execution loop.

### Modified Capabilities

- `ralph-task-execution`: `allTasksPassed` reset logic must account for checkpoint recovery — only reset when not recovering from checkpoint.
- `pipeline-model`: `zero_work` guard in workflow-controller must distinguish genuine zero-work from full-checkpoint-recovery.

## Impact

- `packages/cli/src/openspec/ralph-executor.ts` — short-circuit logic after skipTaskIds, allTasksPassed guard refinement
- `packages/cli/src/workflow/workflow-controller.ts` — zero_work condition narrowing, checkpoint consistency cleanup
- `packages/cli/tests/ralph-executor.test.ts` — new tests for full-checkpoint-recovery scenario
- `packages/cli/tests/workflow-controller.test.ts` — new tests for zero_work false positive prevention
