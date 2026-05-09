## Why

Workflow history is currently hard to trust because checks can also execute fixes, retry tasks, and spawn coder agents, hiding the unit that actually changed code. Clarifying that stages orchestrate, tasks execute, checks only verify, and artifacts are durable workflow files makes pipeline behavior auditable, easier to visualize, and safer to extend before more recovery policies are added.

## What Changes

- Define tasks as the only workflow units allowed to change code, run agents, run commands with side effects, repair artifacts, or record execution results.
- Define checks as read-only validators that return `CheckResult` with transient evidence in `output` and never write files, modify code, spawn coder agents, advance stages, or rerun tasks.
- Define durable artifacts as workflow files intended to be preserved with the change, such as `proposal.md`, `specs/`, `design.md`, `tasks.json`, `self-review.md`, `review.md`, and `review-self-check.md`.
- Treat build logs, test output, command stderr/stdout, transient error summaries, agent session streams, health gate results, and parsed review verdicts as execution result or check output rather than artifacts.
- Replace hidden check `fix?()` behavior with explicit fix tasks such as health-gate fix tasks, review-finding fix tasks, and plan-artifact repair tasks.
- Replace execution-style check reactions for retry and auto-fix with a simple stage-local check failure policy: failed check, optional configured fix task, re-run check, then pause/fail after max attempts.
- Make fix attempts visible in task history and UI so users can see sequences like `execute-tasks-json`, `health:build`, `fix-build-health`, and the follow-up `health:build` result.
- Allow build-stage task results to complete with an empty durable artifact list when the task only changes code or records transient execution output.
- **BREAKING**: Deprecate and remove the main `Check.fix?()` contract and stop using `retry-task` / `auto-fix` reactions as execution mechanisms.

## Capabilities

### New Capabilities



### Modified Capabilities

- workflow-engine
- pipeline-model
- change-artifacts
- web-ui

## Impact

- Workflow runner contracts: `packages/cli/src/workflow/base-stage-runner.ts`, `packages/cli/src/workflow/stage-context.ts`, `packages/cli/src/workflow/workflow-loader.ts`, and exported workflow types need clearer task, check, execution result, and failure policy semantics.
- Check implementations: `packages/cli/src/workflow/checks/index.ts`, `health-gate-check.ts`, `ai-review-check.ts`, and plan/check artifact checks must become read-only and return evidence through `CheckResult.output`.
- Stage runners: `plan-stage-runner.ts`, `build-stage-runner.ts`, `check-stage-runner.ts`, and `integrate-stage-runner.ts` need explicit task results for generation, implementation, fix, review, integration, and final-health work, including tasks with no durable artifacts.
- Persistence and APIs: `packages/cli/src/db/stage-execution-repo.ts` and any serializers/API responses that expose `taskResults` or `checkResults` must preserve durable artifact paths separately from transient execution/check output.
- Agent runtime integration: health gate and review fixing should move from check methods into named stage tasks that spawn coder sessions and record task execution results.
- UI pipeline rendering: components that display tasks, checks, artifacts, or hidden auto-fix behavior need to show failed check -> fix task -> re-check as an explicit audit trail.
- Tests should cover read-only checks, explicit fix task scheduling, empty-artifact build task results, durable artifact preservation, visible review/health fixes, and max-attempt pause/failure without introducing fallback chains.
