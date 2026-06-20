## Context

Integrate lands a finished issue onto `master`. Today it uses **two** workspaces: a persistent workflow workspace (on the run branch) and a disposable `--shared` "landing" clone that `mohist/publish` checks `master` out into to run `merge --squash` + `push`. The landing clone's only consumers are `publish` and the check-stage `merge-ready` probe.

This duality is fragile. In issue #166, `mohist/prepare`'s rebase crashed mid-flight (runner-lost) inside the **shared workflow workspace**, leaving it in an unfinished rebase. Every later `git checkout runBranch` was refused ("resolve your current index first") and the issue died — the landing clone did nothing to protect the workspace from the prepare rebase that corrupted it.

Current state of the relevant code:
- `packages/runner/src/actions/rebase.ts` — `rebaseAction` (rebase onto **local** base + conflict-resolution loop) and `rebaseStatusAction`.
- `packages/runner/src/actions/registry.ts` — `prepareAction` (fetch remote base + rebase + conflict loop + dirty-tree cleanup) and `publishAction` (landing clone → `checkout -B target` → `merge --squash` → `commit` → `push`). These two rebase paths are near-duplicates.
- `packages/runner/src/runtime/workspace.ts` — `WorkspaceManager.{materialize,verify,createLandingWorkspace,disposeLandingWorkspace,pruneLandingWorkspaces}`.
- Dispatch: `WorkExecutor.verifyBoundWorkspace` calls `workspaceManager.verify` on **every** task dispatch (`executor.ts:112`); `workspaceManager.materialize` is called once at workflow start (`runner-signalr.ts:38`).
- `mohist-default.workflow.yaml` — integrate stage is `spec-sync → archive-change → prepare → publish`.

The run branch is already the single source of truth for *content*; landing is only a mechanism to get a `master` checkout. That mechanism can be replaced by on-workspace `rebase(squash)` + a ref-only fast-forward `push`. Constraints: the workspace must never leave its run branch (`workspace-branch-stability`); dispatch must never re-clone (`workspace-materialization`); delivery stays two visible tasks.

## Goals / Non-Goals

**Goals:**
- Collapse integrate delivery to a single workspace: `integrate:rebase` (remote + squash) → `integrate:push` (fast-forward), with no landing clone.
- Give the workspace a crash self-healing safety net (health gate) so removing landing does not reintroduce the #166 fatality.
- Unify the duplicate rebase implementations into one `mohist/rebase` action shared by manual rebase and integrate.
- Preserve the user-visible outcome: one squash commit on `master`, landed as a fast-forward.
- Keep delivery as two independently tracked, separately-retryable tasks.

**Non-Goals:**
- No change to plan/build/check execution mechanics.
- No "push run branch to origin for cross-machine persistence" — the health gate self-heals locally; remote run-branch persistence is a later hardening.
- No change to the squash commit message format (`Complete issue #N`).
- No change to the `git diff --check` health ordering vs push.
- No change to the public shape of `POST /{number}/rebase`; only the underlying action is unified.

## Decisions

### D1 — Run branch is the single source of truth; delete the landing clone
Landing exists only to give `publish` a `master` checkout. A fast-forward `push origin <runBranch>:<baseBranch>` needs no `master` checkout, and the squash can be built on the run branch directly (D3). Removing landing eliminates a redundant workspace, its prune/dispose lifecycle, and the `--shared` alternates reference-tracking in `isCacheReferencedByActiveWorkspace`.
- *Alternative:* keep landing but make it crash-safe. Rejected — #166 showed the shared workspace itself gets corrupted by the prepare rebase; landing does not protect against that, and doubling workspace state is the root fragility.

### D2 — Unify `prepare` + `rebase.ts` into one `mohist/rebase` action with `remote` + `squash` options
`prepareAction` and `rebaseAction` are the same abort→rebase→conflict-loop→abort flow, differing only in remote-fetch and (soon) squash. One action with options:
- `remote` set → `fetch <remote> <baseBranch>` then rebase onto `<remote>/<baseBranch>` (old prepare); unset → rebase local base (old rebase). Integrate sets `origin`; manual rebase may omit it.
- `squash` + `message` → after a successful rebase, fold N commits into 1 (D3).
The manual endpoint and `integrate:rebase` share this action with different parameters.
- *Alternative:* keep two actions that call a shared helper. Rejected — two registered actions with overlapping semantics is exactly the current confusion; one parameterized action is simpler for workflow authors and removes a code path.

### D3 — Squash via `git reset --soft <base>` + `git commit`, not `merge --squash`
After a successful rebase, the run branch HEAD already has the resolved final tree. `reset --soft <base>` (where `<base>` is the `origin/<baseBranch>` tip we just rebased onto) moves HEAD back to base while keeping index + work tree at the rebased tip; a single `commit` then produces one commit containing the entire issue diff. This **cannot conflict** because the tree is already the final resolved state — squash adds no new failure surface. This is why squash can move out of the landing clone and onto the workspace safely.
- *Alternative:* `merge --squash` in a landing clone (old publish). Rejected — requires a second branch context (landing) and can surface conflicts at squash time. `reset --soft` sidesteps both.

### D4 — `mohist/push` is a pure fast-forward ref push
`git push origin <source>:<target>` with `source = run branch`, `target = base branch`. No checkout, no working-tree mutation, no landing clone. It runs from the workspace dir but only updates a remote ref; the workspace stays on `workspace.branch`. A non-fast-forward rejection is reported as `base-moved` (→ re-rebase); anything else is `retry-safe`.
- *Alternative:* `push --force-with-lease`. Rejected — we want strict fast-forward so base movement is surfaced, not papered over.

### D5 — Workspace health gate at `verify` + `materialize` entry
Before handing the workspace to any task, probe for residual `rebase-merge` / `rebase-apply` / `MERGE_HEAD` / `CHERRY_PICK_HEAD`. If present: abort the in-progress op, `checkout <runBranch>`, `reset --hard <runBranch>` to align tree+index, leaving no conflict markers. Placement is `WorkspaceManager.verify` (every dispatch) and `WorkspaceManager.materialize` (workflow start) — the two documented entry points, matching `workspace-health-gate`.
- *Alternative:* detect-only and fail with "needs manual cleanup". Rejected — that **is** the #166 fatality.
- *Alternative:* re-clone on detection. Rejected — discards run-branch commits and violates the no-re-materialize-at-dispatch contract.

### D6 — The safety invariant: run branch ref immobility during rebase
`git rebase` only advances `<runBranch>` on success; while a rebase is in progress the ref stays at the pre-rebase commit. Therefore if the runner crashes mid-rebase, every commit already on `<runBranch>` is still reachable, and the health gate's `reset --hard <runBranch>` restores a known-good state **without losing committed agent work**. This is the theoretical foundation that makes D5 non-destructive and is what lets us delete landing without a persistence net.

### D7 — merge-ready preflight → `git merge-base --is-ancestor origin/<baseBranch> <runBranch>`
Replaces the landing-clone `merge --squash --no-commit` probe with a ref-only, working-tree-free check. The preflight now answers "is the run branch prepared against the latest base?" (yes → merge-ready; no → needs rebase) and no longer reports conflict files.
- *Alternative:* keep the squash-merge probe without landing, by checking out master in the workspace. Rejected — violates branch stability.
- *Trade-off:* conflict-file detail is lost from the preflight. Acceptable: the preflight is a readiness signal; authoritative conflict detection moves to the Integrate rebase (which already reports structured conflict evidence). This is the explicit scope trade in the proposal.

### D8 — Workflow YAML integrate stage: `spec-sync → archive-change → rebase(remote=origin, squash=true) → push`
Single rewrite of the integrate tasks. `lockBehavior: sequential` + `resources: [project-integration]` already serialize integrate, so the on-workspace rebase+push has no concurrency risk.

## Risks / Trade-offs

- [Health gate `reset --hard <runBranch>` could discard uncommitted work] → it fires **only** when residual rebase/merge state is detected, i.e. after a crash mid-operation where the uncommitted state already belongs to the aborted op. The rebase action also `commitPendingChanges` before rebasing, so legitimate agent work is committed before any rebase. Mitigation: document that the gate fires only on detected residual state; leave untracked files alone (dirty-worktree boundary checks handle those separately).
- [Preflight is weaker — no conflict file detail] → Mitigation: the Integrate rebase is authoritative and reports conflict files; merge-ready remains a cheap readiness gate. Base-movement between check and integrate is still caught by the push's non-fast-forward (`base-moved`).
- [Push now mutates the remote from the workspace's repo] → it only advances a ref via fast-forward and never touches the working tree; the workspace remains on `workspace.branch`. Non-FF → `base-moved` → re-rebase. No new concurrency surface beyond the existing sequential lock.
- [Squash loses individual commit history] → explicit non-goal; matches the existing `Complete issue #N` convention.
- [Removing landing changes publish failure semantics] → conflicts now surface only at rebase (never at push); push only fails `base-moved`/`retry-safe`. The failure-kind taxonomy is updated in `merge-delivery` accordingly.
- [High blast radius — every future issue lands via this path] → Mitigation: update the two regression tests (`merge-ready.spec.ts`, the prepare/publish tests) plus add focused tests for push, rebase-squash, and a simulated mid-rebase-crash health-gate fixture; plus acceptance criterion #8 (one full issue end-to-end).

## Migration Plan

Single-PR, in-repo change (Mohist is local-first; no external deploy or schema migration). Suggested ordering so the tree stays test-green at each step:

1. Extend `mohist/rebase` with `remote` + `squash`/`message` (absorb prepare's fetch + dirty-tree handling). Keep `prepare`/`publish` registered temporarily.
2. Add `mohist/push`; register it. Add `push.spec.ts` coverage.
3. Add the health gate to `WorkspaceManager.verify` + `materialize`; add a mid-rebase-crash fixture test.
4. Switch `merge-ready` preflight to `merge-base --is-ancestor`; update `merge-ready.spec.ts`.
5. Rewrite the integrate stage in `mohist-default.workflow.yaml` to `rebase → push`.
6. Delete `prepareAction`/`publishAction`, the landing methods + path helpers + landing calls in the registry, and the alternates-scan landing root; delete/replace `prepare.spec.ts`/`publish.spec.ts`; update `issue-112-regression`-style tests.
7. End-to-end: run one issue through the full workflow and confirm master receives a single squash commit via fast-forward (acceptance #8).

**Rollback:** revert the PR; the previous prepare/publish/landing model is restored from git history. There is no data migration — workspaces and run branches are git state, not schema. A workspace mid-flight during rollback may already have been rebased+squashed under the new model, but its run branch is still a valid single commit, so the old `publish`'s `merge --squash` would still land it correctly.

## Open Questions

- **Fetch inside `push`?** `rebase` already fetched `origin/<baseBranch>` and confirmed ancestry immediately before; an extra fetch in `push` would only race. Lean: no fetch in `push`; rely on the push's non-FF rejection as the authoritative `base-moved` signal.
- **Should the health gate `git clean -fd` untracked files?** Lean: no — only abort the residual op + `reset --hard <runBranch>` to align tracked state. Untracked files are governed by the existing dirty-worktree boundary checks; a destructive clean risks dropping agent artifacts.
- **Keep `rebaseAction`'s `commitPendingChanges` auto-commit?** Lean: yes, unchanged — it protects agent work before rebase and composes cleanly with the health gate.
