## Context

Issue completion currently mixes two different facts: pipeline checks completed and code merged to the target branch. The workflow can advance from Check to Done after `UserApprovalCheck` passes, while the merge queue is only triggered by the Check branch of `POST /api/issues/:number/approve`. Because `UserApprovalCheck`, Plan resume logic, pending approval queries, and agent recovery mostly inspect `approvalState.status` without validating `approvalState.stage`, a stale Plan approval can satisfy Check and skip the API path that enqueues the merge.

The existing code already has most primitives needed for the fix: `ApprovalState.stage`, `MergeState`, `MergeQueue.enqueue()`, `onMergeSuccess`, `merge_completed` events, issue archive APIs, CLI issue formatting, and Web UI merge components. The design should tighten invariants and centralize state interpretation rather than introduce a new database stage or a second merge system.

## Goals / Non-Goals

**Goals:**

- Ensure only current-stage approval can drive `user-approval`, resume/reject APIs, pending approval lookup, and paused-agent detection.
- Ensure `stage=done` and `status=completed` mean the issue has reached `MergeState.Merged`.
- Keep Check completion visible as a merge gate: after checks and user approval, the issue is queued/merging/merged or blocked by a merge failure.
- Make `mergeState = null` explicit in Web and CLI, including the `done/completed + null` anomaly.
- Prevent archive flows from silently treating false-done issues as safely completed.
- Preserve the existing `plan -> build -> check -> done` persisted stage model unless a later change explicitly adds a database merge stage.

**Non-Goals:**

- Add GitHub PR integration or external PR objects.
- Add new merge metadata columns for target branch, source branch, merged SHA, or merged timestamp in this change; display can use existing project/worktree/issue fields and `mergeState` until a storage change is specified.
- Rebuild the merge queue algorithm, conflict resolver, or worktree rebase strategy.
- Rewrite pipeline stage execution architecture beyond the approval and completion invariants required here.
- Automatically repair historical false-done issues by merging them without user action.

## Decisions

### D1: Centralize approval validity in a stage-aware predicate

Add a small shared helper, for example `isApprovalForStage(issue, stage = issue.stage, status?)`, colocated with workflow/domain types or a workflow state utility. It should return true only when `issue.approvalState?.stage === stage` and, when a status is supplied, `issue.approvalState.status === status`.

Use this predicate in `UserApprovalCheck`, Plan runner's approved skip, approve/reject handlers, `IssueRepo.findPendingApproval*`, `AgentRunnerService.isIssueAwaitingApproval()`, pause detection after pipeline run, and blocked resume unblocking. Stale approval should not be interpreted as current approval. When a stage advances, clear consumed approval state so stale state does not remain available to later stages.

**Alternatives considered:** Leave approval checks local and add `&& stage === issue.stage` at each call site. This is smaller in the first file but preserves the design problem: every new approval use has to remember a hidden invariant. A shared predicate pulls the complexity down and makes tests target the invariant directly.

### D2: Treat stale approval as inert data, not as a driver

The first-line defense is ignoring stale approvals everywhere. Stage advancement should also clear approval state after a successful stage transition or after the corresponding API consumes approval, but the system must remain safe even if stale JSON is still present in persisted rows.

For `awaiting` approval with a mismatched stage, pending approval APIs should return no pending approval. For `approved` or `rejected` with a mismatched stage, checks should return pending for the current stage rather than pass or fail. Optionally log a warning when a stale approval is encountered in workflow/API paths to aid diagnosis.

**Alternatives considered:** Fail the pipeline when stale approval exists. That would expose corruption aggressively but would also turn harmless leftover state into a blocker for active issues. Ignoring stale state while logging and clearing it on normal transitions gives safer runtime behavior.

### D3: Check approval enqueues merge; workflow no longer completes directly from Check

`CheckStageRunner` should not produce `nextStage = Stage.Done` as the direct result of passing checks. Instead, Check reaching `user-approval` without current approval pauses as today. When the user approves the current Check approval through the API, the API marks current-stage approval approved and calls `mergeQueue.enqueue(projectId, number)`. The issue remains in `Stage.Check` with `mergeState` moving through `pending`, `rebasing`, `merging`, `resolving`, `conflict`, `build-failed`, `blocked`, and `merged`.

Only merge success transitions the issue to Done and Completed. The authoritative place should be one merge success transition path called from the merge queue success callback or `merge_completed` event handler, with duplicate handlers made idempotent. That path should set `mergeState = merged` before or with `stage=done`, clear approval state, clear blocked reason, delete checkpoints, and emit completion events.

**Alternatives considered:** Add `Stage.MergeGate` to the database model. This would model the domain explicitly, but it requires stage enum migrations, kanban changes, old issue migration, and broader spec churn. The current requirement says Merge Gate need not be a database stage, so using `Stage.Check + mergeState` is the smaller compatible change.

### D4: Add a shared merge delivery classifier for UI and CLI semantics

Create a pure classifier that maps `{ stage, status, mergeState }` to a display status such as `not-ready`, `ready-for-approval`, `not-merged`, `queued`, `rebasing`, `merging`, `resolving`, `conflict`, `build-failed`, `blocked`, `merged`, `unknown`, and `done-not-merged`. The server can expose raw fields as it does today; frontend and CLI can each use equivalent formatting utilities, or a shared serializable API field can be added later if code sharing is impractical.

Important null handling:

- `mergeState = null` before Check completion means not ready or not merged yet, depending on stage/status.
- `stage=check`, checks passed, current approval awaiting means ready for approval/merge intent.
- `stage=done` or `status=completed` with `mergeState !== merged` means `done-not-merged` anomaly.
- `mergeState=merged` means merged even if UI is refreshed before the stage transition finishes.

**Alternatives considered:** Let each UI component and CLI command infer status independently. This is how the current hidden-null problem arose. A classifier makes the ambiguous states explicit and keeps future status labels consistent.

### D5: Keep raw storage, add invariant helpers rather than schema changes

Do not change the SQLite schema for this issue. `approval_state` already stores `stage`, and `merge_state` already stores merge lifecycle. Add helper methods or predicates in `IssueRepo`/domain utilities for `findCurrentPendingApproval`, `isFalseDoneIssue`, and possibly `findFalseDoneIssues` if recovery/status paths need batch detection.

Historical rows with `done/completed + merge_state IS NULL` should remain readable. They should be classified as false-done anomalies and blocked or warned during archive, not silently migrated to `merged`.

**Alternatives considered:** Backfill all existing `done/completed + null` rows to `merged`. That would hide the exact class of bug this issue is meant to surface and may falsely claim delivery for unmerged branches.

### D6: Archive guardrail warns or blocks based on delivery trust

Single-issue archive may continue to allow non-terminal issues with an explicit warning, but false-done issues must get a stronger warning and should not be included in `archiveAllCompleted`. Batch archive should only archive issues that are both completed and trusted merged, i.e. `stage=done`, `status=completed`, and `mergeState=merged`.

If single archive keeps allowing forced archive of false-done issues, the response must include a visible warning. If the implementation chooses to block by default, it should return a clear error telling the user the issue is marked completed but lacks a merge record.

**Alternatives considered:** Permit archive as currently implemented for any `Stage.Done` issue. That preserves convenience but makes the false-done state disappear from active views, which conflicts with this change's trust goal.

### D7: Approval copy reflects the next action, not the desired end state

Review/Check approval UI should render `Approve & Queue Merge` or `Approve & Merge to <target branch>` rather than `Approve & Done`. Plan approval copy should say `Approve design and start build` or equivalent. CLI `mo issue approve` should print the API returned `data.message` so the same command can accurately report either resume-pipeline or queued-for-merge.

**Alternatives considered:** Keep generic `Approve` copy everywhere. That is less misleading than `Approve & Done`, but it misses the opportunity to explain the merge gate and still leaves users guessing what happens next.

## Risks / Trade-offs

- [Risk] Duplicate merge success handlers can race and emit duplicate completion events. → Make the transition idempotent: if issue is already `done/completed` with `mergeState=merged`, no-op except logging.
- [Risk] Clearing approval state too early can erase audit context needed by UI. → Clear only after the approval has been consumed for a transition or merge enqueue; rely on stage executions/check results for historical audit.
- [Risk] Existing tests may assume Check pass advances directly to Done. → Update tests to assert Check approval queues merge and merge success completes the issue.
- [Risk] Historical false-done issues may become more visible and appear as errors to users. → Provide explicit messaging and recovery paths such as retry merge, rerun Check, or manual inspection rather than silently changing state.
- [Risk] Frontend and CLI classifiers can drift if implemented separately. → Keep classifier tables small, name states identically, and cover both with formatting tests for the same scenario matrix.
- [Risk] `mergeState=merged` without a completed stage can occur during async transition. → Classify it as merged/finishing and let the idempotent merge success transition settle the issue to Done.

## Migration Plan

1. Add domain helpers for current-stage approval and false-done/merge delivery classification.
2. Replace direct `approvalState.status` checks in workflow checks, Plan runner skip logic, pending approval repo methods, agent pause detection, approve/reject APIs, and blocked resume logic.
3. Clear consumed approval state on stage transition and after merge success; keep stale persisted approvals inert if encountered.
4. Change Check completion flow so Check approval enqueues merge and Done/completed is set only by the merge success transition.
5. Make merge success transition idempotent and remove or neutralize duplicate non-authoritative Done transitions.
6. Update archive logic so batch archive excludes false-done issues and single archive warns or blocks them visibly.
7. Update Web UI merge panel/card/detail/approval labels to use explicit merge delivery states and render `mergeState = null` instead of returning null.
8. Update CLI `show`, list/status formatting, and `approve` output to surface merge state and API messages.
9. Add regression tests for stale Plan approval at Check, Done blocked before merge success, false-done archive guardrails, CLI approve message, and UI/formatting null merge-state semantics.

Rollback is code-only because no schema migration is required. If deployment reveals issues, revert the workflow/API/UI changes together; do not backfill or mutate historical `merge_state` values during rollback.

## Open Questions

- Should single-issue archive block false-done issues by default, or allow archive with a prominent warning and no cleanup unless forced?
- Should the merge delivery classifier be returned by the API as a computed field, or kept as duplicated pure formatting logic in CLI and Web UI for now?
