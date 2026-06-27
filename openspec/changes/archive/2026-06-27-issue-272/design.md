## Context

When a stage fails mid-flight — most commonly the `integrate` stage's `mohist/rebase` hitting a conflict — the runner leaves the workspace in a dirty git state: an in-progress rebase, uncommitted changes, or a detached HEAD. A subsequent `rerun` starts the next stage's tasks against that dirty workspace and the first git-touching task blows up, forcing the user to manually `git rebase --abort && git reset --hard` before the autonomous flow can resume.

Two existing mechanisms already touch this surface, neither of which solves the stage-boundary case:

- `WorkspaceManager.runHealthGate` / `reenterRunBranch` (`packages/runner/src/runtime/workspace.ts:217-262`) run as an **implicit** per-dispatch backstop. They are invisible to profiles, emit no structured diagnostics, and are not testable (they call `runCommand("git", …)` directly with no injectable seam). They fire on every dispatch, not specifically at stage boundaries, and their failures surface as generic exceptions.
- `enforceCleanWorktree` (`packages/runner/src/runtime/executor.ts:272-306`) runs **after** a task succeeds, verifying the worktree is clean. It cannot help when a *previous* stage left residue that the *next* stage's first task trips over.

What is missing is an **explicit, profile-visible, idempotent, diagnosable** prepare step at the stage boundary — analogous to `actions/checkout` in GitHub Actions. The proposal puts this in the runner as a new `mohist/workspace-prepare` action, registered as the first task of every stage in the `mohist/local` and `mohist/github-pr` profiles.

**Stakeholders:** developers relying on `rerun` to advance issues autonomously through multi-stage workflows.

**Constraints:**

- `WorkspaceManager.runHealthGate` / `reenterRunBranch` / `enforceCleanWorktree` MUST NOT change (proposal Non-Goal + acceptance criterion 9). The new action layers on top, it does not refactor the runtime.
- The action is runner-owned (runner owns the workspace lifecycle — see `design/architecture.md`); the server only supplies profile YAML.
- No network operations (fetch/pull/push), no workspace creation/clone — those belong to other actions/tasks.

## Goals / Non-Goals

**Goals:**

- Add a `mohist/workspace-prepare` runner action that reconciles a workspace's local git state toward the expected branch: abort residual rebase/merge/cherry-pick, checkout expected branch, `reset --hard HEAD`, `clean -fd`, then health-verify.
- Make the action idempotent with a sub-second fast-pass on an already-clean workspace.
- Emit structured failure diagnostics (`failureKind` + current HEAD + expected branch + residual-state probe + `git status --porcelain`) so `rerun` and the web UI can act on a fresh prepare attempt.
- Inject `mohist/workspace-prepare` as the first task of every stage in `mohist/local` and `mohist/github-pr`, executing exactly once per stage and never inside recovery sequences.
- Cover the action with fake-git-runner unit tests mirroring the `rebase.spec.ts` harness.

**Non-Goals:**

- Changing `runHealthGate` / `reenterRunBranch` / `enforceCleanWorktree` semantics (they remain the implicit backstop).
- Refactoring the residual-state probing in `WorkspaceManager.detectResidualState` into a shared helper — it stays private (see D2).
- Adding per-task workspace checks inside a stage (task-to-task cleanliness stays with `enforceCleanWorktree`).
- Remote sync, workspace creation/clone, recovery-task-aware continuation (separate issues per proposal).
- Adding a new `DeliveryFailureKind` to the web UI (see D4).

## Decisions

### D1. New standalone action `packages/runner/src/actions/workspace-prepare.ts`, registered in `createDefaultRegistry`

Create `workspace-prepare.ts` exporting `workspacePrepareAction: ActionHandler` plus a module-level `setWorkspacePrepareGitRunnerForTest` seam, and add `registry.register("mohist/workspace-prepare", workspacePrepareAction)` to `createDefaultRegistry()` (`packages/runner/src/actions/registry.ts:42-62`).

**Rationale:** Matches the established one-file-per-action layout (`rebase.ts`, `push.ts`, `github-pr.ts`). The action discovers its inputs the same way `push.ts:17,27` does — `stringAt(context.variables, ["workspace", "path"])` and `["workspace", "branch"]`, falling back to `context.workDir` — which the executor populates from `resolvedWorkspaceToVariables` (`executor.ts:377-386`). The injectable `git` seam mirrors every other git-touching action and is the only way to keep this unit-testable (unlike `WorkspaceManager`, which has no seam).

**Alternative considered:** Implement the prepare as a method on `WorkspaceManager` and have the executor call it at stage start. Rejected — it would (a) violate the "must not change WorkspaceManager" constraint, (b) hide the step from profiles (the proposal explicitly wants it profile-visible as the `pr-first-workflow` capability requires explicit stage-boundary side effects), and (c) inherit `WorkspaceManager`'s non-injectable `runCommand` calls, making diagnostics-only paths untestable.

### D2. Implement residual-state probing inside the action via the injectable `git` wrapper, do NOT extract a shared helper

The action implements its own four-way probe (`rebase-merge`, `rebase-apply`, `MERGE_HEAD`, `CHERRY_PICK_HEAD`) using `git rev-parse --git-path` (as `rebase.ts:386-395` already does for the rebase subset) plus `git status --porcelain` for the dirty-tree check. The equivalent logic in `WorkspaceManager.detectResidualState` (`workspace.ts:251-262`) is consulted for behavior parity but not imported — it is private, uses raw `exists()` filesystem checks, and is not injectable.

**Rationale:** The probe is small (a handful of `git` calls), must be diagnose-producing (it feeds the failure output), and must run through the injectable `git` seam for testing. Importing `WorkspaceManager`'s private method would require a visibility/refactor change that crosses the "must not change" constraint, and `rebase.ts`'s exported `abortRebaseIfInProgressAction` covers only the rebase subset (no merge/cherry-pick). Keeping the probe local to the action preserves the single-action-file invariant and the test seam.

**Alternative considered:** Extract a shared `detectResidualState` + `abortResidualOperations` module imported by both `workspace-prepare.ts` and (opportunistically) `WorkspaceManager`. Rejected for this issue — it widens the blast radius into the runtime layer the proposal explicitly fences off, and `WorkspaceManager` cannot consume it without becoming injectable first. File a follow-up if a third consumer appears.

### D3. Step ordering follows the spec scenarios exactly: abort → checkout → reset+clean → health-verify

The action executes, in order:

1. Abort residual rebase (`git rebase --abort`) if `rebase-merge`/`rebase-apply` present; then merge (`git merge --abort`) if `MERGE_HEAD`; then cherry-pick (`git cherry-pick --abort`) if `CHERRY_PICK_HEAD`. Re-probe after each abort so a later step never runs while an earlier operation is still in progress (matches spec scenario "Abort residual rebase … perform no other recovery step until no rebase is in progress").
2. If HEAD is not on the expected branch, `git checkout <expected branch>`.
3. `git reset --hard HEAD`, then `git clean -fd` (new operation — no current call site in the repo does `git clean`).
4. Health verification: HEAD on expected branch, working tree clean (`git status --porcelain` empty), neither `.git/rebase-merge` nor `.git/rebase-apply` exists. Succeed only if all three hold.

**Rationale:** This is the only ordering that satisfies every spec scenario simultaneously — aborting before checkout avoids `checkout` refusing to switch with a rebase in progress; resetting before verifying gives the verify step a stable input. It also mirrors the sequence `WorkspaceManager.reenterRunBranch` already proved works (`workspace.ts:217-228`), extended with the `clean -fd` and the explicit verify the spec mandates.

**Alternative considered:** Run `reset --hard` unconditionally (even on a clean tree) to keep the path branch-free. Rejected — it breaks the fast-pass requirement (spec: "< 1s, no side effects on a clean workspace") since `reset`/`clean` always mutate mtimes/index. The fast-pass short-circuits after the initial probe if everything is already clean.

### D4. Reuse the existing `workspace-setup` failure kind; do NOT add a new `DeliveryFailureKind`

On failure the action emits output JSON with `kind: "workspace-prepare"`, `failureKind: "workspace-setup"` (matching `delivery-failure.ts:67-72`, which already covers "runner could not prepare the workflow workspace"), plus the diagnostic fields the spec mandates: failing step name, expected branch, current HEAD (hash + ref, or `(detached)`), and the residual-state probe results.

**Rationale:** The web UI's `DeliveryFailureKind` union (`packages/web/src/shared/lib/delivery-failure.ts:1-9`) is a closed 8-value taxonomy; adding a kind touches the union, `DELIVERY_FAILURE_KINDS`, `DELIVERY_FAILURE_GUIDANCE`, `isDeliveryFailureKind`, and the `KIND_IN_MESSAGE` regex. `workspace-setup` already labels exactly this failure class (workspace preparation failed) and is `retryable: false`, which is correct — a workspace-prepare failure needs a fresh prepare, not an automatic retry of the same step. The `extractFailureKindCandidate` extractor (`delivery-failure.ts:159-207`) reads `failureKind` first, so no UI change is needed. Use `failureKind` (not `errorCode`) to match `push.ts`'s convention and the extractor's first-checked field.

**Alternative considered:** Introduce a `workspace-prepare` kind for finer-grained UI guidance. Rejected — it duplicates `workspace-setup`'s meaning and guidance text, and the proposal explicitly lists "reuse existing `workspace-setup` / `retry-safe`" as the web-side option. The `kind: "workspace-prepare"` discriminator on the action output already gives component-level identity without expanding the taxonomy.

### D5. Inject as an explicit first task in each stage's `tasks:` list — no `init`/`preTasks` mechanism

Edit `packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/mohist-local.workflow.yaml` and `mohist-github-pr.workflow.yaml` to prepend a `workspace-prepare` task to the `tasks:` array of all four stages (`plan`, `build`, `check`, `integrate`) in both files — 8 insertion points.

**Rationale:** The `pr-first-workflow` capability requires that PR-affecting side effects appear explicitly in the task graph, not via hidden stage-boundary hooks. An explicit first task honors that constraint; a profile-level `init`/`preTasks` field would reintroduce the "hidden stage-boundary side effect" the capability forbids. The spec's "execute exactly once at stage initialization, not between tasks" requirement is satisfied structurally — because the task is literally first in the list, the executor's existing per-stage task iteration runs it once and recovery-sequence injection (`onFailure` / check repair) happens *after* a business task fails, so recovery tasks are never preceded by a fresh prepare (spec scenario "Recovery sequences are not preceded by a fresh prepare").

**Alternative considered:** Add a `preTasks`/`init` array to the profile schema auto-prepended by the parser. Rejected — it violates the explicit-side-effect constraint and adds schema/parser surface area for a single one-task use case.

### D6. Output and task id conventions

Task id `workspace-prepare` (bare name, matching `proposal`/`specs` style; no `stage:` prefix since it is not stage-specific). Title `Prepare workspace`. Output JSON shape:

```jsonc
{
  "kind": "workspace-prepare",
  "status": "success" | "failure",
  "failureKind": "workspace-setup" | null,
  "expectedBranch": "mohist/run-...",
  "head": { "commit": "<sha>", "ref": "<name> | (detached)" },
  "residual": { "rebaseMerge": false, "rebaseApply": false, "mergeHead": false, "cherryPickHead": false },
  "porcelain": "",
  "step": "<failing step, on failure>"
}
```

**Rationale:** `failureKind`/`status`/`kind` align with `push.ts`/`rebase.ts` output contracts and the web extractor; the diagnostic block captures exactly what the spec's failure scenarios require. Emitting `step`, `head`, `residual`, and `porcelain` on failure (and a compact subset on success) gives `rerun` and the user enough to diagnose without re-running git by hand.

## Risks / Trade-offs

- **[`git reset --hard` + `git clean -fd` discard uncommitted work]** -> This is intentional and required by the spec, but a user who manually edited files in the workspace between stages will lose them. Mitigated by: (a) running only at stage start where, by contract, no in-flight task work exists; (b) the action is profile-visible (not hidden), so the data-loss surface is explicit in the task graph; (c) `enforceCleanWorktree` already enforces post-task cleanliness, so uncommitted work surviving across the stage boundary is already an anomalous state. Document in the task title/body that it discards uncommitted changes.
- **[Three layers now touch residual git state (runHealthGate, workspace-prepare, enforceCleanWorktree)]** -> Overlap is deliberate defense-in-depth, but a future maintainer may be confused about which layer owns what. Mitigated by: this design doc, the unchanged `runHealthGate` remaining the implicit per-dispatch backstop, and the in-code comments on the new action stating it is the explicit stage-boundary layer. The three layers differ in *when* and *visibility*, not in mechanism — that is the point.
- **[`git clean -fd` is a new operation with no existing call site]** -> No precedent to inherit behavior/test patterns from; risk of `-fd` flags being too aggressive (e.g. removing `.env`/untracked artifacts the user wanted) or too weak. Mitigated by: `-fd` matches the spec verbatim and mirrors GitHub Actions `actions/checkout` post-clean behavior; untracked files in a workflow workspace are by definition not source-controlled and thus not durable work. If a profile needs to preserve untracked files, that is a separate `clean` policy decision outside this issue.
- **[`workflow-profile.spec.ts` line 26 asserts `not.toContain("mohist/prepare")`]** -> The substring `"mohist/prepare"` is NOT contained in `"mohist/workspace-prepare"` (the character after `mohist/` is `w`, not `p`), so the existing assertion still passes. No test edit required for that line; the test does need a positive assertion that `workspace-prepare` now precedes the integrate business tasks, to lock in the D5 ordering.
- **[Profile YAML changes are not unit-tested on the server side beyond parsing]** -> The runner-side `workflow-profile.spec.ts` reads the file from disk and asserts substrings; a server-side parse test exists via `MohistWorkflow.cs`. Mitigated by extending the runner-side test with a "first task is workspace-prepare in every stage" assertion per profile.
- **[Action has no real-git integration test]** -> Consistent with every other action in the repo (all use the fake-git seam); real-git workspace behavior is exercised by `workspace.spec.ts` at the runtime layer. The new action's correctness hinges on git CLI semantics, which are stable; the fake-git tests assert the exact command sequence and the diagnostic output shape.

## Migration Plan

**Deploy:** No schema, API, or config migration. Ships in the next runner + server build.

- Runner: new `workspace-prepare.ts` + `registry.ts` registration (additive).
- Server: two workflow YAML edits (prepend one task to each of 8 `tasks:` lists).

**Activation:** Immediate on next workflow run; no flag, no opt-in. Existing in-flight workflows pick up the new task on their next stage transition (a stage's task list is resolved when the stage begins).

**Rollback:** Revert the three file changes. Because `workspace-prepare` is purely the first task in each stage, removing it returns the profiles to their prior task order with no state to clean up — the implicit `runHealthGate` backstop continues to handle the dirty-workspace case as it does today (less diagnosable, but functional). No cross-component rollback coupling.

## Open Questions

- Should the action's `git clean` exclude a configurable ignore list (e.g. preserve `.env` / credential helper files dropped into the workspace by the runner)? Current design uses bare `-fd` per the spec; a `-e` allowlist is a policy decision deferred to a follow-up if real workflows lose files they need.
- Should `failureKind` be `retry-safe` rather than `workspace-setup` for the subset of failures that are plausibly transient (e.g. a `git checkout` that lost a race with a concurrent process)? Current design uses `workspace-setup` uniformly because every failure here indicates the workspace is in a state a blind retry will not fix — a fresh prepare is always required, which `retryable: false` correctly encodes. Confirm during implementation whether any step's failure is genuinely transient.
- The fast-pass target is "< 1s". Confirm whether the initial probe (4 `rev-parse`/status calls) meets that budget on a cold disk; if not, collapse the residual probes into a single `git status`-derived check. Validate with a benchmark during implementation.
