## Review

No error-level findings.

### Correctness

- PASS: `WorkflowRun.nextWork()` and explicit approval completion paths both route through the shared evidence guard, blocking missing static task/check evidence, missing Build source evidence, stale Check evidence, pending runtime work, and missing Integrate delivery evidence instead of inferring completion from empty work queues. Evidence: `packages/cli/src/workflow/domain/index.ts:1269-1296`, `1324-1456`, `1057-1076`.
- PASS: Build dynamic work remains run-owned state. `StageDefinition` keeps `Build.tasks` empty while generated tasks are materialized into `StageRun.tasks` and tracked via `buildWorkSourceState`. Evidence: `packages/cli/src/workflow/domain/index.ts:709-725`, `395-410`, `887-905`.
- PASS: Projection is defensive and does not use stale sessions or merge state as authoritative completion truth. Evidence: `packages/cli/src/services/workflow-run-projection.ts:53-137`.

### Complexity

- PASS: New logic is concentrated in focused guard helpers rather than duplicated across runners. The main guard helpers remain readable and below the requested threshold in practice: `evaluateBuildWorkSourceFailureGuard`, `evaluateIntegrateDeliveryEvidenceGuard`, `evaluateCheckReviewEvidenceGuard` in `packages/cli/src/workflow/domain/index.ts:1340-1452`.

### Test Coverage

- PASS: Regression coverage exists for empty static work, missing/invalid/empty Build sources, materialized Build tasks, runtime rebase work, stale Check evidence, stale failed session with later successful workflow evidence, Integrate delivery evidence, and impossible passed projections. Evidence: `packages/cli/tests/workflow-run-domain.test.ts:156-205`, `710-812`, `992-1059`, `1419-1499`; `packages/cli/tests/build-workflowrun-tasks.test.ts:201-320`; `packages/cli/tests/workflowrun-e2e.test.ts:153-255`; `packages/cli/tests/workflow-engine-aggregate.test.ts:394-496`.
- PASS: Targeted suites pass locally: `npm test -- --run tests/workflow-run-domain.test.ts tests/workflow-engine-aggregate.test.ts tests/workflowrun-e2e.test.ts tests/build-workflowrun-tasks.test.ts tests/integrate-workflowrun.test.ts`.

### Security

- PASS: Review found no new injection, secret exposure, or unsafe input-handling issues in the completion-evidence changes.

### Spec Compliance

- PASS: `nextWork()` / stage completion logic does not treat empty required task/check sets as success. Evidence: guard rejects missing static task/check evidence in `packages/cli/src/workflow/domain/index.ts:1355-1365`; tests in `packages/cli/tests/workflow-run-domain.test.ts:156-186`.
- PASS: Static stage tasks/checks declared by `StageDefinition` must exist in `StageRun` before pass. Evidence: `packages/cli/src/workflow/domain/index.ts:1355-1365`; tests `packages/cli/tests/workflow-run-domain.test.ts:156-186`.
- PASS: Build with missing, invalid, or zero-task `tasks.json` does not advance as completed and returns clear reasons. Evidence: `packages/cli/src/workflow/domain/index.ts:1340-1349`, `1367-1373`, `887-905`; tests `packages/cli/tests/workflow-run-domain.test.ts:188-204`, `1419-1469`, `packages/cli/tests/build-workflowrun-tasks.test.ts:201-320`.
- PASS: Dynamic Build tasks are materialized into `Build StageRun.tasks`, not copied into `StageDefinition.tasks`. Evidence: `packages/cli/src/workflow/domain/index.ts:709-725`, `395-410`, `887-905`; tests `packages/cli/tests/build-workflowrun-tasks.test.ts:98-199`.
- PASS: Runtime-added tasks such as `rebase-branch` are not static `StageDefinition.tasks`, but once appended they must complete before the stage can pass. Evidence: `packages/cli/src/workflow/domain/index.ts:551-557`, `1385-1387`, `737-764`; tests `packages/cli/tests/workflow-run-domain.test.ts:1471-1499`.
- PASS: Check cannot pass without current authoritative review and merge-readiness evidence. Evidence: `packages/cli/src/workflow/domain/index.ts:1424-1452`; tests `packages/cli/tests/workflow-run-domain.test.ts:787-911`.
- PASS: Integrate cannot complete without required Integrate delivery evidence. Evidence: `packages/cli/src/workflow/domain/index.ts:1396-1422`; tests `packages/cli/tests/workflow-run-domain.test.ts:992-1059`, `packages/cli/tests/integrate-workflowrun.test.ts:184-257`.
- PASS: Explicit completion path uses the same guard as `nextWork()`. Evidence: `packages/cli/src/workflow/domain/index.ts:1294-1296`, `1324-1337`, `1454-1456`, `1057-1076`; tests `packages/cli/tests/workflow-run-domain.test.ts:761-812`.
- PASS: `WorkflowRunProjection` defensively refuses impossible passed snapshots, including workflows that did not finish Integrate. Evidence: `packages/cli/src/services/workflow-run-projection.ts:97-137`; tests `packages/cli/tests/workflowrun-e2e.test.ts:184-255`.
- PASS: A stale failed session does not prevent `Done` when later workflow evidence succeeds. Evidence: projection ignores session state in `packages/cli/src/services/workflow-run-projection.ts:53-137`; test `packages/cli/tests/workflowrun-e2e.test.ts:153-182`.
- PASS: Regression tests cover the requested scenarios. Evidence listed above across `workflow-run-domain`, `build-workflowrun-tasks`, `workflow-engine-aggregate`, `integrate-workflowrun`, and `workflowrun-e2e` suites.

### Fix Suggestions

- None.

<promise>PASS</promise>
