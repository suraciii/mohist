## Findings

1. High: Config-driven Plan and Check execution is broken on the default registration path.

Evidence:
- `packages/cli/src/services/agent-runner-service.ts:1195-1198` registers the unified runner with the real `defaultAgentSessionTaskHandler` for `'agent-session'` tasks.
- `packages/cli/src/workflow/config-driven-stage-runner.ts:298-305` and `308-315` call that handler first for Plan tasks and `ai-review`, then return early on any non-null result, so the stage-specific fallback logic is skipped.
- `packages/cli/src/workflow/task-runtime/types.ts:20-29` defines `AgentSessionTaskInput` as requiring `prompt`, `cwd`, `stage`, and `attempt`.
- `packages/cli/src/workflow/config-driven-stage-runner.ts:301-302` and `311-312` pass only `{ taskId, title, kind: 'agent-session' }`.
- `packages/cli/src/workflow/task-runtime/agent-session-task-handler.ts:20-24`, `45-55`, and `64` immediately consume `prompt`, `cwd`, `stage`, and `attempt` to create the session and execute the prompt.

Impact:
- In production, config-driven Plan and Check tasks can execute with missing prompt/cwd/stage metadata, which breaks the migrated default runner path.
- Even if the handler tolerates the malformed input, the unified runner still bypasses the migrated Plan/Check semantics implemented in `executePlanAgentSessionTask()` and `executeCheckAiReviewTask()`, so artifact verification, retry prompts, checkpoint restore, and checkpoint writes are skipped.
- This violates the Plan and Check preservation requirements in `specs/workflow-definition/spec.md` and the registry-execution requirement in `specs/workflow-engine/spec.md`.

Suggested fix:
- In `packages/cli/src/workflow/config-driven-stage-runner.ts:298-315`, stop dispatching Plan and Check agent tasks through the generic bare `ExecutableTask` path.
- Either:
  1. always route Plan tasks to `executePlanAgentSessionTask()` and `ai-review` to `executeCheckAiReviewTask()`, or
  2. construct a full `AgentSessionTaskInput` with prompt/cwd/stage/attempt/artifact verification and keep the artifact/checkpoint/retry behavior in the handler path.
- Add a regression test that wires `ConfigDrivenStageRunner` with `defaultAgentSessionTaskHandler`-shaped input expectations, not a permissive mock.

## Correctness

- FAIL: The default config-driven Plan/Check path is malformed and bypasses required stage behavior.

## Complexity

- WARN: `packages/cli/src/workflow/config-driven-stage-runner.ts` remains large and contains substantial stage-specific branching, but I am not marking this alone as a release blocker.

## Test Coverage

- PASS with warning: Focused regression suites passed (`pnpm vitest run packages/cli/tests/workflow/stage-runner-migration-regression.test.ts packages/cli/tests/workflow-engine-aggregate.test.ts packages/cli/tests/workflow-run-domain.test.ts packages/cli/tests/workflow/rebase-workflow-regression.test.ts`), but the current tests miss the real default `agent-session` registration shape, which allowed the production-path regression above.

## Security

- PASS: No new secret exposure or obvious injection issue found in the reviewed change set.

## Spec Compliance

- PASS: `workflow-definition` declarative policy fields exist in `packages/cli/src/workflow/domain/index.ts:468-627`, and stage order remains `plan -> build -> check -> integrate` via `WorkflowRun.stageOrder` in `packages/cli/src/workflow/domain/index.ts:665-667`.
- FAIL: `workflow-definition` Plan preservation scenario is not met on the default path because Plan tasks can bypass artifact verification/checkpoint behavior (`packages/cli/src/workflow/config-driven-stage-runner.ts:298-305` vs. `380-480`).
- FAIL: `workflow-definition` Check preservation scenario is not met on the default path because `ai-review` can bypass review artifact retry/checkpoint behavior (`packages/cli/src/workflow/config-driven-stage-runner.ts:308-315` vs. `671-769`).
- PASS: `workflow-engine` aggregate single-work behavior is covered and exercised in `packages/cli/tests/workflow-engine-aggregate.test.ts:254-319`.
- PASS: `workflow-run` repair/invalidation/rebase behavior is implemented in `packages/cli/src/workflow/domain/index.ts:803-817` and `1136-1175`, with coverage in `packages/cli/tests/workflow-run-domain.test.ts:199-315` and `packages/cli/tests/workflow/rebase-workflow-regression.test.ts:174-260`.
- PASS: `ralph-task-execution` Build materialization and Ralph handler wiring are present in `packages/cli/src/workflow/config-driven-stage-runner.ts:816-908`, `packages/cli/src/workflow/task-runtime/ralph-task-loader.ts:7-25`, and `packages/cli/src/workflow/task-runtime/ralph-task-handler.ts:11-121`, with focused coverage in `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts:457-576`.

## Overall

- FAIL: The migrated unified runner does not safely preserve Plan/Check semantics on the actual default registration path.

<promise>FAIL</promise>
