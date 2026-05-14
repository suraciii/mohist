## Findings

1. Error: `RalphTaskHandler` does not own task progress persistence, so the split single-task path does not satisfy the spec's handler boundary or persistence requirement.
File: `packages/cli/src/openspec/ralph/handler.ts:47-252`
Evidence: `executeRalphTask()` returns a `StageTaskResult`, stores failure learnings, and optionally builds WIP context, but it never reads or writes `change.tasksPath`, never updates `passes`, `attempts`, `error`, or `durations`, and never commits `tasks.json`. Those updates still happen only in the legacy wrapper via `onAttemptUpdate` and post-handler branches in `packages/cli/src/openspec/ralph-executor.ts:713-716`, `763-765`, and `869-870`.
Spec impact: FAIL for `specs/ralph-task-execution/spec.md` Requirement `Task status persistence` and for the acceptance criterion that `RalphTaskHandler` owns single-task execution, retry, failure classification, learning, WIP handling, session failure handling, and progress updates.
Suggested fix: Move `tasks.json` mutation/persistence into `executeRalphTask()` or a helper it owns. The handler should update the loaded task's `attempts`, `passes`, `error`, and duration fields on every attempt/result using `change.tasksPath`, then return normalized result metadata to the wrapper instead of relying on `runRalphLoop()` callbacks for persistence.

2. Error: `ralph-task` was added as a type only; it is not wired into the shared task-runtime registry or executable task flow.
File: `packages/cli/src/workflow/task-runtime/types.ts:3-85`, `packages/cli/src/workflow/task-runtime/index.ts:1-29`, `packages/cli/src/openspec/ralph/loader.ts:13-57`
Evidence: `TaskKind` includes `'ralph-task'`, but there is no registry registration or consumer lookup anywhere in `packages/cli/src/workflow/`; a repo search only finds `createTaskHandlerRegistry` in `types.ts`. `RalphTaskLoader.load()` returns custom `RalphLoadedTask[]`, not `ExecutableTask[]` with `kind: 'ralph-task'`, so the new runtime cannot be consumed through the shared handler contract described in the design/spec.
Spec impact: FAIL for `specs/ralph-task-execution/spec.md` Scenario `Single Build task can execute through shared task runtime` and task acceptance criteria requiring a Ralph-specific task kind registered through the shared task-runtime registry.
Suggested fix: Have `RalphTaskLoader` materialize `ExecutableTask` values with `kind: 'ralph-task'` and register a `TaskHandler` adapter for that kind in the shared registry path. Then ensure the single-task Build path can execute through `registry.get('ralph-task')` rather than only through `runRalphLoop()`.

## Acceptance Criteria

1. PASS: `RalphTaskLoader` reads `tasks.json`, validates dependencies, sorts tasks, and exposes ordered work.
Evidence: `packages/cli/src/openspec/ralph/loader.ts:26-56`, `packages/cli/src/openspec/ralph/task-utils.ts:7-105`.

2. FAIL: `RalphTaskHandler` owns single-task execution plus persistence side effects.
Evidence: execution/retry/classification are in `packages/cli/src/openspec/ralph/handler.ts:47-252`, but persistence remains in `packages/cli/src/openspec/ralph-executor.ts:713-716`, `763-765`, `869-870`.

3. PASS: Compatibility exports still exist.
Evidence: `packages/cli/src/openspec/ralph-executor.ts:24-29`, `46-56`, `191-199`, `322-390`, `421-429`, `919-927`; re-exports in `packages/cli/src/openspec/ralph/index.ts:1-5`.

4. FAIL: Split runtime exposes Build work through shared task-runtime loader/handler registration.
Evidence: `packages/cli/src/workflow/task-runtime/types.ts:3-85` adds the type, but no registry registration/use exists in the repo; `packages/cli/src/openspec/ralph/loader.ts:13-57` returns `RalphLoadedTask[]`, not `ExecutableTask[]`.

5. PASS: Legacy compatibility loop still preserves ordered sequential behavior, `onlyTaskId`, `skipTaskIds`, validation failure, and deadlock handling.
Evidence: `packages/cli/src/openspec/ralph-executor.ts:421-917`; direct tests in `packages/cli/tests/openspec/ralph-loop-compatibility.test.ts:37-202`.

6. PASS: `BuildStageRunner` stays on the existing public Ralph execution path.
Evidence: `packages/cli/src/workflow/build-stage-runner.ts:162-199` continues calling `new RalphExecutor(...).execute(change, ...)`.

7. PASS: `tasks.json` schema is unchanged.
Evidence: writes still emit `{ version: 1, tasks }` in `packages/cli/src/openspec/ralph-executor.ts:221-224`; loader/tests assume the same schema.

8. PASS with warning: Focused Ralph tests and package build pass, but the first attempted test command used an unsupported Vitest flag.
Evidence: `npx vitest run packages/cli/tests/openspec/ralph-loader.test.ts packages/cli/tests/openspec/ralph-handler.test.ts packages/cli/tests/openspec/ralph-loop-compatibility.test.ts` passed with 49 tests; `npm run build` passed. The earlier `npm test -- --runInBand ...` invocation failed because Vitest does not support `--runInBand`.

## Verdict

Overall: FAIL. The legacy wrapper behavior is largely preserved, but the refactor does not yet complete the intended split runtime contract because handler-owned persistence and shared registry integration are both still missing.

<promise>FAIL</promise>
