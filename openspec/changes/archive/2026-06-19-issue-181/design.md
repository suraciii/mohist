## Context

Mohist splits workspace responsibility across two processes:

- **Server (Orleans grains):** `IssueGrain.StartWorkflowAsync` builds the `WorkspaceIdentity` *deterministically* — `MohistWorkspaceLayout.IssueWorkspacePath(runnerRoot, project.Name, issue.Number)` for the path, `WorkflowRunBranch.For(workflowRunId)` for the branch — and binds it on `WorkflowRun.Workspace` via `WorkflowStartInput.Workspace` before the grain starts (`IssueVariableBuilder.cs:83`, `IssueGrain.cs:151`). The dispatch variables therefore already carry `workspace.path` / `workspace.branch`. A runner is claimed *lazily* afterwards, via the backlog (`WorkflowBacklogGrain.ClaimAsync` → `WorkflowGrain.AssignRunnerAsync`), and the grain dispatches work through `MakeDispatchAsync`.
- **Runner (TypeScript):** owns all git operations. `WorkExecutor.variables()` calls `WorkspaceManager.ensure()` on **every** work-item dispatch (`executor.ts:362`), and `ensure()` re-runs the full pipeline: `ensureCache()` (bare clone + `git fetch`) → `ensureFreshWorkspace()` (shared clone + base checkout + run-branch create/checkout) → marker write (`workspace.ts:25-67`).

Two defects follow from this:

1. **Per-task re-materialization.** Plan, build, check, and integrate each re-clone/refresh. Issue #180 failed at `integrate:prepare` because the late re-clone hit a transient GitHub TLS error — clone/checkout became a late-stage failure mode instead of startup infrastructure.
2. **Cache self-corruption.** `ensureCache()` deletes the bare cache on *any* `git fetch` failure or origin-URL mismatch (`workspace.ts:212`). Every workflow workspace is a `--shared` clone whose object store points at that cache via alternates, so one transient blip corrupts every already-running workspace that references it.

The runner also exposes a SignalR RPC surface (`runner-signalr.ts`) the server already calls for workspace *queries* and cleanup (`RunnerWorkspaceClient`: `GetDiff`, `GetFileContent`, `RemoveWorkspace`), invoked by the claimed runner's `connectionId`.

The `workspace-branch-stability` capability already guarantees the workspace stays on its run branch for the whole run and that task boundaries verify it; this change hardens *when* the workspace comes into existence, not its branch discipline.

## Goals / Non-Goals

**Goals:**
- Make workflow-workspace materialization a once-per-run boundary, performed before the first task's actual work executes, with the bound identity already recorded by the server at start.
- Make dispatch verify-only (exists, same-run ownership, on `workspace.branch`) and surface missing/corrupt/identity-mismatch as workflow-infrastructure failures distinct from task failures.
- Stop the bare cache from being deleted on transient fetch failure, and gate any cache replacement on "no active workspace references this object store."
- Keep `mohist/prepare` operating in-bound and `mohist/publish` using its isolated `--shared` landing clone.

**Non-Goals:**
- No reintroduction of `project.path` / `repository.path` / local-checkout paths into project/repository config (issue Non-Goals).
- No exposure of cache paths in workflow variables or public APIs.
- No server-side git execution; git stays in the runner.
- No change to `mohist/publish`'s landing-workspace contract (already specified by `worktree-manager`).
- No change to the branch-stability boundary checks (already correct).

## Decisions

### Decision 1: Materialization is runner-side and server-gated once per start boundary

**Choice.** After a runner is claimed and before the first dispatch for a start boundary, the grain invokes the runner's `MaterializeWorkspace` SignalR RPC. The runner performs cache prep, workspace checkout, branch creation, and marker writing, then later task/check dispatches verify the bound workspace only. The grain records a small `WorkspaceMaterializedAt` fact after a successful RPC so normal multi-task progression does not re-materialize.

**Why this is server-triggered.** The issue requires materialization to complete before the first task is dispatched. A dedicated RPC makes that ordering explicit in the same claimed-runner path the backlog already uses, and it lets the server fail the run as workflow-start infrastructure if materialization fails before any task is marked running.

**Why this satisfies "before the first task dispatch."** The start-boundary precheck (Decision 2) runs as the first step of the first dispatch, before the task's own action executes. Clone/checkout therefore complete before any plan/build/check/integrate *work* runs, and no later dispatch repeats them.

### Decision 2: Split `ensure()` into `materialize()` + `verify()`; move the call out of `variables()`

**Choice.** `WorkspaceManager.ensure()` is split:

- `materialize(work, signal)` — the existing full pipeline (cache prep, shared clone, base checkout, run-branch create/checkout, marker write). Writes/refreshes the marker. Invoked only when the marker is absent/mismatched.
- `verify(work, signal)` — read-only: workspace path exists, marker present and matches this run's identity, `HEAD` is on `workspace.branch`. Returns the resolved `WorkspaceInfo`. Never clones.

`WorkExecutor.execute()` gains an explicit start-boundary precheck **before** `executeOne()`: resolve the workspace from the server-supplied variables; if the marker is absent/mismatched call `materialize()`, otherwise call `verify()`. On any failure it reports a distinct infrastructure failure kind and does **not** run the action. `variables()` (`executor.ts:361`) no longer calls the heavy `ensure()`; it only assembles variables from the already-resolved workspace, so clone/checkout is no longer part of "building variables for every work item."

**Failure kinds.** Materialization/verification failures are reported as `workspace-missing`, `workspace-corrupt`, `workspace-identity-mismatch`, or `branch-invariant-violation` (the last reusing the existing branch-stability kind). These are workflow-infrastructure failures, attributed to the runner/infrastructure, distinct from `dirty-worktree`, `conflict`, `base-moved`, and provider failures. This is what lets CLI/API/UI distinguish startup-materialization failure from ordinary task failure without a separate lifecycle phase.

**Why precheck in `execute()` rather than inside `ensure()`.** Putting the materialize-vs-verify branch inside `ensure()` would leave the clone buried in `variables()` and make the "not every work item" guarantee implicit. An explicit precheck makes the boundary visible, lets us short-circuit before the action, and keeps `variables()` cheap and side-effect-free.

### Decision 3: `agent-job` owner-kind is exempt from the materialize/verify contract

Agent jobs (`owner-kind = agent-job`) supply a standalone `workspace.path` / `workDir` that the caller owns; the runner uses it as-is and never clones (issue #126). The start-boundary precheck skips `materialize()`/`verify()` for `owner-kind = agent-job` and resolves the workspace directly from variables, preserving the existing short-circuit behavior. Only `owner-kind = workflow` participates in the once-per-run materialization contract.

### Decision 4: Hardened bare cache — fetch failure never deletes; replacement is reference-gated

`ensureCache()` (`workspace.ts:205`) is rewritten:

- **Fetch failure does not delete.** If the cache exists and its `origin` URL matches `repository.gitUrl`, a failed `git fetch origin` is surfaced as a `cache-fetch-failed` (non-fatal where possible) error and the existing cache is kept. The workspace's shared alternates stay valid. This alone removes the #180 corruption path.
- **Replacement is allowed only on identity mismatch or verified corruption, and only when unreferenced.** URL mismatch or a verified-corrupt cache (e.g. `git fsck` failure) is the only justification for replacing the bare cache. Before deletion, the manager scans the project's `workspaces/` and `landing/` directories for clones whose `.git/objects/info/alternates` points at `<cachePath>/objects`. If any active workspace references it, deletion is refused and the error is surfaced (replacement deferred). This is the safeguard against deleting object stores still referenced by active workflow workspaces.
- **Cache fetch failure during initial materialization is fatal** (there is no prior cache to fall back on); during re-materialization across a transient blip it is non-fatal because the existing cache + existing workspace remain usable.

**Why scan alternates rather than track "active runs."** Cross-process active-run tracking is brittle (the runner that materialized may not be the runner that holds a reference). Alternates are the actual physical coupling — if a clone's alternates file names the cache, deleting the cache corrupts that clone. Scanning the project directory is O(number of workspaces) and runs only on the rare replacement path, not on every dispatch.

### Decision 5: `mohist/prepare` and `mohist/publish` contracts are unchanged

- **`mohist/prepare`** already does `git fetch <remote> <baseBranch>` + `rebase` inside `workspace.branch` without checking out the base branch (`registry.ts:183`). After Decision 2 it simply reaches a workspace that the start boundary already materialized; it never triggers materialization. If prepare runs against a missing/unbound workspace, the start-boundary precheck fails it as `workspace-missing` before the action runs.
- **`mohist/publish`** keeps its isolated temporary landing workspace (`createLandingWorkspace`, a `git clone --shared` of the workflow workspace, `workspace.ts:77`). This is already specified by `worktree-manager`'s "Isolated temporary landing workspaces" requirement and already derives from (never re-clones the remote to recover) the workflow workspace. No change.

### Decision 6: Minimal server-side materialization state

The server already binds `WorkflowRun.Workspace` at start. It additionally stores `WorkspaceMaterializedAt` after the start-boundary materialization RPC succeeds. Retry and rerun clear this fact so a fresh dispatch after workspace loss or a failed attempt re-enters the start-boundary materialization path; ordinary subsequent tasks/checks keep the fact and remain verify-only.

**Why no separate lifecycle phase.** The timestamp is only a dispatch gate, not a public workflow stage. It preserves the existing run state machine while making the one-time materialization boundary explicit and resettable on retry/rerun.

## Risks / Trade-offs

- **[First-dispatch materialization failure is attributed to that dispatch's task] → Mitigation.** The distinct `workspace-*` failure kinds let surfaces separate infra failure from task failure; the task is never marked completed/failed on its own merits when the failure kind is `workspace-*`. Document that the "startup materialization" phase is identified by failure kind, not by a separate workflow stage.
- **[Alternates scan misses workspaces outside the project dir or on other hosts] → Mitigation.** Multi-host runner setups are out of scope today (single shared `runnerRoot`); the scan is scoped to the project's runner-managed `workspaces/`+`landing/` dirs, which is where `--shared` clones live. If multi-host support arrives, this becomes an open question.
- **[Disk marker is the trust root — a stale/forgeable marker could let verify pass for a wrong run] → Mitigation.** The marker carries `issueId`/`issueNumber`/`workflowRunId` and `verify()` checks all three against the dispatch variables; the marker is written by the runner under the runner-managed root, not user-editable surface. A mismatch routes to `materialize()` (re-materialize), so a stale marker self-heals rather than silently passes.
- **[Cache kept on fetch failure means a run can proceed against a stale base until prepare rebases] → Mitigation.** This is strictly safer than today (which corrupts running workspaces). Stale-base refresh remains `mohist/prepare`'s explicit `fetch`+`rebase` job at integrate, unchanged.
- **[Behavior change for any caller that relied on `ensure()` re-materializing per call] → Mitigation.** Tests asserting "server-supplied `workspace.path` still causes runner clone/materialization" are rewritten to assert once-at-start (acceptance criterion). The `agent-job` short-circuit is preserved.

## Migration Plan

1. **Runner first.** Implement `materialize()`/`verify()` split, the `execute()` precheck, the failure kinds, and cache hardening. Existing runner tests that drive `ensure()` per dispatch are migrated to the new boundary.
2. **Regression coverage.** Add a test proving a run that completes plan/build/check does not issue a remote `git clone` at integrate (record/spy the git invocations), and a test that a transient `git fetch` failure leaves the cache and an existing shared workspace intact.
3. **Failure surfacing.** Map the new `workspace-*` failure kinds through the runner→server report path so CLI/API/UI render them as workspace-materialization failures.
4. **Rollback.** The change is localized to the runner execution/workspace layer plus failure-kind mapping; rollback is reverting the runner package. No data migration, no schema change (the marker schema and `WorkspaceIdentity` record are unchanged). The server-side `Workspace` binding already exists, so partial rollout (new server, old runner) degrades to today's per-dispatch behavior without data loss.

## Open Questions

- Should a `cache-fetch-failed` during re-materialization *retry* in-place with backoff before falling back to the stale cache, or fail fast and let the next dispatch retry? (Lean: fail fast, let dispatch retry — keeps the boundary simple.)
- For multi-runner/multi-host deployments (future), does the alternates-reference scan need to become cooperative (e.g., a reference-count file under the cache dir) instead of a directory scan? Out of scope for this issue.
