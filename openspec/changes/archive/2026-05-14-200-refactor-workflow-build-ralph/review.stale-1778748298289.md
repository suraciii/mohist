## Findings

1. Error: `runRalphLoop()` drops the handler's failure category and re-classifies from raw text, which changes Ralph failure semantics for `session_failed` and `timeout_with_wip` in workflow reporting paths. `executeRalphTask()` already computes and returns `lastCategory` using the full handler context at `packages/cli/src/openspec/ralph/handler.ts:168`, `packages/cli/src/openspec/ralph/handler.ts:217`, `packages/cli/src/openspec/ralph/handler.ts:233`, and `packages/cli/src/openspec/ralph/handler.ts:261`, but the compatibility wrapper discards it at `packages/cli/src/openspec/ralph-executor.ts:753-758` and then re-runs `categorizeFailure(lastError, {})` at `packages/cli/src/openspec/ralph-executor.ts:803`. That reclassification turns `failureKind: 'session_failed'` into `timeout` for errors like `Session liveness probe timed out`, and turns `timeout_with_wip` back into plain `timeout`, so logs and aggregate/reporting consumers no longer see the original Ralph category. Suggested fix: carry `lastCategory` through `handlerResult` and use it directly for `task_failed` logging and any downstream reporting/decision paths.

2. Error: the new single-task Ralph runtime does not emit terminal task progress events on the shared handler boundary. `executeRalphTask()` only calls `emitTaskUpdate` for `started` and `retrying` at `packages/cli/src/openspec/ralph/handler.ts:117-125` and `packages/cli/src/openspec/ralph/handler.ts:236-244`. `completed` and `failed` are emitted only by the legacy wrapper at `packages/cli/src/openspec/ralph-executor.ts:767` and `packages/cli/src/openspec/ralph-executor.ts:899`. That means any future StageRunner using `createRalphTaskTaskHandler()` directly through the shared registry at `packages/cli/src/workflow/task-runtime/ralph-task-handler.ts:35-43` will lose terminal `stage_task_update` / `ralph_task_update` parity compared with the compatibility loop. Suggested fix: emit `completed`/`failed` inside `executeRalphTask()` before returning, then let the legacy loop suppress duplicates when it wraps the handler.

## Spec Compliance

- Requirement: Ralph-style task loop execution
  - PASS: legacy compatibility path now loads through `RalphTaskLoader`, validates dependencies before execution, and sequences work through the loop at `packages/cli/src/openspec/ralph-executor.ts:407-446` and `packages/cli/src/openspec/ralph-executor.ts:626-750`.
  - PASS: single-task execution is exposed as ordered executable work via `RalphTaskLoader.load()` and `ExecutableTask.kind = 'ralph-task'` at `packages/cli/src/openspec/ralph/loader.ts:27-80`, with shared-registry execution wired at `packages/cli/src/workflow/task-runtime/registry.ts:23-45`.

- Requirement: Task failure handling with retry
  - FAIL: retry and failure handling remain handler-owned, but failure classification is not preserved end-to-end because the compatibility wrapper overwrites the handler category at `packages/cli/src/openspec/ralph-executor.ts:803`.

- Requirement: Task status persistence
  - PASS: handler updates `passes`, `attempts`, `error`, and `durations` in `tasks.json` without schema changes at `packages/cli/src/openspec/ralph/handler.ts:149-177` and `packages/cli/src/openspec/ralph/handler.ts:264-286`; compatibility helpers still exist in `packages/cli/src/openspec/ralph-executor.ts:25-129` and `packages/cli/src/openspec/ralph-executor.ts:192-385`.

- Requirement: REQ-RTE-001 Task attempts consume session failure results
  - FAIL: the handler correctly records session liveness failures as failed attempts at `packages/cli/src/openspec/ralph/handler.ts:167-177`, but the compatibility layer misreports the same failure as a generic timeout by discarding `lastCategory` at `packages/cli/src/openspec/ralph-executor.ts:753-758` and recomputing it at `packages/cli/src/openspec/ralph-executor.ts:803`.

## Verification

- PASS: `npm run build` in `packages/cli`.
- PARTIAL: focused Ralph tests exist for loader, handler, and compatibility paths in `packages/cli/tests/openspec/ralph-loader.test.ts`, `packages/cli/tests/openspec/ralph-handler.test.ts`, and `packages/cli/tests/openspec/ralph-loop-compatibility.test.ts`.
- PARTIAL: `npm test -- ralph-handler.test.ts ralph-loader.test.ts ralph-loop-compatibility.test.ts ralph-executor.test.ts` hit the 120s tool timeout in this environment, so I could not verify the full focused test run to completion from the terminal.

<promise>FAIL</promise>
