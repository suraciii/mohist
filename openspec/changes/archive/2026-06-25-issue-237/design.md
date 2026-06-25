## Context

Prerequisite #190 ("基于 GitHub PR 的集成工作流") landed the `mohist/pr` workflow profile plus two runner actions: `mohist/create-pull-request` (push workflow branch + open/update PR) and `mohist/merge-pull-request` (squash merge + confirm `MERGED`). The current profile confines both actions to the integrate stage (`integrate:open-pr` → `integrate:merge-pr`), and `mergeOrConfirmPr` (`packages/runner/src/actions/publish-via-pr.ts:327`) calls `gh pr merge --squash` immediately after confirming the PR is `OPEN` — it never inspects GitHub PR checks. So:

- The whole run has no visible GitHub integration container until integrate's last moment.
- `gh pr merge` is GitHub's arbiter: pending checks get merged early, and failed checks still attempt a merge (relying on branch-protection to reject), producing noisy `protection-conflict` failures instead of a clean `pr-checks-failed`.

Issue #237 moves to a PR-first shape and makes merge checks-gated. Constraints fixed by the issue and `design/workflow/actions.md`:

- No new stage hooks, no hidden stage-boundary side effects, no workflow finalize task.
- PR checks stay an internal precondition of `merge-pull-request`, never a stage-level check.
- The workflow engine stays error-code-agnostic; only generic JSON-path matching is allowed for recovery.

Stakeholders: runner (action behavior), server (profile YAML + profile tests), web (delivery indicator regression only).

## Goals / Non-Goals

**Goals:**
- Restructure `mohist/pr` so PR creation runs as an explicit task right after plan approval and stage tails declare explicit update-PR tasks.
- Make `merge-pull-request` wait for GitHub PR checks before merging, with a clean `pr-checks-failed` failure path.
- Keep `base-moved` recovery working under the new shape, reusing the same branch/PR.
- Preserve the existing architecture: stage / tasks / checks / action-owned output / engine error-code-agnosticism.

**Non-Goals:**
- PR checks auto-fix (no recovery task for `pr-checks-failed` in this change).
- New manual-waiting / human-intervention workflow state.
- GitHub Actions/CI configuration, GitHub issue sync, remote head-branch deletion.
- Multiple PRs per issue; head-SHA locking; per-task auto-push inside a stage.

## Decisions

### D1: PR-first task graph expressed purely in profile YAML

The PR-first shape is achieved by editing `mohist-pr.workflow.yaml` only — no engine changes. Task placement:

- A `create-pull-request` task runs as the first task after plan approval (head of build, or a dedicated pre-build slot). It pushes the workflow branch and opens/reuses the PR, projecting identity via `setVars`.
- build and check stages append an explicit `create-pull-request` (update-PR) task at their tail when they need to sync working-branch state to GitHub. Re-running `create-pull-request` on the same head/base hits the existing "reuse open PR" path in `openOrUpdatePr` (`pull-request.ts:313`), so no new action capability is required for updates.
- integrate's happy-path delivery collapses to a single `merge-pull-request`; `integrate:open-pr` is removed from the happy path (it survives only inside the `base-moved` recovery branch).

**Rationale:** the issue explicitly forbids hidden side effects; the only mechanism that creates/updates a PR must be a visible task. Reusing the existing create action for updates avoids a separate `update-pull-request` action.

**Alternatives considered:**
- *A dedicated `mohist/update-pull-request` action.* Rejected: `create-pull-request` already idempotently creates-or-updates by head/base; a second action would duplicate that logic and split the `kind` field the web delivery indicator keys on.
- *An engine-level "PR carrier" that auto-pushes at stage boundaries.* Rejected: violates the no-hidden-side-effect constraint and `design/workflow/actions.md`.

### D2: Checks-gated merge implemented inside `mergeOrConfirmPr`

After the existing `OPEN`/`CLOSED`/`MERGED` state check and before `gh pr merge`, insert a checks-wait phase:

1. Poll `gh pr checks <prNumber> --json bucket,name,state` (gh's `bucket` field rolls per-check state into `PASS`/`FAIL`/`PENDING`/`SKIP`).
2. Classification:
   - any `PENDING` → sleep (fixed poll interval, e.g. 15s), re-poll; respect `context.signal` for cancellation.
   - no `PENDING` and any `FAIL` (covering gh `FAIL`/`CANCELLED`/`ACTION_REQUIRED` buckets) → return `pr-checks-failed`.
   - all `PASS`/`SKIP` (or no checks reported) → proceed to merge.
3. Each poll records a `PullRequestStep` so the output audit trail shows the wait, mirroring existing step recording.

This is an internal precondition of the action, matching the spec requirement "PR checks are not stage-level checks."

**Rationale:** keeping it inside the action preserves engine agnosticism and makes the wait observable via the existing `steps[]` output. Using `gh pr checks --json bucket` avoids the action parsing raw GraphQL status contexts.

**Alternatives considered:**
- *Let GitHub branch protection reject the merge and classify the resulting error.* Rejected: produces opaque `protection-conflict` failures, not the required `pr-checks-failed`, and merges early when protection is off.
- *Model checks as a stage-level check.* Rejected by the issue's explicit non-goal.
- *Use `gh pr checks --watch` (blocking).* Rejected: it blocks without respecting `context.signal` and has no max-wait; an explicit poll loop is testable with faked gh and cancellable.

**Open param:** max wait / timeout. Decision: no hard cap in v1 (mirror the issue's "keep waiting" semantics); cancellation is via the runner's existing task signal. Revisit if a run ever blocks indefinitely on a stuck pending check.

### D3: `pr-checks-failed` as action-owned output, no auto-fix

The checks failure returns the existing failure shape from `mergePullRequestAction` with `errorCode: "pr-checks-failed"`, plus `prNumber`, `prUrl`, `message`. `pr-checks-failed` is added to `PublishViaPrFailureKind`. The profile declares **no** `onFailure` case for `pr-checks-failed`, so the engine surfaces an ordinary task failure; the user fixes the cause and retries/reruns.

**Rationale:** the issue's non-goal is auto-fix; modeling it now would require engine/checks semantics the architecture forbids. The action-owned `errorCode` leaves the door open for a future profile-level recovery task without engine changes (per `actions.md`).

**Alternatives considered:**
- *A new manual-waiting workflow state.* Rejected by the issue's non-goals.

### D4: Identity projection unchanged, consumed earlier

`create-pull-request`'s existing `setVars: { github.pr.number: output.prNumber, github.pr.url: output.prUrl }` is already correct. The only change is that the consuming `merge-pull-request` task now reads `vars.github.pr.number` from a task that ran stages earlier, not minutes earlier in the same stage. No new variable or projection path is introduced; runtime-profile `vars.*` already persists across stages for the run.

**Risk:** long-lived `vars.github.pr.number` could go stale if a human reopens/closes the PR mid-run. `merge-pull-request` keeps its existing `resolvePrNumberForMerge` fallback that re-lists PRs by head/base when `prNumber` is absent, and the `base-moved` recovery re-runs `create-pull-request` which overwrites the same `vars.*` (allowed by the `setVars` rule that recovery may overwrite runtime facts).

### D5: `base-moved` recovery preserved verbatim

The existing `onFailure` case on `integrate:merge-pr` (`output.errorCode: base-moved` → `rebase -> create-pull-request -> merge-pull-request`) is retained. Because update-PR reuses the same workflow branch and the same open PR, recovery still satisfies "reuse same branch and PR." The only adjustment is that the recovery `create-pull-request` is now conceptually identical to the early/​tail update tasks (same action, same `setVars`), so no special-casing is needed.

## Risks / Trade-offs

- `[Indefinite wait on stuck pending check]` → No hard timeout in v1; runner task signal cancels. Mitigation: observable via `steps[]`; revisit a max-wait config if it bites in practice.
- `[PR recreated/updated by an external actor mid-run]` → `create-pull-request` reuses by head/base, and `merge-pull-request` confirms `state=MERGED` post-merge. A closed PR mid-run yields `pr-state-conflict` (existing behavior), surfaced as ordinary failure.
- [`gh pr checks` semantics vary across repos]` → Some repos have no checks (bucket list empty → proceed, matching "skipped" intent); some have required vs optional checks. v1 treats all reported checks equally; differentiating required/optional is deferred (see Open Questions).
- `[Web delivery indicator now sees PR tasks in build/check]` → Pure regression risk; the indicator keys on `uses`/`kind`, which are unchanged. Mitigation: add a regression test asserting the indicator renders for a build-stage `create-pull-request` task.
- `[Profile test churn]` → `MohistPrIssueWorkflowProfileSpecs` asserts integrate task order; these must be updated to the new graph. Low risk, mechanical.

## Migration Plan

1. Runner first: implement checks-wait + `pr-checks-failed` in `mergeOrConfirmPr`/`MergePullRequestOutput` with faked-gh tests (no behavior change for repos with no checks — they proceed, preserving current default-profile equivalence).
2. Profile: restructure `mohist-pr.workflow.yaml` to the PR-first graph; update `MohistPrIssueWorkflowProfileSpecs` and `MohistDefaultWorkflowProfileSpecs`.
3. Docs: flip `design/workflow/builtin-workflows.md`, `design/workflow/actions.md`, `docs/workflow-profiles.md` from "target state" to "current state."
4. Web: add the delivery-indicator regression test; no code change expected.

**Rollback:** the profile YAML and runner action are independent. Reverting the profile YAML restores the integrate-only PR shape while the hardened `merge-pull-request` still works (it just waits-then-merges within integrate). Reverting the runner action restores unconditional merge but re-exposes the early-merge/failed-checks behavior. No data/storage migration; no HTTP API change.

## Open Questions

- **Required vs optional checks:** should `merge-pull-request` distinguish GitHub required checks from optional ones (e.g. via `gh pr view --json mergeStateStatus` / `statusCheckRollup.requires`)? v1 treats all reported checks equally; defer until a real repo needs the distinction.
- **Poll interval / max wait:** is 15s poll, no cap, acceptable for the liveness quiet threshold, or do we need a configurable cap? Decide after observing a real pending CI run.
- **Update-PR task granularity:** should every stage tail get an update task, or only build (where the bulk of commits land)? Tentatively: build tail always; check tail only if check produces commits. Finalize during profile edit.
