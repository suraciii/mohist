## Why

`WorkflowController` (908 lines) mixes six distinct responsibility domains—state machine coordination, Plan/Build/Check stage logic, Git operations, ACP session management, and Checkpoint management—making it a high-risk change point. This refactoring splits it into focused, composable units (StageRunner interface + per-stage runners + shared AcpRoundRunner and CheckpointManager) without changing any observable behavior.

## What Changes

- Replace `WorkflowController` with `WorkflowEngine` (state machine loop only, ~80 lines)
- Introduce `StageRunner` interface: `canHandle(stage) → boolean`, `run(ctx) → Promise<StageRunResult>`
- Create `PlanStageRunner` (~150 lines), `BuildStageRunner` (~120 lines), `CheckStageRunner` (~60 lines)
- Extract `Check` interface with implementations: `BuildTestCheck` (~85 lines), `MergeReadyCheck` (~59 lines), `AiReviewCheck` (~120 lines)
- Create `AcpRoundRunner` (~120 lines) to unify multi-round ACP session lifecycle across Plan and AiReviewCheck
- Create `CheckpointManager` (~60 lines) to unify checkpoint read/verify/upsert/delete across all stages
- Create `GitCommitter` (~40 lines) for build artifact commit logic
- Create `stage-context.ts` for `StageContext` and `StageRunResult` types
- **BREAKING**: Delete `workflow-controller.ts` in its entirety
- Replace `any` type dependencies on `worktreeManager` and `projectRepo` with concrete interface types

## Capabilities

### New Capabilities

_(none — this is a pure code restructuring with no new behavior)_

### Modified Capabilities

_(none — no observable capability changes; all existing tests remain valid)_

## Impact

- **Files created**: `workflow/workflow-engine.ts`, `workflow/stage-context.ts`, `workflow/plan-stage-runner.ts`, `workflow/build-stage-runner.ts`, `workflow/check-stage-runner.ts`, `workflow/checks/index.ts`, `workflow/checks/build-test-check.ts`, `workflow/checks/merge-ready-check.ts`, `workflow/checks/ai-review-check.ts`, `workflow/acp-round-runner.ts`, `workflow/checkpoint-manager.ts`, `workflow/git-committer.ts`, `workflow/utils.ts`
- **File deleted**: `workflow/workflow-controller.ts`
- **Module path change**: `WorkflowController` → `WorkflowEngine` in all imports
- **No API or behavior change**: all existing tests pass without modification
