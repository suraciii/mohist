## Context

Issue 628 is a P0 runner reliability defect exposed while recovering Epic 67 issue #567. A recovery task completed at a detached `HEAD`; an exact retry reused the same workflow context but reached another detached commit and failed again at the task boundary. The runner therefore cannot safely advance the workflow even though the expected run branch is known.

The current runner has several related but separate paths:

- `WorkspaceManager` owns the stable workspace path and per-run branch, and performs preparation and health checks when a workflow workspace is entered.
- `mohist/workspace-prepare` cleans residual rebase, merge, and cherry-pick state, cleans dirty files, and accepts an explicit `expectedBranch` input.
- `mohist/rebase` currently accepts `baseBranch` and `remote`, but its successful result is not intrinsically tied to the workflow run branch. The base ref is a rebase target, not a workspace identity.
- `WorkExecutor` performs branch checks at task start and end through `branch-stability.ts`, but action-level success and workspace preparation do not yet share the same completion contract or diagnostic format.
- Recovery scheduling returns the existing `WorkItemResult` and adds handler or retry tasks. The workspace path, workflow run, and run branch must remain bound to the original dispatch during this process.

This design implements the motivation in `openspec/changes/issue-628/proposal.md` and the scenarios in `openspec/changes/issue-628/specs/rebase-recovery-branch-integrity/spec.md`. It does not alter AgentSession replay, Runner slot policy, or per-work resource limits.

## Goals / Non-Goals

**Goals:**

- Make `workspace.branch`, the expected run branch, the authoritative branch identity for recovery and task boundaries.
- Treat a recovery as successful only when `HEAD` is attached to exactly that branch, the worktree is clean, and no rebase, merge, or cherry-pick residual state remains.
- Repair a detached or mismatched workspace when it is safe to do so, and verify the complete state after every repair step.
- Return durable, actionable failures containing the expected branch, observed branch or detached ref, workspace condition, and failed operation.
- Preserve the existing workflow workspace path, workflow run identity, and run branch across recovery failures and exact retries.
- Prove detached-head, branch-mismatch, conflict, cleanup-failure, successful-repair, task-boundary, and idempotent-rerun behavior with deterministic fake-worktree tests.

**Non-Goals:**

- Changing how agents are stopped, replayed, or reconciled after an unknown result.
- Changing workflow recovery budgets, Runner slot allocation, or per-work resource limits.
- Creating a new workspace or run branch as a fallback for a failed branch repair.
- Changing rebase, squash, fetch, merge, push, or conflict-resolution semantics beyond their branch and workspace safety checks.
- Adding a new server persistence model or changing the `WorkItemResult` transport contract.

## Decisions

### 1. Use the workflow run branch as the only recovery branch identity

The executor will resolve the expected branch from `variables.workspace.branch`. Git recovery actions that need this value will receive it through an engine-sourced input, so workflow authors do not need to duplicate it in each task declaration. `mohist/workspace-prepare` already has `expectedBranch`; `mohist/rebase` will use the same hidden `workspace.branch` source while retaining `baseBranch` only as the rebase target.

The rebase action will validate the expected branch after the rebase and any requested squash have finished. A branch named `HEAD`, an empty branch result, or a different branch is invalid even when the rebase command itself exits successfully. The base ref may still be checked for rebase correctness, but it can never satisfy the branch-identity check.

**Alternative considered:** Add the expected branch to `ActionHost`. This would make the value available without an input, but it expands the host contract for every action and leaks workflow state into otherwise generic action APIs. An engine-sourced input follows the existing input-injection mechanism and remains invisible in the public action catalog.

**Alternative considered:** Rely only on `WorkExecutor` start and end checks. This would catch many invalid results, but a direct recovery action could still report success, and the rebase action would not own its completion invariant. Action-level verification and executor boundary checks are both required.

### 2. Define one workspace-health contract with narrow adapters

Introduce one internal semantic model for a workspace health snapshot and its diagnostic fields:

- expected branch;
- observed branch, detached reference, or probe failure;
- clean/dirty worktree status;
- rebase, merge, and cherry-pick residual state;
- the operation that failed, when applicable.

`workspace-prepare` will use the model to drive its repair state machine. `rebase` will use the same evaluator for its final success check. `branch-stability.ts` and `WorkspaceManager` may keep their existing Git-process adapters, but they must produce the same branch and failure semantics. Shared evaluation and formatting should remain small and internal; it should not become a general Git abstraction.

The preparation state machine is deterministic:

1. Probe branch, detached ref, worktree status, and all residual operation markers.
2. Return success immediately only for the exact expected branch with a clean, non-residual workspace.
3. Abort residual operations in a fixed order: rebase, merge, then cherry-pick. After each abort, probe that specific residual state again. An abort failure or an unverifiable residual state stops the state machine.
4. If the tree is dirty, run `git reset --hard HEAD` followed by `git clean -fd`; re-probe before continuing.
5. If the branch is detached or mismatched, check out the existing expected branch. Do not create a branch from `baseBranch`, force-create a new branch, or replace the workspace.
6. Run a complete final probe. Only the exact expected branch, clean status, and absence of every residual marker can return success.

The rebase action will preserve an unresolved conflict as a `conflict` failure so a configured resolver can work with it. It will not claim success on a conflict or on a detached/mismatched post-rebase state. Workspace preparation remains responsible for aborting and validating residual operation state before a retry.

**Alternative considered:** Keep independent branch and health checks in each action. This is a smaller immediate edit, but it would allow the two actions to disagree about detached `HEAD`, probe failures, or residual state. A shared semantic contract avoids that drift while leaving command execution local to each existing adapter.

**Alternative considered:** Move all action-time checks into `WorkspaceManager`. The manager owns workspace lifecycle, not the state produced by an individual action, and this would couple action execution to manager internals. It also would not protect direct action completion. The manager will be aligned with the contract rather than made the sole owner of it.

### 3. Enforce the invariant at task boundaries

`WorkExecutor` will keep the start probe before action invocation and the end probe after a successful action, before artifact/worktree settlement can convert the task to a completed result. When an expected branch is defined, a detached `HEAD`, a branch mismatch, a failed branch probe, or an inability to establish that the directory is the expected Git workspace is a failure. The diagnostic uses the same expected/observed terminology as action failures.

Normal recovery scheduling remains compatible with the current `tryRecovery` flow, but a scheduled recovery is not an action success: the original branch-integrity error and its diagnostic must remain attached to the result, and no invalid action output may be emitted as successful recovery. The final task boundary must always run before a result can be settled as a successful task.

**Alternative considered:** Check only at task start. This prevents an already-invalid workspace from being used, but does not protect against an action leaving the workspace detached. The end check is necessary for the observed failure mode.

### 4. Carry diagnostics through existing failure reporting

Use the existing `ActionError` and `WorkItemResult` path. Rebase and workspace preparation failures will use their declared failure codes and a bounded diagnostic message containing stable fields such as `operation=checkout`, `expectedBranch=...`, `observedBranch=...`, `observedRef=...`, `dirty=...`, and residual-state details. Conflict failures will continue to list unresolved files. Probe failures will identify the Git command and its output or exit code.

The diagnostic is assembled at the point of failure and survives normal recovery scheduling and exact retry without requiring callers to infer state from action output or logs. Existing output remains reserved for successful action output; no new result-replay or server schema is needed.

**Alternative considered:** Add a new structured failure payload to `WorkItemResult`. That would make machine inspection easier, but it creates a wire-model change for a problem that can be addressed by the existing error envelope. The snapshot model can be introduced internally and promoted later if operational data shows that text is insufficient.

### 5. Preserve identity and make preparation idempotent

`WorkspaceManager.prepare` and `verify` will use the same final branch and health checks for an existing workspace. A branch repair failure must leave the existing workspace and its identity binding in place. A retry must resolve the same `workflowRunId` to the same workspace path and the same run branch; it must not clone a replacement merely because checkout or cleanup previously failed.

Recovery task materialization will continue to inherit the original workflow variables and workspace binding. Self-retry copies the original action declaration and recovery metadata, while the executor resolves the same workspace branch again. Repeated preparation of an already healthy workspace takes the fast path and issues no replacement or identity-changing commands.

**Alternative considered:** Re-clone on any preparation error. Re-cloning can hide the original failure and discard the workspace needed by a conflict resolver, and it violates the retry identity requirement. It remains appropriate only for initial materialization or an independently detected corrupt/mismatched workspace identity, not for branch repair failure.

### 6. Test the state machine with a stateful fake worktree

Extend the existing fake Git/worktree tests rather than relying only on fixed command-response tests. The fake will track branch, detached ref, dirty status, residual markers, available branches, and transient command failures so assertions can verify both final state and command ordering.

Coverage will include:

- clean expected branch fast path with no mutation;
- detached clean workspace repaired by checkout and accepted only after a follow-up probe;
- mismatched dirty workspace cleaned, checked out, and verified in order;
- successful rebase followed by detached or wrong `HEAD`, which remains a failure;
- unresolved rebase conflicts reported with file names and never represented as success;
- abort failure, residual-state reprobe failure, checkout failure, and final dirty/branch/residual verification failure;
- task-start detached/probe failures and action-end detached/mismatch failures;
- a transient repair failure followed by an exact retry against the same path and branch;
- repeated preparation of a healthy workspace without replacement or branch creation.

Manifest and executor tests will also verify that the engine-sourced expected branch is populated from `workspace.branch` and that existing workflow profiles do not need a second branch declaration.

## Risks / Trade-offs

- [Risk] `reset --hard` and `clean -fd` are destructive and can remove unresolved or uncommitted files. -> Mitigation: perform them only in the explicit workspace-preparation path, after residual abort handling, and retain the rebase action's conflict path without automatic cleanup so resolver tasks can operate first.
- [Risk] A workspace can change between the action's final probe and the executor's end probe. -> Mitigation: keep both checks, run the executor check immediately before settlement, and fail closed when a probe cannot establish the invariant.
- [Risk] Repeated implementations of Git probing can drift between actions and `WorkspaceManager`. -> Mitigation: share the health snapshot/evaluator and diagnostic contract, keep adapters narrow, and require the same fake-worktree scenario matrix for each boundary.
- [Risk] Older or custom workflows may invoke `mohist/rebase` without a usable `workspace.branch`. -> Mitigation: treat the missing engine value as an actionable preparation/input failure, audit built-in profiles before rollout, and do not silently substitute `baseBranch`.
- [Risk] A branch checkout can fail because the expected branch ref is absent or the worktree remains in an unusable state. -> Mitigation: report expected and observed identity plus the checkout/reset operation, preserve the existing binding, and let the exact retry converge after the external failure is removed.
- [Risk] Existing recovery scheduling represents a scheduled recovery as `completed`. -> Mitigation: preserve the original branch-integrity error and message, test that invalid action output is never projected as success, and distinguish orchestration scheduling from recovery action completion in diagnostics.
- [Risk] Treating a non-Git directory as a successful branch check could bypass the invariant. -> Mitigation: when an expected branch is present, a failed or non-Git branch probe is a boundary failure; retain the non-Git exception only for actions with no expected workspace branch.

## Migration Plan

1. Add the internal health contract and align `workspace-prepare`, `rebase`, `WorkspaceManager`, and executor boundary checks with it.
2. Add the engine-sourced expected-branch declaration to the rebase action and update direct action tests and fake-worktree fixtures. Built-in workflow profiles continue to derive the value from `workspace.branch`; no workflow author migration is required.
3. Run the focused runner test suites for rebase, workspace preparation, executor branch boundaries, recovery scheduling, and workflow-profile contracts, followed by the normal runner test suite.
4. Deploy the runner change without a server or workspace schema migration. Existing workspaces remain at their current paths and retain their existing identity markers and run branches.
5. For an affected run, the first retry should invoke the same workspace preparation and branch checks. Operational diagnostics should be monitored for expected/observed branch and failed-operation fields.

Rollback is a runner binary/configuration rollback only. It does not delete or replace workspaces. If a workspace was left detached by the newer runner, use the compatible preparation path or an operator-approved Git repair to restore the recorded run branch before retrying with the older runner; otherwise the pre-existing defect can recur. No server data rollback is required.

## Open Questions

- Confirm whether any supported external workflow invokes `mohist/rebase` outside a materialized workflow workspace. If so, decide whether that invocation should fail explicitly or receive a separate non-workflow contract.
- Decide whether `mohist/rebase-status` should adopt the expected-branch check as well. This design limits the new invariant to recovery completion and workspace preparation; its base-relative status semantics otherwise remain unchanged.
- Determine from operational diagnostics whether the bounded error message is sufficient, or whether a future version should expose the health snapshot as structured failure fields in the workflow API.
