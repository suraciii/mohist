## Why

Mohist can currently report an issue as `done/completed` even when its work has not been merged into the target branch, because approval state is reused across stages and completion is not gated by merge success. This breaks the user's core trust question: whether the issue's code has actually landed, where it landed, and what is blocking it if not.

## What Changes

- Make approval lifecycle stage-aware: only approval state for the issue's current stage may satisfy `user-approval`, resume pipelines, or appear as pending approval.
- Clear or ignore stale approvals when stages advance so Plan approval cannot leak into Build or Check, and Check must request a fresh user approval.
- Treat Check approval as approval to merge, not approval to mark done: Check approval enqueues the issue for merge and only merge success may set `stage=done` and `status=completed`.
- Detect `done/completed` issues whose `merge_state` is not `merged` as false-done anomalies during runtime recovery, archive flows, CLI formatting, and Web UI display.
- Make merge visibility PR-like in Web UI and CLI: issue detail, issue cards, Done column, and `mo issue show` must display merged, queued, merging, conflict, not merged, unknown, or anomalous merge states, including when `merge_state` is `null`.
- Align approval copy with the real action: Plan approval resumes/builds, Check approval queues merge, and `Approve & Done` is removed before merge completion.
- Make CLI approve output use the API response message so users can distinguish resumed pipeline from queued-for-merge behavior.
- Add regression coverage for stale Plan approval leaking into Check, false-done protection, and `merge_state = null` display semantics.

## Capabilities

### New Capabilities

- None

### Modified Capabilities

- `pipeline-model` — Completion semantics change so CHECK pass and user approval are not sufficient for Done; Done/completed requires a successful merge, and approval state is scoped to the active stage.
- `http-api` — Approval endpoints must validate current-stage awaiting approval, route Check approval to the merge queue, return action-specific messages, and expose enough issue/merge state for clients to diagnose merge status.
- `cli-interface` — `mo issue show`, `mo issue approve`, and issue list/status formatting must surface trustworthy merge state and false-done warnings.
- `web-ui` — Issue detail, issue cards, approval panels, merge panels, and Done column warnings must show PR-like merge visibility, including explicit handling for `mergeState = null`.
- `local-issue-store` — Stored issue state invariants and archive behavior must prevent silently treating unmerged or unknown-merge issues as safely completed.

## Impact

- **Workflow checks**: `packages/cli/src/workflow/checks/user-approval-check.ts`, `plan-stage-runner.ts`, `check-stage-runner.ts`, and shared stage advancement logic must consume only current-stage approval and avoid leaking stale approval state.
- **Workflow completion**: `packages/cli/src/workflow/workflow-engine.ts`, `packages/cli/src/services/agent-runner-service.ts`, and merge success callbacks must ensure `done/completed` is written only after `MergeState.Merged`.
- **API**: `packages/cli/src/api/issues.ts` approval/reject/archive/show paths must validate approval stage, enqueue merges for Check approval, return accurate messages, and guard false-done archive/recovery cases.
- **Merge queue**: `packages/cli/src/git/merge-queue.ts` remains the post-Check merge path and must be the only path that transitions an approved Check issue to merged completion.
- **Storage**: `packages/cli/src/db/issue-repo.ts`, migrations, and issue state queries may need helper predicates for current-stage approval and false-done detection without requiring a new dependency.
- **CLI**: `packages/cli/src/cli/commands/issue.ts` must display merge status in `show`/list output and print API-provided approve messages instead of a fixed "agent resumed" message.
- **Web UI**: `packages/cli/web/src/components/MergeStatePanel.tsx`, `IssueCard.tsx`, `IssueDetailPage.tsx`, `ReviewApprovalPanel.tsx`, and related hooks/API types must render stable merge status, warnings, and action-specific approval labels.
- **Tests**: Add or update workflow, API, merge/archive, CLI formatting, and Web UI formatting tests covering stale approvals, merge-gated completion, false-done anomalies, and null merge-state interpretation.
