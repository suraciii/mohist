## Context

Today the entire Integrate delivery — prepare the branch, resolve conflicts, land the change, (notionally) publish it — happens inside one opaque runner task `integrate:merge`, implemented by `mergeAction` in `packages/runner/src/actions/registry.ts`. That action inlines an agent conflict-resolution retry loop (`resolveMergeConflict`) and the squash commit, so the expensive reconciliation work is invisible and un-retryable as its own unit, and any failure is one opaque error. The default workflow (`packages/server/.../mohist-default.workflow.yaml`) declares it as a single `integrate:merge` task using `mohist/merge`.

Relevant current state, verified in code:

- **The server treats Integrate tasks generically.** A grep of `packages/server/src` shows `integrate:merge` appears only in the workflow yaml — there is no server-side branching on the merge task identity. Delivery metadata/freeze (workflow-run REQ-WR-005) is carried as ordinary task-result output, not special-case code. This means the split is primarily a runner + workflow-yaml change, not a server-domain change.
- **Worktree topology is fixed.** `workspace.ts` creates the issue workspace via `git worktree add -b mo/issue-N <worktree> <baseBranch>` off `project.path`. Linked worktrees **share the main repo's refs and object store**, and `mo/issue-N` is checked out in the worktree (so it cannot be checked out again in `project.path`). The existing `mohist/rebase` action rebases `mo/issue-N` (HEAD) inside that worktree.
- **No remote push exists today.** The current `mergeAction` only lands locally on the base branch; `git push` does not appear in the runner or server. So the publish task's push is genuinely new delivery behavior.
- **`post-merge-health-failed` is spec-only.** It is referenced in the workflow-run spec but by no `.cs`/`.ts` code, so renaming it is safe.
- **Tests use injectable runners** (`setMergeGitRunnerForTest`, `setMergeConflictResolverForTest`, and the `setRebase*` equivalents), asserting exact `git` call sequences and parsed `output` JSON.

Constraints: the project is in active development with no version-compatibility requirement; the `integrate` stage already runs under `lockBehavior: sequential` with the `project-integration` resource, so deliveries are already serialized per project; the user-visible outcome (one commit on the base branch, pushed) must not change.

## Goals / Non-Goals

**Goals:**
- Replace the single `integrate:merge` task with two real, ordered, independently tracked tasks: `integrate:prepare` then `integrate:publish`.
- Make conflict resolution a first-class, visible concern that lives only in `integrate:prepare` (reusing the proven rebase + agent conflict-resolution machinery), with its own attempts/evidence.
- Make `integrate:publish` land the prepared change as one commit on the base branch and push it to the remote, staying cheap under base-branch contention (no conflict-resolution loop).
- Classify delivery failures into actionable kinds (`retry-safe`, `base-moved`, `conflict`) carried in the existing structured task-result output.
- Guarantee a failed delivery attempt leaves a clean workspace (no in-progress rebase/merge, no partial commit).
- Keep the change localized to the runner actions, the default workflow yaml, and the openspec deltas (already written).

**Non-Goals:**
- No change to what lands: an issue's changes still arrive on the base branch as exactly one commit, pushed to the remote.
- No new user-visible configuration or workflow-profile schema; task ids/`uses` names are internal.
- No new realtime/transport layer for failures — the kinds ride the existing task-result/log/evidence channel.
- No change to the Check-stage `merge-ready` squash-mergeability preflight (its meaning is unchanged).
- No general refactor of unrelated runner actions.

## Decisions

### Decision 1: Split `mergeAction` into two actions, `prepareAction` + `publishAction`

Replace the single `mohist/merge` registration with `mohist/prepare` and `mohist/publish` in `createDefaultRegistry()`. Each returns its own `ActionResult` with a distinct `kind` (`prepare` / `publish`), so each becomes a genuine task-list row with independent status, attempts, and evidence.

- **Alternative considered:** keep one action that emits two sub-records. **Rejected** — it re-hides conflict resolution inside one task and directly contradicts the "truthful task list" goal.

### Decision 2: Prepare runs in the issue worktree; publish runs in the project repo

- `integrate:prepare` operates on `context.workDir` (the issue worktree, where `mo/issue-N` is HEAD) — it rebases `mo/issue-N` onto the base branch. This is exactly the environment `mohist/rebase` already targets, so no checkout conflict.
- `integrate:publish` operates on `project.path` (the main repo, where the base branch is default and the remote is configured) — it checks out the base branch, squash-lands `mo/issue-N`, commits, and pushes. This mirrors where `mergeAction` runs today (`mergeWorkDir = project.path`).
- The rebased `mo/issue-N` ref is visible to publish because linked worktrees share the main repo's refs (confirmed via `git worktree add` in `workspace.ts`).

- **Alternative considered:** run both in `project.path`. **Rejected** — `mo/issue-N` is checked out in the worktree, so it cannot be checked out again in the main repo.
- **Alternative considered:** run both in the worktree. **Rejected** — publish must land on/push the base branch, which belongs in the main repo.

### Decision 3: Prepare reuses `rebase.ts` machinery; publish does cheap squash + push with no conflict loop

- `prepareAction` reuses the existing rebase + conflict-resolution building blocks from `packages/runner/src/actions/rebase.ts` (`runConflictResolver`, `verifyRebaseComplete`, `abortRebaseIfInProgress`, `commitPendingChanges`) so conflict resolution is the task's explicit, visible purpose rather than reinvented code. On unresolvable conflict it reports a `conflict` failure kind.
- `publishAction` performs only cheap git: fetch-less local land via `git merge --squash <issue>` → single `git commit` → `git push origin <base>`. It contains **no** agent conflict-resolution loop. If the squash conflicts (base moved) or the push is rejected as non-fast-forward, it aborts/resets and reports `base-moved` (re-prepare needed).

- **Alternative considered:** have publish re-run a prepare-like rebase on contention. **Rejected** — the issue explicitly forbids silently repeating expensive conflict-resolution work in a loop.

### Decision 4: Prepare fetches the latest base; publish is the single push owner

- To honor "up to date with the **latest** base branch", `prepareAction` first runs `git fetch origin <base>` and rebases onto the fetched base, recording `preparedBaseSha` + the prepared candidate head in its output.
- `publishAction` is the only task that runs `git push`; prepare never pushes. This is the "single owner for pushing to the remote."

### Decision 5: Failure kinds are a structured `failureKind` field in the action `output`

Each delivery action's JSON `output` carries a `failureKind` when it fails, drawn from a small closed set that maps 1:1 to the issue's actionable kinds:

| `failureKind` | Produced by | Meaning / next action |
|---|---|---|
| `conflict` | prepare | Conflicts could not be resolved → needs attention |
| `base-moved` | publish | Base moved after prepare (squash conflict or non-fast-forward push) → prepare again, then publish |
| `retry-safe` | either | Transient failure unrelated to base movement/conflict → just retry |

The kind rides the existing task-result `output` channel that already flows to logs, API, CLI, and Web; CLI/Web rendering maps the kind to the recommended action. No new transport or domain event is introduced.

- **Alternative considered:** a first-class failure-type enum in the WorkflowRun domain model. **Rejected** — over-engineering for this scope; the generic structured-output channel already reaches every surface.

### Decision 6: Failed delivery always leaves a clean workspace

- `prepareAction` calls `git rebase --abort` on any failure (already the behavior of the reused rebase path), so no rebase stays in progress and no markers remain.
- `publishAction` calls `git merge --abort` / resets the base branch to its pre-publish sha on conflict, and never commits partial state.

### Decision 7: Rename the post-health failure reason to `post-publish-health-failed`

The delivery freeze point moves from merge to publish, so the workflow-run failure reason is renamed `post-merge-health-failed` → `post-publish-health-failed`. It is currently spec-only (no code references it), so the rename is safe and keeps the model intelligible.

### Workflow yaml change

The Integrate `tasks` block replaces the `integrate:merge` entry with:

```yaml
- id: integrate:prepare
  title: Prepare branch
  uses: mohist/prepare
  with:
    baseBranch: ${{ project.baseBranch }}
    conflictResolver:
      title: Resolve prepare conflicts
      with:
        description: "Resolve rebase conflicts, stage resolved files, and continue until the rebase completes."
- id: integrate:publish
  title: Publish changes
  uses: mohist/publish
  with:
    source: mo/issue-${{ issue.number }}
    target: ${{ project.baseBranch }}
    message: Complete issue #${{ issue.number }}
```

The `health` check, `lockBehavior: sequential`, and the `project-integration` resource are unchanged, and `integrate:spec-sync` → `integrate:archive-change` ordering stays ahead of delivery.

## Risks / Trade-offs

- **[Shared-ref assumption between worktree and project repo]** → Mitigation: confirmed linked worktrees share refs (`git worktree add`); add a test asserting publish sees the `mo/issue-N` ref that prepare rebased.
- **[Publish push rejected because the remote base moved]** → Mitigation: publish never force-pushes; a non-fast-forward push is classified `base-moved` with re-prepare guidance, leaving base untouched.
- **[Failure kind not actually rendered in CLI/Web]** → Mitigation: `failureKind` is in the same `output` payload surfaces already render; add minimal mapping in the task-failure rendering paths and a test asserting the field is present.
- **[Two tasks roughly double the task-granularity for delivery]** → Trade-off accepted: this is the intended visibility win; the sequential lock already makes the two-task sequence no more contention-prone than before.
- **[Network in delivery is new (fetch + push)]** → Mitigation: fetch is best-effort for "latest base"; push failures are classified and recoverable. Keep both within the existing sequential Integrate lock.

## Migration Plan

1. Ship the runner change (new `mohist/prepare` + `mohist/publish`; remove `mohist/merge`) and the updated `mohist-default.workflow.yaml`.
2. Replace `packages/runner/tests/merge.spec.ts` with `prepare.spec.ts` and `publish.spec.ts`, reusing the injectable git/resolver test harness and asserting the new `output` shape (including `failureKind`).
3. Apply the openspec deltas (already authored: `merge-delivery` added, `workflow-definition` + `workflow-run` + `web-ui` modified) and archive the change once Integrate passes.
4. **In-flight runs:** a run already inside `integrate:merge` finishes on the old code path; runs that (re)enter Integrate after deploy use prepare → publish. Because the server binds tasks generically from the yaml, no data migration is needed.
5. **Rollback:** revert the runner + yaml; `integrate:merge` is restored. No persisted state references the new task ids outside the run that produced them.

### Spec-sync timing

The delta files at `openspec/changes/issue-141/specs/{workflow-definition,workflow-run,web-ui}/spec.md` are the source of truth for what the change means; the corresponding files at `openspec/specs/{workflow-definition,workflow-run,web-ui}/spec.md` are the *post-merge* form. The change ships the deltas; the workflow-run service's `integrate:spec-sync` step is what applies them into main specs. Until that step runs for this issue, the main spec files are intentionally out of date relative to the shipped code. The default-yaml shape and the `mohist/prepare` + `mohist/publish` action registrations are the runtime contract regardless of when `openspec sync` runs.

### Failure-kind cheat sheet (canonical mapping)

| `failureKind` | Produced by | Reason | Next action |
|---|---|---|---|
| `conflict` | prepare | The agent conflict resolver exhausted its attempts or the resolver was not configured. | Inspect the conflicting files, resolve them on the issue branch, and rerun prepare. |
| `base-moved` | publish | The base branch moved between prepare and publish (squash conflict or non-fast-forward push). | Re-run prepare against the new base, then re-run publish. |
| `retry-safe` | either | A transient git/network error unrelated to conflicts or base movement. | Retry the same task without re-preparing. |

This mapping is the source of truth for both CLI (`DeliveryFailureGuidance.cs`) and Web (`delivery-failure.ts`); both surfaces must keep these labels and next-action strings in sync.

### Race between `git fetch` and `git rebase`

`prepareAction` runs `git fetch origin <base>` and then `git rebase origin/<base>` in two separate git invocations. The remote base branch can move between those two calls; the rebase would target an older SHA than the just-fetched one. The race window is small and self-healing: a subsequent prepare will re-fetch, and a `base-moved` publish failure is the recovery path if the race crosses the prepare/publish boundary. Treat the race as acceptable under the sequential `project-integration` lock (only one delivery in flight per project).

## Open Questions

- Should `publishAction` retry the cheap squash once before reporting `base-moved`, or report on first conflict? **Resolved:** report immediately (one attempt). Honest failure, explicit re-prepare. The issue's "publish must not silently repeat expensive conflict-resolution work in a loop" precludes silent retries. Future improvement could be a manual retry with explicit `force` flag, but out of scope here.
- Which remote does publish push to in `project.path`? **Resolved:** `origin` is the default. The action accepts a `remote` input so a project can override.
- Does prepare need to update the issue worktree's base view for any later stage, or is rebase alone sufficient? **Resolved:** rebase alone is sufficient. The `mo/issue-N` ref is shared between the issue worktree and the main repo (linked worktree), so publish sees the rebased head via the shared refs store.
