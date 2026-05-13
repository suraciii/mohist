## Why

Plan, Build, Check, and Integrate currently each own their own task dispatch, repair-task entrypoints, safe event emission, and workflow-log write paths, so even small fixes to repair behavior or task execution semantics must be repeated across multiple runners. This change is needed now because the StageRunner unification work cannot safely move toward shared stage definitions until the minimum task execution contract is centralized and legacy runners stop hiding critical behavior in private branches.

## What Changes

- Move duplicated runner-level emit/log helpers into shared stage context infrastructure so runners can reuse the same safe `emit` and `log` behavior without changing event names or workflow-log semantics.
- Define the minimum shared task execution contract around task definition plus `StageContext` input and normalized `StageTaskResult`-style output, while keeping stage progression, checkpointing, and approval decisions in the runner/workflow domain.
- Introduce adapter-based task execution for existing repair and fix paths so legacy entrypoints such as plan repair, review repair, merge repair, and stage health fixes can route through shared handler infrastructure without removing current runner paths.
- Add a minimal static task runtime that can express Plan, Check, and Integrate static task definitions and resolve them into executable tasks without taking over Build or Ralph task loading.
- Add focused non-Build handlers for agent-session tasks and service-call tasks so Plan/Check artifact generation and Integrate service steps can share a common execution boundary before any StageRunner cutover.
- Preserve the current workflow architecture by keeping `WorkflowEngine`, legacy runners, Ralph execution, SSE event names, and default runner registration unchanged in this issue.

## Capabilities

### New Capabilities

- Shared non-Build task runtime primitives can express executable tasks, resolve static Plan/Check/Integrate task input, and execute those tasks through a normalized handler contract.

### Modified Capabilities

- Stage runners share one safe stage-scoped `emit` / `log` boundary instead of maintaining private helper implementations.
- Existing repair and fix entrypoints route through shared adapters and handlers while preserving legacy exported entrypoints and runner ownership of stage control.
- Minimal Plan, Check, and Integrate execution paths can delegate static task preparation and execution through shared runtime components without changing `WorkflowEngine` registration, runner classes, or Ralph execution.

## Impact

- Workflow runtime code in `packages/cli/src/workflow/`, especially `stage-context.ts`, `base-stage-runner.ts`, `plan-stage-runner.ts`, `check-stage-runner.ts`, `integrate-stage-runner.ts`, and shared repair/fix task modules such as `health-fix-task.ts`, `plan-repair-task.ts`, and `review-fix-task.ts`.
- New shared task runtime types and adapters for static task definitions, executable task loading, task handlers, and legacy repair/fix compatibility paths.
- Existing Plan and Check agent-session task execution paths and Integrate service-call execution paths, which should reuse shared handler contracts without changing user-visible stage behavior.
- Workflow logging and fire-and-forget event emission behavior, which must stay behaviorally equivalent while moving from runner-private helpers to shared context helpers.
- Focused tests covering Plan, Check, Integrate, and repair/fix task execution contracts, including current task ids such as review repair, merge repair, and stage health fixes.
