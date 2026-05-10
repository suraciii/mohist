# Review Report

## Result: FAIL

## Dimensions

### Correctness: FAIL

1. Legacy projection still drops current task/check evidence from earlier retries.
Evidence: `packages/cli/src/services/stage-state-service.ts:618-642` only reads `stageExecutions.at(-1)` and projects task/check data from that single execution row. The design requires scanning legacy executions chronologically and upserting by identity so later attempts win without discarding partial evidence from earlier attempts. With the current implementation, a retried issue whose latest execution only contains checks, while an earlier execution contains tasks, can still return contradictory or empty current task state.
Fix suggestion: Update `packages/cli/src/services/stage-state-service.ts:618-642` so `seedProjectedStageState()` iterates all `stageExecutions` in chronological order and calls `upsertTask()` and `upsertCheck()` for each parsed result.

2. Runtime approval state is not mirrored into stage-state current data.
Evidence: `packages/cli/src/workflow/base-stage-runner.ts:393-398` writes approval state through `issueRepo.setApprovalState(...)`, but there is no corresponding call to `StageStateService.setApproval(...)`. As a result, the normal live write path can produce `/api/issues/:number/stage-state` responses with `status: 'awaiting-approval'` but `approval: null`, even though `packages/cli/src/services/stage-state-service.ts:441-458` supports persisting approval metadata.
Fix suggestion: After `issueRepo.setApprovalState(...)` in `packages/cli/src/workflow/base-stage-runner.ts:393-398`, call `ctx.stageStateService?.setApproval(ctx.issue.id, ctx.issue.stage, { status: 'awaiting', output: approvalOutput, requestedAt: ... })`. Also mirror approval responses on the approve/reject path so `respondedAt` is persisted.

### Complexity: FAIL

1. `packages/cli/src/services/stage-state-service.ts:570-658` and `packages/cli/src/services/stage-state-service.ts:660-693` exceed the requested function-size target and centralize most projection branching in two long methods.
Evidence: `seedProjectedStageState()` spans roughly 89 lines and `buildProjectedStageSeed()` spans roughly 34 lines while also encoding multiple data-source precedence rules.
Fix suggestion: Split projection into narrower helpers such as stage row seeding, legacy task projection, legacy check projection, and status derivation, keeping each function below the requested size target.

### Test Coverage: PASS

1. Focused backend and frontend coverage exists for stage-state behavior.
Evidence: `packages/cli/tests/stage-state-service.test.ts`, `packages/cli/tests/stage-state-regression.test.ts`, `packages/cli/tests/api/stage-state-api.test.ts`, and `packages/cli/web/src/components/stage-state-consistency.test.tsx` cover retries, dynamic fix tasks, API shape, legacy projection, and shared frontend data usage.

2. Relevant tests passed.
Evidence: `npm run test:stage-state` succeeded in `packages/cli`, including backend stage-state tests and the frontend consistency test.

### Security: PASS

1. No new injection or secret-handling issues were identified in the reviewed changes.
Evidence: the new API route in `packages/cli/src/api/issues.ts:408-435` reads server-side issue state and does not interpolate untrusted input into SQL directly; persistence continues through repository/database helpers. The review did not find exposed credentials, secret material, or new shell execution paths in changed files.

### Spec Compliance: FAIL

1. Acceptance criterion: Issue Detail Page 上，TaskProgressPanel 和 PipelineView 展示一致的 task 状态. PASS
Evidence: `packages/cli/web/src/components/PipelineView.tsx:1108-1119` and `packages/cli/web/src/components/TaskProgressPanel.tsx:98-109` both use `useIssueStageState(...)` as the primary source. Consistency is tested in `packages/cli/web/src/components/stage-state-consistency.test.tsx:114-138`.

2. Acceptance criterion: Stage 有多次 retry 时，显示的是当前最新状态，不是第一次 execution 的快照. FAIL
Evidence pass: direct current-state retry behavior is covered by `packages/cli/tests/stage-state-service.test.ts:362-415` and `packages/cli/tests/stage-state-regression.test.ts:35-114`.
Evidence fail: legacy projection still only reads the final execution snapshot in `packages/cli/src/services/stage-state-service.ts:618-642`, so migrated issues with split task/check evidence across retries can still lose current state.

3. Acceptance criterion: 动态出现的 task（如 fix-check-health）正确显示，不依赖前端硬编码. PASS
Evidence: dynamic tasks are accepted by `packages/cli/src/services/stage-state-service.ts:368-399` without filtering against static templates, and rendering is covered by `packages/cli/web/src/components/stage-state-consistency.test.tsx:140-165`.

4. Acceptance criterion: Task 状态的表达统一（消除 passes/status/pass 三种 schema）. PASS
Evidence: normalization helpers are implemented in `packages/cli/src/services/stage-state-service.ts:251-281`, and API tests assert normalized task/check statuses in `packages/cli/tests/api/stage-state-api.test.ts:204-237` and `packages/cli/tests/api/stage-state-api.test.ts:300-325`.

5. Acceptance criterion: stage_executions 审计数据不丢失. PASS
Evidence: `/executions` remains a separate endpoint in `packages/cli/src/api/issues.ts:383-406`, and retry regression tests still assert multiple execution rows are preserved in `packages/cli/tests/stage-state-regression.test.ts:73-76` and `packages/cli/tests/stage-state-regression.test.ts:110-111`.

6. Acceptance criterion: 现有 plan/build/check/integrate 流程功能等价. FAIL
Evidence fail: approval metadata is not written into stage-state during the normal runtime path because `packages/cli/src/workflow/base-stage-runner.ts:393-398` does not call `StageStateService.setApproval(...)`, even though the stage-state API requires approval state for current progress. This is a behavior gap for plan/check approval flows.

7. Requirement `REQ-HTTP-001 Issue stage-state API exposes current progress`. FAIL
Evidence pass: endpoint exists at `packages/cli/src/api/issues.ts:408-435`, and tests verify normalized stage/task/check payload shape in `packages/cli/tests/api/stage-state-api.test.ts:158-237`.
Evidence fail: legacy projection scenario is not fully satisfied because `packages/cli/src/services/stage-state-service.ts:618-642` only imports the last execution row, and the live approval scenario is not fully satisfied because `packages/cli/src/workflow/base-stage-runner.ts:393-398` does not persist approval metadata into stage-state.

8. Requirement `REQ-HTTP-002 Execution history remains separate`. PASS
Evidence: `packages/cli/src/api/issues.ts:383-406` keeps `/executions` separate from `packages/cli/src/api/issues.ts:408-435`.

9. Requirement `REQ-PM-005 Stage tasks and checks have current state`. FAIL
Evidence pass: current task/check rows are read through `packages/cli/src/services/stage-state-service.ts:461-507`, and retry-in-place behavior is tested in `packages/cli/tests/stage-state-service.test.ts:362-415`.
Evidence fail: migrated legacy issues are still projected from only the last execution row in `packages/cli/src/services/stage-state-service.ts:618-642`, which means current state is not reliably queryable for all supported issue histories.

10. Requirement `REQ-PM-006 Backend owns stage task definitions`. PASS
Evidence: backend static task definitions live in `packages/cli/src/services/stage-state-service.ts:289-312` and are seeded by `packages/cli/src/services/stage-state-service.ts:353-366`; build-task mirroring is implemented in `packages/cli/src/workflow/base-stage-runner.ts:597-615` and `packages/cli/src/services/stage-state-service.ts:605-616`.

11. Requirement `REQ-WUI-001 Pipeline UI shows explicit fix tasks`. PASS
Evidence: dynamic fix tasks remain part of current stage state via `packages/cli/src/services/stage-state-service.ts:368-399`, and UI rendering is covered by `packages/cli/web/src/components/stage-state-consistency.test.tsx:140-165`.

12. Requirement `REQ-WUI-004 Issue Detail uses unified stage state`. PASS
Evidence: `packages/cli/web/src/components/PipelineView.tsx:1108-1119` and `packages/cli/web/src/components/TaskProgressPanel.tsx:98-109` both read `useIssueStageState(...)`; consistency is verified in `packages/cli/web/src/components/stage-state-consistency.test.tsx:198-212`.

## Changed Files Covered

1. `packages/cli/package.json`
Covered for test command verification: `test:stage-state` exists and runs the focused backend and frontend stage-state tests.

2. `packages/cli/src/services/stage-state-service.ts`
Covered for correctness, complexity, normalization, backend task definitions, legacy projection, and stage-state persistence behavior.

3. `packages/cli/tests/api/stage-state-api.test.ts`
Covered for API shape, normalized status assertions, and lazy legacy projection scenarios.

4. `packages/cli/tests/stage-state-service.test.ts`
Covered for retry-in-place behavior, dynamic tasks, approval storage API, and normalization helpers.

## Overall Verdict

Overall verdict is FAIL because Correctness fails and Spec Compliance fails. This is consistent with the dimension verdicts above.

<promise>FAIL</promise>
