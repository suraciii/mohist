## Why

Plan, Build, Check, and Integrate now share task runtime, WorkflowRun task/check state, and runtime-added work primitives, but their execution semantics are still split across stage-specific runner subclasses. This change is needed so maintainers can alter stage behavior by changing stage definitions, work sources, handlers, checks, and policies instead of re-learning four private orchestration flows and risking inconsistent task, check, repair, approval, or rebase behavior.

## What Changes

- Extend the existing stage definition model so each stage can declare work sources, task execution policy, check policy, approval policy, repair policy, and invalidation policy in addition to static tasks and checks.
- Add registry-backed execution for stage tasks and checks, binding stage definitions to static task loading, Ralph dynamic Build task loading, runtime-added tasks, shared task handlers, and check implementations.
- Add a config-driven path to `BaseStageRunner` or an equivalent generic runner while preserving the legacy runner path as a rollback mechanism during this issue.
- Migrate Integrate, Plan, Check, and Build in that order to the config-driven runner path, validating each stage independently before making the unified runner the default.
- Preserve WorkflowRun ownership of next-work selection, stage progression, task/check result decisions, approval waiting, failure, repair scheduling, and fact-driven invalidation.
- Preserve runtime-added work such as `rebase-branch` as ordinary visible tasks that keep ordering, failure blocking, and branch-fact-driven invalidation semantics.
- Preserve existing user-visible workflow order, event names, checkpoint/state compatibility projections, and legacy runner files in this issue.

## Capabilities

### New Capabilities


### Modified Capabilities

- workflow-definition
- workflow-engine
- workflow-run
- ralph-task-execution

## Impact

- Workflow domain model: `packages/cli/src/workflow/domain/index.ts` and related persistence/application services need richer `StageDefinition` policy fields and a clear mapping from definitions to task/check execution decisions.
- Stage runner infrastructure: `packages/cli/src/workflow/base-stage-runner.ts`, `plan-stage-runner.ts`, `build-stage-runner.ts`, `check-stage-runner.ts`, `integrate-stage-runner.ts`, and runner registration need a legacy/config-driven split during migration, with the unified path becoming the default only after all stages are validated.
- Task runtime: `packages/cli/src/workflow/task-runtime/**` and Build Ralph integration must be consumed through registries and loaders, including static tasks, `RalphTaskLoader`/`RalphTaskHandler`, service-call tasks, agent-session tasks, repair tasks, and `rebase-branch`.
- Checks and approvals: `packages/cli/src/workflow/checks/**`, approval handling, repair hooks, stale review invalidation, health gates, merge readiness, and post-merge health behavior need registry/config bindings while preserving read-only check semantics.
- Workflow engine and aggregate execution: `packages/cli/src/workflow/workflow-engine.ts`, `WorkflowRun.nextWork()`, aggregate single task/check execution, checkpoint resume, materializeTasks, and no-progress detection must continue to work through the unified runner.
- Compatibility and tests: existing SSE/log event names, stage state projections, checkpoint/state systems, and legacy runners remain available; full workflow, aggregate workflow, and stage-specific Plan/Build/Check/Integrate tests need coverage for both migration safety and final default registration.
