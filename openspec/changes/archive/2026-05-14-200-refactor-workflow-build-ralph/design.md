## Context

Build is the only workflow stage whose core task runtime is still trapped inside `packages/cli/src/openspec/ralph-executor.ts`. `runRalphLoop()` currently owns three different responsibilities at once:

- loading and validating `tasks.json`
- selecting and sequencing the next executable Build task
- executing one task attempt with retry, failure classification, WIP handling, learning capture, task persistence, and aggregate reporting

That shape worked when Build was a stage-private loop, but it now blocks the `WorkflowRun.nextWork()` model introduced in recent workflow refactors. The aggregate can already ask for one unit of work at a time, and `BuildStageRunner` already forwards `onlyTaskId` to `RalphExecutor`, but there is still no first-class single-task runtime that a shared StageRunner can call directly.

The existing code also establishes hard constraints:

- task ordering, `dependsOn` validation, deadlock behavior, retry policy, timeout handling, `session_failed`, WIP commits, and learning capture are already encoded in Ralph and must remain behaviorally stable
- public/test-facing exports such as `RalphExecutor`, `runRalphLoop`, `readTasks`, `sortTasksByOrder`, `findNextPendingTask`, `categorizeFailure`, `setAcpSessionRunner`, and `resetAcpSessionRunner` must keep working
- #199 already introduced shared `task-runtime` primitives (`ExecutableTask`, `TaskHandler`, `TaskHandlerRegistry`, `StaticTaskLoader`), so this change should extend that infrastructure rather than create another Build-only execution model

## Goals / Non-Goals

**Goals:**

- Split Ralph runtime into explicit loader and handler boundaries without changing Build's externally visible behavior.
- Make Build dynamic tasks representable as `ExecutableTask` instances that future generic StageRunner code can load and execute one at a time.
- Preserve `runRalphLoop` and `RalphExecutor` as compatibility wrappers that delegate to the new boundaries.
- Reuse the shared `task-runtime` registry model from #199 so Ralph execution is not a special-case parallel runtime.
- Extract stable Ralph utility code for task loading, dependency validation, context assembly, and failure classification so legacy and future callers use one implementation.
- Add direct tests for loader and handler behavior while keeping existing Ralph tests valid.

**Non-Goals:**

- Migrating `BuildStageRunner` to a fully config-driven generic StageRunner in this issue.
- Changing `tasks.json` schema or task ordering semantics.
- Renaming Ralph/Build workflow events or redefining workflow-stage ownership boundaries.
- Moving checkpoint ownership, stage pass/fail decisions, or workflow handoff out of the runner layer.
- Removing `RalphExecutor` or `runRalphLoop`.

## Decisions

### D1: Split Ralph into three layers: utilities, loader, and single-task handler

The current `ralph-executor.ts` file will be decomposed conceptually into:

- shared Ralph utilities: task file parsing, sorting, dependency validation, failure categorization, task list mutation helpers, learning helpers, and prompt/context assembly helpers
- `RalphTaskLoader`: reads a change's `tasks.json`, normalizes task defaults, validates dependencies, sorts tasks, and returns ordered `ExecutableTask[]`
- `RalphTaskHandler`: executes exactly one Build task and returns `StageTaskResult`-compatible output while applying the existing Ralph retry and failure policy

This keeps the split aligned with the accepted boundary for shared task runtime: loaders prepare work, handlers execute work, runners sequence work. It also keeps the most fragile Build reliability logic in one place instead of partially duplicated between `runRalphLoop()` and future StageRunner code.

**Alternatives considered:**
Keep only a `RalphTaskHandler` and let each caller read/sort/validate tasks itself. Rejected because the loader boundary is the main prerequisite for StageRunner unification; without it, later work would still need to copy `tasks.json` parsing, dependency checks, and task ordering.

### D2: Represent Ralph tasks as ordinary `ExecutableTask` values with a new shared task kind

Extend `TaskKind` in `packages/cli/src/workflow/task-runtime/types.ts` with a Ralph-specific kind such as `ralph-task`. `RalphTaskLoader` should return `ExecutableTask` objects whose `input` contains the normalized Ralph task plus any loader-resolved metadata required by the handler, for example:

- the source `Task` object from `tasks.json`
- total task count / sorted ordering context
- change paths needed for prompt assembly and learning persistence

The handler then plugs into the existing `TaskHandlerRegistry` via the same registration mechanism used by `agent-session` and `service-call` handlers. This makes Ralph a consumer of the shared task-runtime contract rather than a parallel subsystem.

The new kind should remain intentionally narrow: it is a Build dynamic task executor, not a generic dynamic-work abstraction. Future issues can generalize later if more than one stage needs this shape.

**Alternatives considered:**
Encode Ralph tasks as `agent-session` tasks and stuff Build-specific metadata into generic fields. Rejected because a Ralph task does more than run one prompt: it owns retry policy, failure classification, task-file persistence, WIP handling, and learning capture. Hiding that behind `agent-session` would make the contract less honest and harder to test.

### D3: `RalphTaskLoader` owns task-file truth and validation, but not runtime task selection policy

`RalphTaskLoader` should expose a `load(change, options?)` style API that:

- reads `tasks.json`
- applies existing normalization defaults (`attempts`, `passes`, `order`, `error`)
- optionally ignores stored `passes`/`error` when the caller wants aggregate-driven runtime truth
- validates `dependsOn` references, ordering constraints, and circular dependencies
- sorts tasks by the existing `order` rules
- emits linear `ExecutableTask[]` in Build execution order

The loader should not choose “the next pending task” by itself. That is loop/runner behavior. `runRalphLoop()` can still use `findNextPendingTask()` for legacy sequential execution, while a future generic StageRunner can select one task from the loaded list based on WorkflowRun state. This keeps loader output reusable for both old and new scheduling paths.

When validation fails, the loader should return structured validation errors rather than immediately hard-coding loop termination. The legacy wrapper can translate that into the current `RalphLoopResult` behavior, while future callers can surface the same failure through runner-native mechanisms.

**Alternatives considered:**
Make the loader return only the next ready task. Rejected because it would collapse loading and scheduling back together and would not help future materialization or direct testing of the full Build task list.

### D4: `RalphTaskHandler` owns the full single-task lifecycle, including retries and side effects

`RalphTaskHandler` should accept one loaded Ralph executable task plus `StageContext` and execute the same task-attempt lifecycle that currently lives inside the body of `runRalphLoop()`:

- build the initial prompt via shared `buildTaskContext`
- create per-task observers/session options
- invoke the ACP session runner
- classify failures with `categorizeFailure` and `FAILURE_CATEGORY_CONFIGS`
- persist attempt counts, `passes`, `error`, and durations back to `tasks.json`
- store failure learnings after failed attempts
- generate WIP resume context when timeout-with-WIP occurs
- emit existing task progress events and workflow-log entries
- optionally commit implementation changes and/or `tasks.json` updates on the success path where current Ralph does so
- report aggregate-facing task completion details through the existing `workflowApplicationService.completeTask(...)` bridge when that bridge is present

The handler returns one `StageTaskResult`, but it may also need richer internal result metadata for the legacy loop wrapper, such as `paused`, `pauseReason`, or the updated normalized task snapshot. The design should therefore use an internal handler result shape that contains a `stageTaskResult` plus Ralph-specific metadata. The public `TaskHandler` adapter can then project that to plain `StageTaskResult` for StageRunner consumers.

This preserves the reliability-critical behavior in the smallest reusable unit: one Build task execution.

**Alternatives considered:**
Move retry and pause policy up into the runner and keep the handler as “single attempt only.” Rejected because the issue explicitly requires the handler to own retry, failure classification, WIP handling, and learning; pushing those back upward would leave the hard part unsolved.

### D5: Keep `runRalphLoop()` as a compatibility orchestrator that delegates, not as the primary implementation

The legacy API surface remains, but `runRalphLoop()` should be reduced to orchestration logic:

- call `RalphTaskLoader` once to get normalized ordered tasks and any validation result
- apply legacy loop semantics such as checkpoint recovery, `skipTaskIds`, `onlyTaskId`, deadlock detection, and “all tasks already passed” short-circuiting
- select tasks in the same order as today using `findNextPendingTask()` semantics over the normalized task list
- call `RalphTaskHandler` for each chosen task
- translate handler outcomes into `RalphLoopResult`, paused-task behavior, and legacy callbacks such as `onTaskCompleted` and `onLoopComplete`

`RalphExecutor.execute()` remains a thin wrapper over that compatibility function. Tests that import the old entrypoints continue to exercise the same behavior, but the authoritative implementation for load/execute logic lives below the wrapper.

**Alternatives considered:**
Delete `runRalphLoop()` and switch BuildStageRunner/tests directly to loader/handler. Rejected because compatibility exports are an explicit requirement and because this issue is not the StageRunner cutover.

### D6: Shared Ralph utilities should move to stable modules with old exports re-exported from `ralph-executor.ts`

Several helpers are already used conceptually outside the loop body and should become stable imports:

- `categorizeFailure` and `FAILURE_CATEGORY_CONFIGS`
- task sorting/loading/validation helpers such as `readTasks`, `sortTasksByOrder`, `findNextPendingTask`, and `validateTaskDependencies`
- prompt/context helpers centered around `buildTaskContext`

The implementation may place them under `packages/cli/src/openspec/ralph/` or a similarly scoped folder, but `ralph-executor.ts` should continue re-exporting the historical names so direct imports in tests do not break. This approach supports future reuse without forcing a large immediate import-path migration.

`buildTaskContext` already lives in `context-assembler.ts`; the main change is to make it an intentional shared dependency of `RalphTaskHandler` rather than a helper reached only from the loop body.

**Alternatives considered:**
Leave all helpers in `ralph-executor.ts` and only instantiate loader/handler classes from that file. Rejected because the file would remain the de facto god module and later follow-up work would still have to peel logic back out.

### D7: `BuildStageRunner` adopts the new handler boundary only where it already supports requested single-task execution

This issue should not rewrite `BuildStageRunner` into a generic runner, but it should leave Build closer to that future state. The minimal adoption is:

- continue materializing Build tasks for WorkflowRun from `tasks.json`
- continue using `RalphExecutor.execute(change, { onlyTaskId })` as the public compatibility path
- ensure that compatibility path now delegates into `RalphTaskLoader` and `RalphTaskHandler`
- optionally add local registry wiring or helper construction so the future #201 runner can reuse the same loader/handler instances without redesign

The key is that `BuildStageRunner` should not grow new private Build-task execution logic. Any new single-task runtime behavior introduced here must live under task-runtime/Ralph modules.

**Alternatives considered:**
Have `BuildStageRunner.executeReportedTask()` call `RalphTaskHandler` directly in this issue. Rejected for now because it would partially begin the runner migration and expand scope into StageRunner behavior that the issue explicitly defers to #201.

### D8: Test the split at three levels: utility, loader/handler, and legacy wrapper compatibility

The test plan should preserve current regression coverage while proving the new boundaries directly.

Add focused tests for:

- task file normalization, sorting, and dependency validation in `RalphTaskLoader`
- success, retry, non-retryable failure, timeout-with-WIP, and `session_failed` handling in `RalphTaskHandler`
- shared failure categorization and context-building utilities where extracted modules introduce new seams
- `runRalphLoop()` compatibility behavior for `onlyTaskId`, `skipTaskIds`, deadlock handling, validation failure, and callback/reporting translation

Existing tests that import old helpers should remain valid. The design goal is that the same public behavior is now covered both from the old wrapper boundary and from the new internal boundary.

**Alternatives considered:**
Rely only on existing Ralph loop tests. Rejected because the whole point of the refactor is to create stable new seams; without direct tests those seams would be free to drift before #201 consumes them.

## Risks / Trade-offs

- [Loader and loop responsibilities may blur again during implementation] -> Keep `RalphTaskLoader` strictly about reading/validating/ordering and leave next-task selection plus checkpoint recovery in the compatibility wrapper.
- [Single-task handler may accumulate too much loop-only behavior] -> Allow an internal richer Ralph result type, but keep the exported `TaskHandler` adapter focused on one task execution result.
- [Task-file persistence and WorkflowRun persistence can diverge] -> Preserve existing write order and reuse current aggregate reporting helpers so this issue changes structure, not persistence semantics.
- [Compatibility exports may accidentally change import paths or behavior] -> Re-export old helper names from `ralph-executor.ts` and keep legacy tests in the validation set.
- [Adding a new `TaskKind` could encourage premature generalization] -> Name/document it as Ralph/Build-specific and defer broader dynamic-task abstractions until another stage actually needs them.
- [WIP/session failure logic is easy to regress because it sits inside retry flow] -> Add focused handler tests that assert timeout-with-WIP and `session_failed` categorization separately from generic failure cases.

## Migration Plan

1. Extract stable Ralph utility modules for task file helpers, dependency validation, failure categorization, learning persistence, and prompt/context assembly reuse.
2. Introduce `RalphTaskLoader` that converts a change's `tasks.json` into ordered Ralph `ExecutableTask` values and structured validation output.
3. Extend shared `task-runtime` types/registry with a Ralph-specific task kind and add `RalphTaskHandler` plus focused direct tests.
4. Reimplement `runRalphLoop()` in terms of the loader and handler while preserving `RalphLoopResult`, callbacks, checkpoint recovery behavior, and helper exports.
5. Keep `BuildStageRunner` on the same public `RalphExecutor.execute(...)` call path, but let that path run entirely through the new boundaries.
6. Run focused Ralph tests and Build workflow tests to confirm unchanged behavior for ordering, `dependsOn`, retries, timeout/WIP, session failure, and aggregate task reporting.

Rollback is low-risk because the public entrypoint stays the same. If the split regresses behavior, the wrapper can temporarily be pointed back to the old inlined loop implementation while leaving the new loader/handler code unused.

## Open Questions

- Should `RalphTaskLoader` live under `packages/cli/src/workflow/task-runtime/` with the other loaders, or under an `openspec/ralph/` subfolder beside the compatibility wrapper, with only a thin adapter exposed to task-runtime consumers?
- Does the future #201 StageRunner cutover want a formal `TaskLoader` interface added to `task-runtime/types.ts`, or is a concrete `RalphTaskLoader` class sufficient for this issue as long as it matches the existing `StaticTaskLoader` boundary?
