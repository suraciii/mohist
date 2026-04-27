## Why

The Ralph executor ignores `dependsOn` at runtime — `findNextPendingTask()` sorts tasks by `order` only and never checks whether a task's declared dependencies have passed. The prompt instructions for generating `dependsOn` are weak, and there is no programmatic validation. This causes tasks to execute out of dependency order (e.g., API routes before repo methods, tests before the modules they test), leading to avoidable failures and retries.

## What Changes

- **Executor: dependency-aware task scheduling** — `findNextPendingTask()` will only select a task whose `dependsOn` tasks have all passed, falling back to `order` for tie-breaking among ready tasks
- **Executor: DAG validation on load** — when reading `tasks.json`, validate that `dependsOn` references exist, form a DAG (no cycles), and point only to lower-order tasks
- **Prompt: strengthen dependency generation** — enhance `tasks.md` prompt to explicitly require dependency analysis and provide examples of correct dependency chains
- **Self-review: add dependency completeness check** — add a checklist item verifying that every non-first task has at least one `dependsOn` entry and that no cycles exist

## Capabilities

### New Capabilities

- `task-dependency-validation` — programmatic validation of task dependency graph on tasks.json load (reference existence, DAG check)

### Modified Capabilities

- `ralph-task-execution` — task selection must respect `dependsOn` in addition to `order`; next task must have all dependencies passed

## Impact

- `packages/cli/src/openspec/ralph-executor.ts` — `findNextPendingTask()`, `sortTasksByOrder()`, task loading logic
- `packages/cli/src/agents/prompts/artifacts/tasks.md` — dependency generation instructions
- `packages/cli/src/agents/prompts/artifacts/self-review.md` — dependency completeness checklist
- `packages/cli/src/agents/prompts/planner-self-review.yaml` — dependency validation criteria
