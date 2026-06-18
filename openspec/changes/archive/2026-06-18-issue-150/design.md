## Context

Mohist materializes a runner-owned **workflow workspace** per run, checked out to a per-run branch (`workspace.branch`, e.g. `mohist/run-<runId>`). The foundation is already correct: `WorkspaceManager.ensure()` (`packages/runner/src/runtime/workspace.ts`) clones a shared cache, creates the run branch, and checks it out; `integrate:prepare` (`prepareAction`) rebases the run branch onto `refs/remotes/<remote>/<baseBranch>` without ever checking out the base branch.

The violation is in two later integration actions in `packages/runner/src/actions/registry.ts`:

- `publishAction` (registry.ts:267) runs `git checkout <baseBranch>` *inside the workflow workspace* (line 308), then `merge --ff-only`, `merge --squash`, `commit`, and `push` all while the workspace is on the base branch. On failure it `reset --hard`s, but for the duration of publish the workspace is on `master`, not the run branch.
- `runSquashMergePreflight` (registry.ts:435, used by `mergeReadyAction`) checks out the base branch in the workspace (line 439), runs `merge --squash --no-commit`, then tries to restore the original ref.

Once an action switches the workspace to the base branch, a failed or partially recovered run leaves the workspace in a surprising state, and the next retry cannot distinguish issue work from branch-switch side effects. The task-completion invariant today is only "clean worktree" (`enforceCleanWorktree` in `packages/runner/src/runtime/executor.ts`); there is no branch-stability counterpart.

Constraints: this is high-risk (core runner integration + publish/merge semantics), the project is in active dev with no version-compat requirement, and the user-visible delivery outcome (one clean landing commit on the base branch, pushed) must not change.

## Goals / Non-Goals

**Goals:**
- Make the workflow workspace's run branch a runtime invariant: the workspace stays on `workspace.branch` for the whole run.
- Rework `integrate:publish` to land the single squash commit through a branch-stable mechanism so the workspace never checks out the base branch.
- Make the `merge-ready` squash-mergeability preflight ref-safe (no base-branch checkout in the workspace).
- Add task-boundary branch-stability verification (start + end) with evidence, sibling to the existing clean-worktree evidence.
- Classify branch-invariant violations as a distinct runner/action-bug failure kind.
- Preserve today's delivery outcome exactly (one landing commit on the base branch, pushed to the remote).

**Non-Goals (from issue):**
- No new read-only task classes or user-facing task taxonomy.
- No making the main repository checkout the workflow workspace.
- No keeping `project.path` as a workflow execution fallback.
- No separate user-facing push task.
- No relaxing the clean-worktree invariant.
- No full integration-drift repair workflow — this issue only creates the branch-stability foundation that makes such repair safe.

## Decisions

### Decision 1: Enforce branch-stability as boundary checks in the executor, not inside actions

The invariant is enforced in `WorkExecutor.executeOne` (`packages/runner/src/runtime/executor.ts`) at two boundaries, mirroring `enforceCleanWorktree`:

1. **Start check** — after `workDir` is resolved and before the action runs: read `git rev-parse --abbrev-ref HEAD`, compare to `variables.workspace.branch`. On mismatch, do not run the action; report a `branch-invariant-violation` (expected vs observed branch).
2. **End check** — after the action reports success and *before* `enforceCleanWorktree`: re-verify the branch. The branch check runs before the clean-worktree check so a wrong branch is never mis-reported as a dirty worktree (per the `workspace-branch-stability` spec scenario "Branch-invariant violation is distinct from a dirty worktree").

The expected branch comes from `variables.workspace.branch`, already populated by `variables()` → `WorkspaceManager.ensure()` (executor.ts:243-252).

**Alternatives considered:**
- *Enforce inside each action* — rejected: scattered, easy to miss for new actions, and cannot catch a wrong-branch state left by a previous action.
- *End-only check* — rejected: a task that *starts* on the wrong branch (left there by a prior bug) should be blocked before it does damage, not only caught after.

### Decision 2: Branch-stable publish via an isolated temporary landing workspace

`publishAction` stops using `context.workDir` (the workflow workspace) for base-branch operations. The workspace is used only for **read-only** checks (`rev-parse` of source, `merge-base --is-ancestor` to confirm the run branch is prepared against the latest remote base). The landing commit is constructed in an **isolated temporary landing workspace**:

1. Materialize a temp landing workspace: `git clone --shared <workspacePath> <landingPath>` (the workspace repo already contains both the base branch and the prepared run-branch commits, so the clone sees both).
2. In the landing workspace: set `origin` to the real remote gitUrl (as `ensureFreshWorkspace` already does), `checkout <baseBranch>`, `fetch`, `merge --ff-only origin/<baseBranch>`, `merge --squash <runBranch>`, `commit`, `push origin <baseBranch>`.
3. Dispose the landing workspace (best-effort `rm -rf`); it is isolated, so cleanup failure cannot affect the workflow workspace.

The workflow workspace never leaves `workspace.branch`. The failure kinds (`retry-safe`, `base-moved`) and the rollback semantics are preserved, but rollback now means "discard the landing workspace / reset the landing workspace's base ref", not "reset --hard the workflow workspace".

**Alternatives considered:**
- *Ref-only landing via plumbing* (`git merge-tree` / `commit-tree` + `push <sha>:<baseBranch>`): avoids a temp checkout entirely. Rejected for now: reproducing the exact squash-commit shape and commit metadata (`-m` header, issue number) via plumbing is fiddlier to validate as equivalent; the temp workspace reuses the existing `--shared` clone pattern and is faithful. Ref-only remains a future optimization (see Open Questions).
- *Push the run branch and merge server/PR-side*: rejected — violates the "no separate push task" non-goal and changes the delivery shape.

### Decision 3: Landing workspace clone source is the workflow workspace (shared), not the bare cache

The run-branch commits exist only in the workflow workspace until publish pushes them; the bare cache (`ensureCache`) does not contain them. Therefore the landing workspace is cloned `--shared` from the workspace path. `--shared` creates an independent repo whose object store references the source via alternates (read-only), so removing the landing clone cannot corrupt the workspace's objects.

**Alternative considered:**
- *Clone from the bare cache*: rejected — would not see the prepared run-branch commits.
- *`git worktree add` (linked worktree sharing the workspace's object store)*: rejected — tighter coupling; removal must go through `git worktree remove` and a botched removal risks the workspace's object store. `clone --shared` is more isolated and matches the existing `ensureFreshWorkspace` pattern.

### Decision 4: Merge-ready preflight becomes ref-safe via the same landing-workspace mechanism

`runSquashMergePreflight` no longer checks out the base branch in the workflow workspace. It materializes an isolated landing workspace (clone --shared from the workspace), checks out the base branch *there*, runs `merge --squash --no-commit <source>`, captures conflict files, and discards the workspace. The structured output (`canMerge`, `conflictFiles`, `baseSha`, `candidateHeadSha`, `mergeBaseSha`, …) is unchanged; only the location of the checkout changes. This keeps preflight parity with publish (same squash semantics) and leaves the workflow workspace on `workspace.branch`.

**Alternative considered:**
- *`git merge-tree --write-tree <base> <source>`* — a pure ref-only merge with no working tree. Attractive (no temp dir), but squash-vs-merge-tree semantics and conflict reporting differ slightly from the authoritative publish path; adopting it now would create two code paths to keep equivalent. Treat as a later optimization (Open Questions).

### Decision 5: New evidence/failure kind `branch-invariant-violation`, distinct from `dirty-worktree`

Add a `branch-stability` evidence record (expected branch, observed branch, boundary: start|end) to the task result, and a `branch-invariant-violation` failure kind. This joins the existing taxonomy: `dirty-worktree`, `git-index-lock` (executor), and `retry-safe` / `base-moved` / `conflict` (delivery). Downstream renderers (CLI `DeliveryFailureGuidance`, web `delivery-failure.ts`) gain a branch for the new kind, attributed to runner/action, not issue work. Evidence is recorded in the result JSON the same way `dirty-worktree` evidence is (executor.ts:691-699).

### Decision 6: Landing workspace lifecycle owned by WorkspaceManager

`WorkspaceManager` gains `createLandingWorkspace(...)` / `disposeLandingWorkspace(...)` (or a single scoped helper). Landing dirs live under `<runnerRoot>/<projectSlug>/landing/<runId>-<uuid>/`, are per-run, and are disposed after publish/preflight. A prune step (sibling to the existing worktree prune) removes stale landing dirs from crashed runs. Because landing workspaces are isolated `--shared` clones, a leftover never affects the workflow workspace's branch or working tree.

## Risks / Trade-offs

- **[Shared-clone object-store corruption]** -> Landing workspace uses `clone --shared` (alternates, read-only refs to source objects); disposal is `rm -rf` of an independent repo and cannot delete source objects. Do *not* use linked `git worktree add`, whose removal can damage the shared store.
- **[Landing workspace leaks on crash]** -> Landing dirs are per-run-uuid and isolated; add a prune pass on workspace ensure. A leak does not affect the run branch.
- **[Push from landing workspace targets wrong remote]** -> Set landing workspace `origin` to the configured gitUrl before push, exactly as `ensureFreshWorkspace` resets origin; add a test asserting the push remote.
- **[Equivalence of the landed commit]** -> The squashed commit produced in the landing workspace must equal today's in-workspace result (same tree, same base parent, same message). Add a validation test comparing the landed commit shape before/after.
- **[Test churn hides regressions]** -> `publish.spec.ts`, `push.spec.ts`, and `issue-112-regression.spec.ts` currently assert `"checkout master"` in the workspace call sequence. Rewrite them to assert the workspace call sequence contains *no* `checkout <baseBranch>` and that landing-workspace ops happen in a separate workDir.
- **[Boundary check false-positives on detached HEAD]** -> The run branch is always a real branch ref (`mohist/run-<runId>`); compare `--abbrev-ref HEAD` against it. A detached HEAD at a boundary is itself a violation and should be reported as such, not silently tolerated.
- **[Checks vs tasks scope]** -> The end-check initially wraps task execution (`executeOne`). Checks (`executeChecks`) become safe by construction once merge-ready is ref-safe; decide whether to also wrap checks (Open Questions).

## Migration Plan

No compatibility flag (active dev, no version-compat requirement). Land in dependency order, each step independently testable:

1. `WorkspaceManager`: add isolated landing-workspace create/dispose + prune (workspace.ts).
2. Rework `publishAction` to use a landing workspace; workspace is read-only. Update `publishOutput` facts.
3. Rework `runSquashMergePreflight` to use a landing workspace; structured output unchanged.
4. Add executor start/end branch-stability checks + `branch-invariant-violation` evidence (executor.ts).
5. Update failure rendering (CLI `DeliveryFailureGuidance`, web `delivery-failure.ts`) for the new kind.
6. Rewrite affected runner tests (`publish.spec.ts`, `push.spec.ts`, `issue-112-regression.spec.ts`); add new tests asserting no base-branch checkout in the workspace and correct landing-workspace sequencing.
7. Validate end-to-end against: prepare, merge-ready, publish, retry, and live workflow recovery. Confirm a single clean landing commit still lands on the remote base branch.

**Rollback:** revert steps 4→1. The branch-stability checks (step 4) are additive and revertible on their own; reverting steps 1-3 restores prior in-workspace landing behavior with no data migration, since the delivery outcome (one commit on base, pushed) is unchanged.

## Open Questions

- **Checks scope:** Should the start/end branch-stability check also wrap check-type work (`executeChecks`), or only tasks for now? Lean: tasks now (the high-risk actions are tasks), checks once merge-ready is ref-safe — confirm in build.
- **Preflight optimization:** Adopt `git merge-tree --write-tree` for a pure ref-only preflight now, or keep temp-workspace parity with publish and treat merge-tree as a later optimization? Lean: keep parity now, optimize later.
- **Evidence shape:** Record branch-stability evidence as a dedicated `WorkItemResult` field vs embedded in the result JSON (as `dirty-worktree` is). Lean: embed in JSON for consistency with existing evidence.
- **Landing workspace identity:** Key landing dirs by runId only (one concurrent landing per run) vs runId+uuid (allow retries to coexist). Lean: runId+uuid to avoid clobbering a concurrent retry's landing dir.
