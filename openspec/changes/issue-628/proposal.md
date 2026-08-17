## Why

Rebase conflict recovery can leave the workflow workspace at a detached `HEAD` when the recovery task reaches its boundary. The runner then cannot safely advance the workflow, and an exact retry can encounter the same detached workspace state even though the expected run branch is known. This is a P0 reliability issue exposed while recovering Epic 67 issue #567.

## What Changes

- Define rebase recovery completion around the expected run branch: recovery succeeds only when the workspace is on that branch and satisfies the required clean, non-residual state.
- Ensure detached `HEAD` and other branch mismatches are detected before task completion and are never reported as successful recovery. A branch-integrity failure remains a failed task report and does not attach configured recovery follow-ups that could turn it into completion; a later explicit retry uses the preserved identity.
- Restore the expected run branch when the workspace can be repaired safely; otherwise return a durable, actionable failure that identifies the expected and observed branch/state.
- Preserve the existing workflow workspace and run-branch identity across recovery failures so an exact retry is safe and idempotent.
- When an Agent result settlement durably transitions from `Unknown` to `Blocked`, release that workflow from Runner `activeWorks`, capacity usage, and missing-redelivery reconciliation at the same exactly-once projection boundary. Preserve the original runner/work identity so a matching late authoritative result can still settle, while mismatched reports remain stale.
- Add deterministic fake-worktree regression coverage for detached `HEAD`, successful checkout, conflict state, and idempotent rerun behavior, plus fake-time server/control-plane coverage for blocked settlement, projection release, capacity, and late-result fencing.
- Keep Agent result replay, Runner slot policy, and per-work resource limits outside this change; releasing a durably blocked active-work projection is required behavior, not a slot-policy change.

## Capabilities

- `rebase-recovery-branch-integrity`: Expected run-branch enforcement for rebase conflict recovery and workspace preparation, including detached-`HEAD` repair or failure, task-boundary behavior, workspace identity preservation, and idempotent retries.

## Impact

- **Runner actions:** `packages/runner/src/actions/rebase.ts` and `packages/runner/src/actions/workspace-prepare.ts` will share the recovery contract that validates branch state and reports actionable failures.
- **Runner execution:** `packages/runner/src/runtime/executor.ts`, `branch-stability.ts`, `workspace.ts`, and recovery scheduling will need to preserve the branch invariant at action and task boundaries without converting an invalid workspace into successful completion.
- **Workflow result handling:** Existing `WorkItemResult` failure reporting will carry the durable branch/workspace diagnostic. For `branch-invariant-violation`, the runner will report `failed` without `addTasks`; no recovery handler or retry task may be projected as successful completion, and no new AgentSession result-replay protocol is required.
- **Server/control plane:** The existing durable blocked-settlement projection will be the sole release boundary for Runner `activeWorks`, capacity usage, and missing-redelivery queries. The workflow assignment and settlement identity remain stored for matching late reports; no new persistence model is required.
- **Tests:** Runner fake Git/worktree tests, especially workspace preparation, rebase recovery, task-boundary, and retry coverage, will be extended with the detached-head regression matrix. Server/control-plane tests will use fake time to assert the durable blocked transition, exactly-once projection release, capacity release, and matching versus mismatched late reports.
- **Dependencies and public interfaces:** No new dependency is expected, and no breaking user-facing workflow or CLI interface is intended.
