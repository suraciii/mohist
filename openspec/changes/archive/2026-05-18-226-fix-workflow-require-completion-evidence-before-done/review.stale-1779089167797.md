## Findings

1. High: `WorkflowEngine` does not handle aggregate `blocked` work states, so the new evidence guard is not surfaced to users as required.
File: `packages/cli/src/workflow/workflow-engine.ts:263-279, 303-320`
Evidence: `WorkflowRun.nextWork()` can now return `{ kind: 'blocked', ... }` from the shared completion guard (`packages/cli/src/workflow/domain/index.ts:1252-1254`), but the engine loop only exits for `complete`, `failed`, and `await-approval`. Any other kind falls through to `const runner = this.getRunner(work.stage)` and executes the stage runner again. There is no `blocked` branch, no blocked reason propagation, and no projection/update path for recoverable blocked evidence. This violates the workflow-engine requirement that runners and callers surface the blocked reason instead of continuing, and it misses the user-facing acceptance criterion that missing or unmaterialized work should show a clear blocked/recoverable reason.
Suggested fix: Add explicit `work.kind === 'blocked'` handling in `runAggregateWorkflow()` that returns a blocked pipeline result using `work.reason`, and ensure the issue/projection layer preserves that blocked diagnostic instead of attempting another runner pass.

## Spec Compliance

1. PASS: `nextWork()` / stage completion logic does not treat an empty required task/check set as successful completion.
Evidence: `packages/cli/src/workflow/domain/index.ts:1298-1333`; tests in `packages/cli/tests/workflow-run-domain.test.ts:156-204`.

2. PASS: Static stage tasks/checks declared by `StageDefinition` must exist in `StageRun` before the stage can pass.
Evidence: `packages/cli/src/workflow/domain/index.ts:1299-1309`; tests in `packages/cli/tests/workflow-run-domain.test.ts:156-185`.

3. PASS: Build with missing, invalid, or zero-task `tasks.json` does not advance as completed without a clear workflow reason.
Evidence: `packages/cli/src/workflow/domain/index.ts:1311-1317`; materialization in `packages/cli/src/workflow/config-driven-stage-runner.ts:471-504`; tests in `packages/cli/tests/workflow-run-domain.test.ts:188-204`.

4. PASS: Dynamic Build tasks generated from `tasks.json` are materialized into the Build `StageRun` and become required for that run.
Evidence: `packages/cli/src/workflow/domain/index.ts:853-871`; persistence/hydration in `packages/cli/src/workflow/domain/persistence.ts:107-123, 168-199`; tests in `packages/cli/tests/build-workflowrun-tasks.test.ts:90-191` and `packages/cli/tests/workflow-run-repo.test.ts:168-192`.

5. PASS: Runtime-added tasks such as `rebase-branch` are not static `StageDefinition.tasks`, but once appended to a `StageRun` they must complete successfully before the stage can pass.
Evidence: runtime policy definitions in `packages/cli/src/workflow/domain/index.ts:656, 689, 729, 789`; guard on run-owned tasks in `packages/cli/src/workflow/domain/index.ts:1329-1331`; tests in `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts:1466-1679`.

6. PASS: Check cannot pass without a current authoritative review task/result and required review/merge checks.
Evidence: `packages/cli/src/workflow/domain/index.ts:1319-1322, 1364-1391`; tests in `packages/cli/tests/workflow-run-domain.test.ts:684-833`.

7. PASS: Integrate cannot complete the workflow without required Integrate tasks and delivery evidence.
Evidence: `packages/cli/src/workflow/domain/index.ts:1324-1362`; tests in `packages/cli/tests/workflow-run-domain.test.ts:868-942` and `packages/cli/tests/integrate-workflowrun.test.ts:184-257`.

8. PASS: `WorkflowRun.completeStage()` or equivalent final completion path enforces the same completion guard as `nextWork()`.
Evidence: `packages/cli/src/workflow/domain/index.ts:1282-1295, 1394-1410`; approval path also re-checks guard in `packages/cli/src/workflow/domain/index.ts:1023-1042`; tests in `packages/cli/tests/workflow-run-domain.test.ts:715-734`.

9. PASS: `WorkflowRunProjection` defensively refuses impossible passed snapshots, including passed workflows that did not reach the final stage.
Evidence: `packages/cli/src/services/workflow-run-projection.ts:97-137`; tests in `packages/cli/tests/workflowrun-e2e.test.ts:150-186`.

10. PASS: A stale failed session on an otherwise successful later workflow run does not prevent `Done`.
Evidence: session status is not consulted by `WorkflowRunProjection.projectIssue()` or `validateCompletionProjection()` (`packages/cli/src/services/workflow-run-projection.ts:53-137`); successful end-to-end completion reaches `Done` via workflow evidence in `packages/cli/tests/workflowrun-e2e.test.ts:140-147`.
Note: This is covered indirectly; I did not find a dedicated regression asserting an older failed session row coexists with a later successful run.

11. FAIL: When Mohist cannot determine or materialize required work, users see a clear blocked/recoverable reason instead of a runner retry or generic engine failure.
Evidence: domain emits `blocked` (`packages/cli/src/workflow/domain/index.ts:1252-1254`), but the engine has no `blocked` branch and instead reruns the stage (`packages/cli/src/workflow/workflow-engine.ts:263-279, 303-320`).

## Tests

- PASS: `npx vitest run workflow-run-domain.test.ts build-workflowrun-tasks.test.ts integrate-workflowrun.test.ts workflow-run-repo.test.ts workflowrun-e2e.test.ts workflowrun-no-bypass.test.ts`
- Result: 93 tests passed.

## Overall

- Overall result: FAIL
- Reason: the core domain/projection hardening is mostly correct, but the aggregate workflow engine still does not surface `blocked` completion evidence states, leaving a spec-visible gap in how recoverable evidence failures behave at runtime.

<promise>FAIL</promise>
