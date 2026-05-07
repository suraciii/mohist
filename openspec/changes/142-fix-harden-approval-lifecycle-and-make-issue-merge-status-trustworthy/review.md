# Review Report

## Result: FAIL

The implementation covers most of the requested approval, merge-gating, CLI, Web, archive, and regression-test work, but two error-level approval lifecycle bugs remain in `AgentRunnerService`. Because Correctness and Spec Compliance fail, the overall verdict is FAIL.

## Dimensions

### Correctness: FAIL

Two runtime paths still mishandle approval state.

ERROR: Periodic orphan scan blocks valid awaiting approvals.

- Location: `packages/cli/src/services/agent-runner-service.ts:315-320`
- The orphan scan should find active issues without a running agent and without a pending approval, but the predicate selects current-stage awaiting approvals:

```ts
if (!isCurrentStageApproval(issue, issue.stage, 'awaiting')) return false;
return true;
```

- This makes valid paused approvals part of `orphans`, then blocks them at `packages/cli/src/services/agent-runner-service.ts:330-334` with `检测到 agent 已退出但状态未更新，自动恢复`.
- Runtime impact: a correctly paused Plan/Check approval can be auto-blocked by the 5-minute timer started at `packages/cli/src/services/agent-runner-service.ts:129` before the user approves it.
- Suggested fix at `packages/cli/src/services/agent-runner-service.ts:319`:

```ts
if (isCurrentStageApproval(issue, issue.stage, 'awaiting')) return false;
```

- Add a regression test that constructs an active Plan or Check issue with current-stage `approvalState.status='awaiting'`, invokes the orphan scan path or fake-timer interval, and asserts the issue remains active and unblocked.

ERROR: Pipeline pause detection still uses stage-unaware approval status.

- Location: `packages/cli/src/services/agent-runner-service.ts:1086`
- The pipeline completion path still checks only `approvalState?.status === 'awaiting'`:

```ts
const isPaused = !result.completed && (issueRepo.findById(issue.id)?.approvalState?.status === 'awaiting');
```

- A stale `Plan` awaiting approval on a `Check` issue can cause an unrelated non-completed pipeline result to be recorded as `awaiting_approval`, emit `agent_paused`, and mark the task completed instead of failing/blocking at `packages/cli/src/services/agent-runner-service.ts:1089-1101`.
- Suggested fix at `packages/cli/src/services/agent-runner-service.ts:1086`:

```ts
const latestIssue = issueRepo.findById(issue.id);
const isPaused = Boolean(
  latestIssue && !result.completed && isCurrentStageApproval(latestIssue, latestIssue.stage, 'awaiting'),
);
```

- Add a regression test where a `Check` issue has stale `Plan` awaiting approval and `WorkflowEngine.run()` returns `{ completed: false }`; assert the task is not completed as `awaiting_approval` and no paused event is emitted.

WARNING: Issue cards still display stale approval badges.

- Location: `packages/cli/web/src/components/IssueCard.tsx:41-42`
- `IssueCard` shows the `Approval` badge for any `approvalState.status === 'awaiting'` without checking `approvalState.stage === issue.stage`.
- Suggested fix at `packages/cli/web/src/components/IssueCard.tsx:41`:

```ts
if (issue.approvalState?.status === 'awaiting' && issue.approvalState.stage === issue.stage) {
```

### Complexity: PASS

- The new central lifecycle helper in `packages/cli/src/workflow/issue-lifecycle.ts:4-73` keeps approval and merge classification logic small and explicit.
- The implementation avoids new schema or stage-model complexity, matching the design non-goal in `openspec/changes/142-fix-harden-approval-lifecycle-and-make-issue-merge-status-trustworthy/design.md:16-23`.
- No reviewed function appeared to exceed the requested complexity thresholds as part of this change. `MergeStatePanel` is longer than ideal at `packages/cli/web/src/components/MergeStatePanel.tsx:12-231`, but it is mostly a flat rendering table and is not the source of a blocking issue.

### Test Coverage: PASS

- Stale Plan approval leaking into Check is covered by `packages/cli/tests/workflow/stage-aware-approval.test.ts:65-168` and `packages/cli/tests/regression-approval-lifecycle.test.ts:81-137`.
- Merge-gated completion is covered by `packages/cli/tests/regression-approval-lifecycle.test.ts:140-215` and workflow tests in `packages/cli/tests/workflow/workflow-engine.test.ts`.
- False-done classification and archive guardrails are covered by `packages/cli/tests/regression-approval-lifecycle.test.ts:217-354`, `packages/cli/tests/workflow/issue-lifecycle.test.ts:159-171`, and `packages/cli/tests/issue-archive.test.ts:482-567`.
- CLI merge formatting is covered by `packages/cli/tests/cli-merge-display.test.ts`.
- Web null merge-state semantics are covered by `packages/cli/web/src/lib/merge-state.test.ts:57-168`.
- Coverage gap: no regression test directly exercises the periodic orphan scan bug at `packages/cli/src/services/agent-runner-service.ts:315-320` or stale approval pause detection at `packages/cli/src/services/agent-runner-service.ts:1086`.

### Security: PASS

- No new secret handling, credential output, shell interpolation, or user-controlled command construction risk was found in the reviewed changes.
- The approval and archive API changes continue to use numeric issue numbers and repository/service methods rather than constructing SQL or shell commands directly.
- Existing `npm audit` output during `npm run build` reported 1 moderate and 1 high vulnerability from the Web dependency install, but this appears pre-existing and not directly introduced by this implementation.

### Spec Compliance: FAIL

- Acceptance: Plan stage approval does not make Check stage `UserApprovalCheck` auto-pass. PASS. `UserApprovalCheck` delegates to `isCurrentStageApproval()` at `packages/cli/src/workflow/checks/user-approval-check.ts:22-44`, and stale Plan-approved coverage exists at `packages/cli/tests/workflow/stage-aware-approval.test.ts:66-82`.
- Acceptance: Only current-stage `approvalState` can drive current stage. FAIL. Workflow checks and API mostly comply, but runtime pause detection at `packages/cli/src/services/agent-runner-service.ts:1086` is still stage-unaware.
- Acceptance: Stale awaiting/approved/rejected approval does not affect current stage. FAIL. Stale awaiting can still affect pipeline pause classification at `packages/cli/src/services/agent-runner-service.ts:1086`.
- Acceptance: Plan approval is consumed and cannot leak to Build/Check. PASS with caveat. Stale Plan approvals are inert in `UserApprovalCheck`, and Plan skip logic uses `isCurrentStageApproval(issue, Stage.Plan, 'approved')` at `packages/cli/src/workflow/plan-stage-runner.ts:47`; however, approval clearing is not comprehensive and safety relies on inert handling.
- Acceptance: Check stage completes review then enters awaiting approval. PASS. `CheckStageRunner` uses `new UserApprovalCheck(Stage.Check)` at `packages/cli/src/workflow/check-stage-runner.ts:43-47`, and `BaseStageRunner` writes current-stage awaiting approval at `packages/cli/src/workflow/base-stage-runner.ts:248-253`.
- Acceptance: Check approve enters merge queue. PASS. API Check approval sets approval approved and calls `mergeQueue.enqueue(projectId, number)` at `packages/cli/src/api/issues.ts:988-997`.
- Acceptance: Done/completed only appears after merge success. PASS with guardrail. `WorkflowEngine` blocks Check-to-Done at `packages/cli/src/workflow/workflow-engine.ts:136-138`, and server merge success handlers set Done/completed plus `MergeState.Merged` at `packages/cli/src/server/index.ts:217-231` and `packages/cli/src/server/index.ts:289-301`.
- Acceptance: `done/completed + merge_state != merged` displays as anomaly. PASS. `classifyMergeDelivery()` returns `done-not-merged` at `packages/cli/src/workflow/issue-lifecycle.ts:25-29`, CLI warns at `packages/cli/src/cli/commands/issue.ts:247-249`, and Web detail renders a red panel at `packages/cli/web/src/components/MergeStatePanel.tsx:22-37`.
- Acceptance: Web Issue detail shows merged/not merged/queued/conflict/unknown. PASS. `MergeStatePanel` renders null, queued, merging, merged, build-failed, conflict, rebasing, resolving, and blocked states at `packages/cli/web/src/components/MergeStatePanel.tsx:22-230`.
- Acceptance: Web Issue card shows merged or merge warning badge. PASS with warning. False-done Done cards get a red badge at `packages/cli/web/src/components/IssueCard.tsx:28-34`; stale approval badge handling remains misleading at `packages/cli/web/src/components/IssueCard.tsx:41-42`.
- Acceptance: CLI `mo issue show` displays merge status. PASS. `mo issue show` prints `Merge:` and branch context at `packages/cli/src/cli/commands/issue.ts:212-245`.
- Acceptance: `Approve & Done` copy is replaced with action-accurate copy. PASS. Review approval now says `Approve & Queue Merge` at `packages/cli/web/src/components/ReviewApprovalPanel.tsx:337`, and inline approval uses Plan/Check-specific labels at `packages/cli/web/src/components/PipelineView.tsx:428-454`.
- Acceptance: `mo issue approve` output distinguishes resumed pipeline and queued merge. PASS. `mo issue approve` prints `response.data?.message` at `packages/cli/src/cli/commands/issue.ts:503-514`; API messages are set at `packages/cli/src/api/issues.ts:1001-1003` and `packages/cli/src/api/issues.ts:1024-1029`.
- Acceptance: Archive does not silently archive false-done issues. PASS. Batch archive skips `done-not-merged` issues at `packages/cli/src/services/issue-service.ts:257-282`; single archive returns an explicit warning at `packages/cli/src/services/issue-service.ts:195-217`.
- Acceptance: New regression test covers stale Plan approval leaking into Check. PASS. Covered by `packages/cli/tests/workflow/stage-aware-approval.test.ts:65-168` and `packages/cli/tests/regression-approval-lifecycle.test.ts:81-137`.
- Acceptance: New regression test covers issue done but `merge_state` empty defense. PASS. Covered by `packages/cli/tests/regression-approval-lifecycle.test.ts:217-280` and archive tests at `packages/cli/tests/regression-approval-lifecycle.test.ts:282-354`.
- Acceptance: New UI/formatting coverage covers `merge_state=null` in different stage/status semantics. PASS. CLI classifier tests are in `packages/cli/tests/cli-merge-display.test.ts`, and Web classifier matrix is in `packages/cli/web/src/lib/merge-state.test.ts:57-168`.

## Changed Files Coverage

- `openspec/changes/142-fix-harden-approval-lifecycle-and-make-issue-merge-status-trustworthy/design.md`: Reviewed as context for implementation approach.
- `openspec/changes/142-fix-harden-approval-lifecycle-and-make-issue-merge-status-trustworthy/proposal.md`: Reviewed as context for scope and motivation.
- `openspec/changes/142-fix-harden-approval-lifecycle-and-make-issue-merge-status-trustworthy/self-review.md`: Covered as change artifact; no code behavior impact.
- `openspec/changes/142-fix-harden-approval-lifecycle-and-make-issue-merge-status-trustworthy/session-memories/T-003.json`: Covered as task metadata; no code behavior impact.
- `openspec/changes/142-fix-harden-approval-lifecycle-and-make-issue-merge-status-trustworthy/session-memories/T-004.json`: Covered as task metadata; no code behavior impact.
- `openspec/changes/142-fix-harden-approval-lifecycle-and-make-issue-merge-status-trustworthy/session-memories/T-007.json`: Covered as task metadata; no code behavior impact.
- `openspec/changes/142-fix-harden-approval-lifecycle-and-make-issue-merge-status-trustworthy/specs/cli-interface/spec.md`: Covered in Spec Compliance CLI criteria.
- `openspec/changes/142-fix-harden-approval-lifecycle-and-make-issue-merge-status-trustworthy/specs/http-api/spec.md`: Covered in Spec Compliance API criteria.
- `openspec/changes/142-fix-harden-approval-lifecycle-and-make-issue-merge-status-trustworthy/specs/local-issue-store/spec.md`: Covered in Spec Compliance archive/storage criteria.
- `openspec/changes/142-fix-harden-approval-lifecycle-and-make-issue-merge-status-trustworthy/specs/pipeline-model/spec.md`: Covered in Spec Compliance pipeline criteria.
- `openspec/changes/142-fix-harden-approval-lifecycle-and-make-issue-merge-status-trustworthy/specs/web-ui/spec.md`: Covered in Spec Compliance Web criteria.
- `openspec/changes/142-fix-harden-approval-lifecycle-and-make-issue-merge-status-trustworthy/tasks.json`: Covered as task tracking metadata; no code behavior impact.
- `packages/cli/src/api/issues.ts`: Reviewed approval, reject, archive-all, and response message paths.
- `packages/cli/src/cli/commands/issue.ts`: Reviewed `show`, list warning, and approve output formatting.
- `packages/cli/src/db/issue-repo.ts`: Reviewed current-stage pending approval query changes.
- `packages/cli/src/server/index.ts`: Reviewed merge success Done/completed transition handlers.
- `packages/cli/src/services/agent-runner-service.ts`: Reviewed recovery, orphan scan, queue recovery, pause detection, and awaiting approval helper paths; contains both error-level findings.
- `packages/cli/src/services/issue-service.ts`: Reviewed single and batch archive guardrails.
- `packages/cli/src/workflow/check-stage-runner.ts`: Reviewed Check-stage approval target and direct Done next-stage behavior.
- `packages/cli/src/workflow/checks/user-approval-check.ts`: Reviewed current-stage approval predicate usage.
- `packages/cli/src/workflow/issue-lifecycle.ts`: Reviewed approval predicate and merge delivery classifier.
- `packages/cli/src/workflow/plan-stage-runner.ts`: Reviewed Plan approved skip behavior.
- `packages/cli/src/workflow/stage-context.ts`: Covered through stage runner/repo interface usage.
- `packages/cli/src/workflow/workflow-engine.ts`: Reviewed Check-to-Done guard and final completion behavior.
- `packages/cli/tests/cli-merge-display.test.ts`: Reviewed CLI merge status coverage.
- `packages/cli/tests/database.test.ts`: Covered storage preservation behavior.
- `packages/cli/tests/issue-archive.test.ts`: Reviewed archive guardrail coverage.
- `packages/cli/tests/recover-issues.test.ts`: Reviewed recovery coverage and noted missing periodic orphan scan coverage.
- `packages/cli/tests/regression-approval-lifecycle.test.ts`: Reviewed stale approval, direct Done guard, false-done, archive, and classifier coverage.
- `packages/cli/tests/workflow/issue-lifecycle.test.ts`: Reviewed lifecycle helper coverage.
- `packages/cli/tests/workflow/pipeline-integration.test.ts`: Covered pipeline expectation updates.
- `packages/cli/tests/workflow/stage-aware-approval.test.ts`: Reviewed stage-aware approval coverage.
- `packages/cli/tests/workflow/workflow-engine.test.ts`: Covered workflow engine transition coverage.
- `packages/cli/tests/workflow/workflow-integration.test.ts`: Covered integration expectation updates.
- `packages/cli/web/src/components/IssueCard.tsx`: Reviewed merge warning badge and stale approval badge warning.
- `packages/cli/web/src/components/IssueDetailPage.tsx`: Reviewed `MergeStatePanel` props and detail rendering path.
- `packages/cli/web/src/components/MergeStatePanel.tsx`: Reviewed stable merge status panel rendering.
- `packages/cli/web/src/components/PipelineView.tsx`: Reviewed Plan/Check approval copy and inline approval condition.
- `packages/cli/web/src/components/ReviewApprovalPanel.tsx`: Reviewed removal of `Approve & Done` copy.
- `packages/cli/web/src/lib/merge-state.test.ts`: Reviewed Web merge-state classifier coverage.

## Verification

- `npm run build` in `packages/cli`: PASS. Build completed, including Web build.
- `npm test -- --run tests/regression-approval-lifecycle.test.ts tests/workflow/stage-aware-approval.test.ts tests/issue-archive.test.ts tests/cli-merge-display.test.ts web/src/lib/merge-state.test.ts` in `packages/cli`: PASS, 137 tests passed.
- Initial targeted test command with repo-root paths from `packages/cli` found no files; reran with package-relative paths successfully.

<promise>FAIL</promise>
