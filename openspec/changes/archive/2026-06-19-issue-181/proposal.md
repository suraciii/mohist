## Why

Repository clone and workspace checkout are startup infrastructure, but today Mohist re-materializes the workflow workspace on every work-item dispatch. Issue #180 proved how dangerous this is: a run that had already finished plan, build, and check failed in `integrate:prepare` because the runner tried to clone the repository again and hit a transient GitHub TLS error. Worse, the cache path deletes the bare cache on any `git fetch` failure (`workspace.ts:212`), and every workflow workspace is a shared clone whose object store points at that cache — so a single transient network blip can corrupt an already-running run. Workspace materialization must become a workflow-start boundary, not a per-task side effect.

## What Changes

- Workflow start performs the first and only repository materialization for a run: prepare/refresh the runner-owned bare cache from `repository.gitUrl`, create the workflow workspace at the bound `workspace.path`, checkout the configured base branch, create and checkout the workflow run branch, write the workspace marker, and bind that workspace identity to the WorkflowRun before the first task is dispatched.
- **BREAKING** Work-item dispatch no longer clones or re-materializes the workflow workspace. `WorkExecutor.variables()` stops running clone/checkout-style `WorkspaceManager.ensure()` work for every work item.
- Dispatch-time workspace handling only verifies an already-bound workspace (exists, belongs to the same workflow run, is on `workspace.branch`, satisfies task boundary invariants) and reports explicit workspace-missing / workspace-corrupt / branch-invariant failures. A missing or corrupt workspace is surfaced as a workflow infrastructure failure, not a business-task clone failure.
- `mohist/prepare` runs fetch/rebase inside the existing bound workflow workspace and no longer triggers workflow workspace creation.
- `mohist/publish` may keep its isolated temporary landing workspace, but only as a `--shared` clone derived from the workflow workspace; it must not mutate the workflow workspace branch or depend on re-cloning the remote to recover it.
- A failed `git fetch origin` no longer deletes an existing bare cache. Cache replacement is limited to clear remote-identity mismatch or verified corruption, with safeguards that prevent deleting object stores still referenced by active workflow workspaces.
- Cache paths remain runner implementation details and are not exposed in project/repository configuration, workflow variables, or public APIs.
- CLI/API/UI failure messages distinguish workflow-start workspace materialization failure from ordinary task failure.

## Capabilities

### New Capabilities

- `workspace-materialization`: A WorkflowRun owns exactly one execution workspace for the run. The workspace is materialized and bound once at workflow start (cache prep, workspace creation, base checkout, run-branch checkout, marker write, identity binding before the first dispatch). Work-item dispatch consumes the already-bound workspace: it verifies existence, same-run ownership, and run-branch invariant, and reports missing/corrupt/identity-mismatch as workflow infrastructure failures, but it does not re-clone or re-materialize. `mohist/prepare` operates inside this bound workspace.

### Modified Capabilities

- `workflow-run`: The workflow-start requirement (REQ-WR-001) gains the obligation to materialize and bind the single workflow workspace before the first task is dispatched, and to treat work-item dispatch as consuming that bound workspace rather than (re)creating it. Integrate `prepare` runs against the bound workflow workspace.

## Impact

- **Runner execution layer**: `WorkExecutor.variables()` / `executeOne()` (`packages/runner/src/runtime/executor.ts`) no longer perform clone/checkout per work item; dispatch gains a verify-only workspace precheck with explicit missing/corrupt/branch-invariant failure kinds.
- **Workspace manager**: `WorkspaceManager` (`packages/runner/src/runtime/workspace.ts`) splits its `ensure()` materialization into a start-time materialize+bind path and a dispatch-time verify path; the bare-cache `ensureCache()` deletion-on-fetch-failure behavior is removed and replaced with identity/corruption-gated, reference-safe replacement.
- **Workflow start pipeline**: the server-side start/resume path (WorkflowRun creation, first dispatch) must invoke workspace materialization and record the bound workspace identity before the first StageRun task is scheduled.
- **Integrate actions**: `mohist/prepare` (`packages/runner/src/actions/registry.ts`) is confirmed as operating against the existing workspace; `mohist/publish` continues using an isolated `--shared` landing workspace derived from the workflow workspace.
- **Tests**: existing tests asserting "server-supplied `workspace.path` still causes runner clone/materialization" are rewritten to assert the new once-at-start contract; regression coverage proves an integrate-stage retry cannot re-clone the workflow repository.
- **Failure surfacing**: CLI/API/UI gain a distinct workflow-start workspace-materialization failure category.
