## Why

Rebase conflict recovery can leave the workflow workspace at a detached `HEAD` when the recovery task reaches its boundary. The runner then cannot safely advance the workflow, and an exact retry can encounter the same detached workspace state even though the expected run branch is known. This is a P0 reliability issue exposed while recovering Epic 67 issue #567.

## What Changes

- Define rebase recovery completion around the expected run branch: recovery succeeds only when the workspace is on that branch and satisfies the required clean, non-residual state.
- Ensure detached `HEAD` and other branch mismatches are detected before task completion and are never reported as successful recovery.
- Restore the expected run branch when the workspace can be repaired safely; otherwise return a durable, actionable failure that identifies the expected and observed branch/state.
- Preserve the existing workflow workspace and run-branch identity across recovery failures so an exact retry is safe and idempotent.
- Add deterministic fake-worktree regression coverage for detached `HEAD`, successful checkout, conflict state, and idempotent rerun behavior.
- Keep Agent result replay, Runner slot policy, and per-work resource limits outside this change.

## Capabilities

- `rebase-recovery-branch-integrity`: Expected run-branch enforcement for rebase conflict recovery and workspace preparation, including detached-`HEAD` repair or failure, task-boundary behavior, workspace identity preservation, and idempotent retries.

## Impact

- **Runner actions:** `packages/runner/src/actions/rebase.ts` and `packages/runner/src/actions/workspace-prepare.ts` will share the recovery contract that validates branch state and reports actionable failures.
- **Runner execution:** `packages/runner/src/runtime/executor.ts`, `branch-stability.ts`, `workspace.ts`, and recovery scheduling will need to preserve the branch invariant at action and task boundaries without converting an invalid workspace into successful completion.
- **Workflow result handling:** Existing `WorkItemResult` failure reporting will carry the durable branch/workspace diagnostic; no new AgentSession result-replay protocol or server persistence model is required.
- **Tests:** Runner fake Git/worktree tests, especially workspace preparation, rebase recovery, task-boundary, and retry coverage, will be extended with the detached-head regression matrix.
- **Dependencies and public interfaces:** No new dependency is expected, and no breaking user-facing workflow or CLI interface is intended.
