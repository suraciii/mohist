## Findings

1. Missing regression test for the stale failed session scenario required by the spec.
File refs: `packages/cli/tests/workflowrun-e2e.test.ts:150`, `packages/cli/tests/workflowrun-e2e.test.ts:169`, `packages/cli/src/services/workflow-run-projection.ts:53`
Why it matters: the projection code intentionally ignores session status and derives `Done` from workflow evidence, which is correct, but the acceptance criteria explicitly require regression coverage for "A stale failed session on an otherwise successful later workflow run does not prevent `Done`". The current tests cover impossible passed snapshots and merge-state insufficiency, but there is no test that creates an older failed session alongside later successful Check/Integrate evidence and proves `Done` still projects.
Suggested fix: add an integration or projection test that persists a failed historical session for the issue, then applies a successful WorkflowRun with full Integrate evidence and asserts the issue projects to `stage=Done` and `status=Completed`.

## Acceptance Criteria

1. PASS - `nextWork()` / stage completion no longer infers success from empty work alone.
Evidence: `packages/cli/src/workflow/domain/index.ts:1276`, `packages/cli/src/workflow/domain/index.ts:1322`, `packages/cli/tests/workflow-run-domain.test.ts:156`

2. PASS - Static stage tasks/checks must exist in `StageRun` before the stage can pass.
Evidence: `packages/cli/src/workflow/domain/index.ts:1323`, `packages/cli/src/workflow/domain/index.ts:1329`, `packages/cli/tests/workflow-run-domain.test.ts:156`

3. PASS - Build with missing, invalid, or zero-task `tasks.json` does not advance silently.
Evidence: `packages/cli/src/workflow/domain/index.ts:1335`, `packages/cli/src/workflow/domain/index.ts:875`, `packages/cli/tests/workflow-run-domain.test.ts:188`

4. PASS - Dynamic Build tasks are materialized into `Build StageRun` and become required run evidence.
Evidence: `packages/cli/src/workflow/domain/index.ts:394`, `packages/cli/src/workflow/domain/index.ts:875`, `packages/cli/tests/build-workflowrun-tasks.test.ts:90`, `packages/cli/tests/workflow-run-repo.test.ts:168`

5. PASS - Runtime-added tasks such as `rebase-branch` are run-owned work and block completion until successful.
Evidence: `packages/cli/src/workflow/domain/index.ts:550`, `packages/cli/src/workflow/domain/index.ts:1353`, `packages/cli/tests/workflow/rebase-workflow-regression.test.ts:110`, `packages/cli/tests/build-workflowrun-tasks.test.ts:284`

6. PASS - Check cannot pass without current authoritative review and merge evidence.
Evidence: `packages/cli/src/workflow/domain/index.ts:1388`, `packages/cli/tests/workflow-run-domain.test.ts:741`, `packages/cli/tests/workflow-run-domain.test.ts:837`

7. PASS - Integrate cannot complete without required Integrate delivery evidence.
Evidence: `packages/cli/src/workflow/domain/index.ts:1360`, `packages/cli/tests/workflow-run-domain.test.ts:946`, `packages/cli/tests/workflowrun-e2e.test.ts:188`

8. PASS - Explicit completion/approval paths enforce the same guard instead of bypassing it.
Evidence: `packages/cli/src/workflow/domain/index.ts:1306`, `packages/cli/src/workflow/domain/index.ts:1045`, `packages/cli/tests/workflow-run-domain.test.ts:715`, `packages/cli/tests/workflowrun-no-bypass.test.ts:42`

9. PASS - `WorkflowRunProjection` rejects impossible passed snapshots before `Done`.
Evidence: `packages/cli/src/services/workflow-run-projection.ts:97`, `packages/cli/tests/workflowrun-e2e.test.ts:150`

10. FAIL - Regression coverage for stale failed session plus later successful workflow evidence is missing.
Evidence: `packages/cli/src/services/workflow-run-projection.ts:53` has the intended session-independent behavior, but the targeted regression suite only covers impossible passed snapshots and merge-state insufficiency at `packages/cli/tests/workflowrun-e2e.test.ts:150` and `packages/cli/tests/workflowrun-e2e.test.ts:169`. No test in the reviewed workflow/projection suites exercises an older failed session coexisting with later successful workflow evidence.

11. PASS - Impossible passed projection snapshots are rejected with a diagnostic blocked result.
Evidence: `packages/cli/src/services/workflow-run-projection.ts:62`, `packages/cli/tests/workflowrun-e2e.test.ts:162`

## Test Status

- PASS - Targeted suites passed: `npm test -- workflow-run-domain.test.ts workflowrun-e2e.test.ts integrate-workflowrun.test.ts build-workflowrun-tasks.test.ts workflow-run-persistence.test.ts workflowrun-no-bypass.test.ts`
- NOTE - I did not run the full backend test suite in this review.

<promise>FAIL</promise>
