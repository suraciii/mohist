## Why

Mohist's recovery actions currently do not express stable user intent: `retry` may be blocked by checkpoint or artifact existence, while `rerun` may continue from incomplete work instead of restarting the current stage. This leaves users unable to decide whether to retry from the failed point, rerun the stage from the beginning, inspect state manually, or wait for external recovery.

## What Changes

- Align `Retry` with failed-work recovery: retry availability is based on the latest WorkflowRun's current-stage failed work, not on whether an OpenSpec artifact or checkpoint file already exists.
- Make `Retry` reset only the failed work and same-stage downstream work that depends on it, while preserving earlier successful work that remains valid.
- Align `Rerun Stage` with current-stage restart semantics: it keeps the issue in the same stage, preserves earlier passed stages, clears current-stage attempt state, and restarts from the first work item.
- Ensure Plan stage rerun re-executes Plan artifact work instead of silently skipping artifacts because files such as `proposal.md`, `specs/`, `design.md`, or `tasks.json` already exist.
- Return distinguishable recovery errors for cases such as no failed WorkflowRun, no retryable failed work, and missing required worktree or artifacts.
- Show recovery action errors consistently in the Web UI so failed retry requests are visible like rerun, start, close, and reopen errors.
- Keep product and command copy aligned to the recovery vocabulary from #178: retry, rerun, and rewind; do not reintroduce restart.

## Capabilities

### New Capabilities

- None

### Modified Capabilities

- `workflow-run` - Retry and rerun state transitions must distinguish failed-work retry from current-stage rerun attempts.
- `http-api` - Recovery endpoints must expose availability and failure reasons using WorkflowRun state rather than checkpoint-only gating.
- `cli-interface` - User-facing recovery copy and errors must use retry, rerun, and rewind terminology consistently.
- `web-ui` - Issue recovery actions must surface retry errors through the same action error display pattern as other issue actions.

## Impact

- `packages/cli/src/workflow/domain/` - WorkflowRun and StageRun recovery semantics for failed work, downstream invalidation, and current-stage rerun reset.
- `packages/cli/src/workflow/workflow-engine.ts` and stage runners - Resume/recovery execution must honor WorkflowRun decisions and avoid artifact-exists skip behavior during rerun.
- `packages/cli/src/workflow/plan-stage-runner.ts` - Plan artifact work retry/rerun behavior, especially failures before `tasks.json` exists.
- `packages/cli/src/services/workflow-run-service.ts` and workflow application service code - Recovery availability, state reset, and error reporting based on latest WorkflowRun state.
- `packages/cli/src/api/issues.ts` - `POST /api/issues/:number/retry`, `POST /api/issues/:number/rerun`, and related recovery responses.
- `packages/cli/src/cli/commands/issue.ts` and API client code - Recovery command copy and surfaced server errors.
- `packages/cli/web/src/components/IssueDetailPage.tsx`, `IssueCard.tsx`, and `packages/cli/web/src/lib/api.ts` - Recovery button behavior, action error display, and terminology.
- Regression tests for the #215 shape: Plan fails while generating `tasks.json`, `Retry` retries the failed Plan work, and `Rerun Stage` restarts Plan from the first Plan work.
