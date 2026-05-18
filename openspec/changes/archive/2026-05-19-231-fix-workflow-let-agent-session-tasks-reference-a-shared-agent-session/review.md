# Review

## Findings

No error-level or warning-level findings in the current implementation.

## Spec Compliance

- PASS: `Agent-session task input supports an optional agentSessionRef`
Evidence: `packages/cli/src/workflow/domain/index.ts:59-64`, `packages/cli/src/workflow/task-runtime/types.ts:30-38`

- PASS: `Omitting agentSessionRef preserves existing task-local session behavior`
Evidence: `packages/cli/src/workflow/task-runtime/agent-session-task-handler.ts:61-73,145-149`; tests `packages/cli/tests/workflow/task-runtime/shared-session-regression.test.ts:155-191`

- PASS: `Plan artifact tasks use the same named agent session reference by default`
Evidence: `packages/cli/src/workflow/domain/index.ts:687-692`

- PASS: `A fresh Plan run creates one shared Plan coder session except restored tasks do not force a new session`
Evidence: restored Plan tasks remain `service-call` results in `packages/cli/src/workflow/task-runtime/task-dispatch-factory-registry.ts:179-201`; shared-session handler reuses the registry-backed session in `packages/cli/src/workflow/task-runtime/agent-session-task-handler.ts:67-73`; tests `packages/cli/tests/workflow/task-runtime/shared-session-regression.test.ts:79-123,239-325`

- PASS: `Plan task list still records independent task results`
Evidence: per-task result fields remain task-owned in `packages/cli/src/workflow/task-runtime/agent-session-task-handler.ts:94-113`; tests `packages/cli/tests/workflow/task-runtime/shared-session-regression.test.ts:125-152`

- PASS: `The Plan transcript contains separate Mohist prompt blocks for individual artifact tasks`
Evidence: shared tasks execute against the same real session via `session.execute(prompt, { kind: 'task', title })` in `packages/cli/src/workflow/task-runtime/agent-session-task-handler.ts:73`, which is the transcript-block mechanism; shared-session runtime tests verify one reused session ID across multiple Plan tasks in `packages/cli/tests/workflow/task-runtime/shared-session-regression.test.ts:80-123`

- PASS: `A stage can define two or more named agent session references`
Evidence: generic policy field in `packages/cli/src/workflow/domain/index.ts:59-64`; registry keyed by arbitrary `ref` in `packages/cli/src/workflow/stage-context.ts:16-39`; tests `packages/cli/tests/workflow/task-runtime/shared-session-regression.test.ts:194-236`

- PASS: `Rerun/retry/rewind stage creates a new session instance for the same ref rather than appending to an old transcript`
Evidence: `attemptSequence` exists on stage snapshots in `packages/cli/src/workflow/domain/index.ts:268-273,365-370`; stage-attempt registry key uses `workflowRunId`, `stage`, and `attemptSequence` in `packages/cli/src/workflow/workflow-engine.ts:210-220`; persistence carries `attempt_sequence` in `packages/cli/src/db/workflow-run-repo.ts:444-449,503-545`; tests `packages/cli/tests/workflow/task-runtime/shared-session-regression.test.ts:328-489`, `packages/cli/tests/workflow/shared-session-workflow-regression.test.ts:276-349`

- PASS: `Skip/restore of an intermediate task does not change the session reference used by later tasks`
Evidence: restored Plan tasks remain service calls in `packages/cli/src/workflow/task-runtime/task-dispatch-factory-registry.ts:179-201`; tests `packages/cli/tests/workflow/task-runtime/shared-session-regression.test.ts:239-325`

- PASS: `Check and Build task session behavior does not change unless explicitly configured`
Evidence: only `policy?.agentSessionRef` is propagated for `agent-session` execution in `packages/cli/src/workflow/config-driven-stage-runner.ts:647-659`; default Plan policies set the ref while default Build/Check policies do not in `packages/cli/src/workflow/domain/index.ts:687-694,761-768`

- PASS: `Stage lifecycle closes named sessions at passed/failed/awaiting-approval/cancelled/pipeline-complete boundaries`
Evidence: targeted close helpers exist in `packages/cli/src/workflow/workflow-engine.ts:237-250`; aggregate workflow closes the current stage registry on abort `packages/cli/src/workflow/workflow-engine.ts:324-327`, failed `335-338`, awaiting approval `340-343`, blocked `345-348`, latest-run failure `367-375`, stage transition or attempt rollover `386-394`, and all registries on pipeline completion `329-333,362-365,424-426`; approval-boundary behavior is covered by `packages/cli/tests/workflow/shared-session-workflow-regression.test.ts:204-231`

- PASS: `New code has tests, all tests pass`
Evidence: targeted regressions passed with `cd packages/cli && npm test -- tests/workflow/task-runtime/shared-session-regression.test.ts tests/workflow/shared-session-workflow-regression.test.ts`; build passed with `cd packages/cli && npm run build`

## Verification

- `cd packages/cli && npm run build` : PASS
- `cd packages/cli && npm test -- tests/workflow/task-runtime/shared-session-regression.test.ts tests/workflow/shared-session-workflow-regression.test.ts` : PASS

## Verdict

Overall: PASS

<promise>PASS</promise>
