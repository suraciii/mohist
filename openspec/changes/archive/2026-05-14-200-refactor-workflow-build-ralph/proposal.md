## Why

Build is the only stage whose runtime work still lives behind `RalphExecutor`'s monolithic loop, so the unified `WorkflowRun.nextWork()` model cannot reuse Build's task loading or execute a single Build task in isolation. Splitting Ralph into loader and handler boundaries is needed now to let Build join the shared StageRunner architecture without regressing task ordering, dependency validation, retry policy, or failure handling.

## What Changes

- Split Build's dynamic Ralph runtime into `RalphTaskLoader` for reading, validating, ordering, and materializing executable Build tasks from `tasks.json`
- Introduce `RalphTaskHandler` for executing exactly one Build task, including prompt/context assembly, retry behavior, failure categorization, WIP handling, session-failure handling, learning capture, and task progress updates
- Keep `RalphExecutor` and `runRalphLoop` as compatibility wrappers so existing BuildStageRunner code paths, tests, and helper exports continue to work while delegating to the new loader/handler pieces
- Extract shared Ralph utilities such as task loading/sorting, task context building, and failure categorization into stable modules that both the legacy wrapper and future generic StageRunner path can consume
- Extend the shared task-runtime registration path so Ralph Build work can plug into the same loader/handler model already used by static stage tasks

## Capabilities

### New Capabilities

<!-- None. -->

### Modified Capabilities

- `ralph-task-execution` - Build task execution must support both the legacy Ralph loop and a split loader/single-task handler model while preserving current task semantics and recovery behavior

## Impact

- Affected runtime code in `packages/cli/src/openspec/ralph-executor.ts`, `packages/cli/src/openspec/context-assembler.ts`, `packages/cli/src/workflow/build-stage-runner.ts`, and `packages/cli/src/workflow/task-runtime/`
- Affected task-runtime contracts in `packages/cli/src/workflow/task-runtime/types.ts` and registry wiring used by StageRunner task execution
- Existing compatibility exports remain supported: `RalphExecutor`, `runRalphLoop`, `setAcpSessionRunner`, `resetAcpSessionRunner`, `readTasks`, `sortTasksByOrder`, `findNextPendingTask`, and `categorizeFailure`
- No `tasks.json` schema change, no WorkflowEngine/default StageRunner migration in this issue, and no intended change to Build behavior for ordering, `dependsOn`, retry, timeout, `session_failed`, WIP commit, or progress persistence
- Specs impact is limited to `ralph-task-execution`; this change prepares later StageRunner unification work without changing other workflow capabilities yet
