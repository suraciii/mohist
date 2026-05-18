## Review

### Findings

1. High: stage retry/rerun can reuse the old shared agent session instead of creating a fresh session transcript.
   - Evidence: `packages/cli/src/workflow/workflow-engine.ts:110` builds the registry key from `this.stageAttemptKey(workflowRun.id, stage)`, and `packages/cli/src/workflow/workflow-engine.ts:204-205` defines that key as only ``${workflowRunId}:${stage}``.
   - Spec impact: this violates `workflow-agent/spec.md` scenario "Stage lifecycle closes named sessions" and `workflow-run/spec.md` scenario "New attempt does not append to old transcript", which require stage-attempt-scoped identity and a fresh real session on retry/rerun/rewind.
   - Why this is a bug: `WorkflowApplicationService.retryStage()` and `rerunStage()` mutate the existing aggregate/run in place rather than creating a new workflow run id (`packages/cli/src/services/workflow-application-service.ts:144-152`, `189-208`). If execution continues within the same `WorkflowEngine.run()` call, the existing registry entry for `workflowRun.id + stage` remains valid and later tasks can append to the previous shared session.
   - Test gap: the new retry coverage only swaps in a brand new `InMemoryAgentSessionRegistry` manually (`packages/cli/tests/workflow/task-runtime/shared-session-regression.test.ts:329-405`) or changes the workflow run id across separate engine instances (`packages/cli/tests/workflow/shared-session-workflow-regression.test.ts:204-245`). It does not exercise an in-process stage retry/rerun using the same workflow run id.
   - Suggested fix: include a true stage-attempt discriminator in the registry key, derived from persisted stage-run attempt identity or an incremented retry/rerun attempt counter, and add an integration test that retries Plan within one aggregate workflow run and asserts a different `acpSessionId` for the same `agentSessionRef`.

### Spec Compliance

1. PASS: `agentSessionRef` is supported on task execution policy and agent-session task input.
   - Evidence: `packages/cli/src/workflow/domain/index.ts:59-64`, `packages/cli/src/workflow/task-runtime/types.ts:30-40`.
2. PASS: omitting `agentSessionRef` preserves task-local behavior.
   - Evidence: `packages/cli/src/workflow/task-runtime/agent-session-task-handler.ts:61-72,145-148`.
3. PASS: default Plan artifact tasks use `plan-artifacts`.
   - Evidence: `packages/cli/src/workflow/domain/index.ts:684-689`.
4. PASS: dispatch propagates configured refs only for agent-session tasks.
   - Evidence: `packages/cli/src/workflow/config-driven-stage-runner.ts:647-659`, `packages/cli/src/workflow/task-runtime/task-dispatch-factory-registry.ts:164-168,204-214,236-247`.
5. PASS: restored Plan artifact tasks stay service-call completions and do not create sessions solely from policy.
   - Evidence: `packages/cli/src/workflow/task-runtime/task-dispatch-factory-registry.ts:179-201`.
6. PASS: named refs reuse one real session within the same attempt and task results remain separate.
   - Evidence: `packages/cli/src/workflow/stage-context.ts:21-39`, `packages/cli/src/workflow/task-runtime/agent-session-task-handler.ts:67-113`.
7. PASS: shared-session task results report the real `acpSessionId` used.
   - Evidence: `packages/cli/src/workflow/task-runtime/agent-session-task-handler.ts:101-112`.
8. PASS: Build and Check stay task-local by default unless explicitly configured.
   - Evidence: `packages/cli/src/workflow/domain/index.ts:758-765`; only Plan tasks carry `agentSessionRef` in defaults at `684-689`.
9. FAIL: retry/rerun/rewind do not have a proven stage-attempt-scoped registry key and the current key is insufficient.
   - Evidence: `packages/cli/src/workflow/workflow-engine.ts:110,204-205`.
   - Deviation: keying only by workflow run id and stage does not satisfy the spec requirement for fresh named sessions per later attempt.
10. FAIL: regression coverage does not prove in-process retry/rerun freshness for the same workflow run.
   - Evidence: `packages/cli/tests/workflow/task-runtime/shared-session-regression.test.ts:329-450`, `packages/cli/tests/workflow/shared-session-workflow-regression.test.ts:204-245`.
   - Deviation: tests prove fresh sessions only when a new registry or new run id is injected manually, not when the real workflow retries within one run.

### Complexity

- PASS with warning: the new code is still readable, but `WorkflowEngine` now owns cross-call registry lifecycle and key derivation, so the missing attempt discriminator is concentrated in one place (`packages/cli/src/workflow/workflow-engine.ts:63-64,110,204-220`).

### Test Coverage

- PASS with warning: targeted tests pass: `npm test -- --run tests/workflow/task-runtime/shared-session-regression.test.ts tests/workflow/shared-session-workflow-regression.test.ts`.
- FAIL for spec completeness: missing a real retry/rerun regression that exercises the production registry key path inside one workflow run.

### Security

- PASS: no obvious injection or secret-handling regressions were introduced by this change set.

### Overall

Overall result: FAIL.

Required fix:
- `packages/cli/src/workflow/workflow-engine.ts:204-205`: replace the registry key with a stage-attempt-scoped identity, not just `workflowRun.id` and `stage`.
- Add an aggregate workflow test that performs Plan retry/rerun with the same workflow run and asserts a new shared-session `acpSessionId` and no transcript reuse.

<promise>FAIL</promise>
