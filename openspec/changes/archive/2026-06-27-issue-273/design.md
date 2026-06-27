## Context

The workflow profiles' integrate-stage recovery is self-defeating in four independent ways, each forcing manual workspace cleanup to recover. All four live in two surfaces: the server-side profile YAML (`packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/mohist-github-pr.workflow.yaml`) and the runner actions (`packages/runner/src/actions/push.ts`, `packages/runner/src/actions/openspec.ts`).

Current state of each defect:

1. **Rebase conflict handler `retrySelf` loop** (`mohist-github-pr.workflow.yaml:286-293`). The `recover:resolve-rebase-conflicts` agent task is declared under `recover:rebase.onFailure` with `retrySelf: true` at the handler level. The agent prompt already loops to completion and runs `git rebase --continue`, so when the agent finishes the rebase is *done*. `retrySelf: true` then re-runs `recover:rebase`, whose `abortRebaseIfInProgress` aborts the in-progress (now actually completed-by-agent) rebase, destroys the resolved tree, and re-applies the rebase onto the same moved base — hitting the identical conflict. This repeats until the `budget: 2` recovery budget is exhausted, leaving the run permanently failed.

2. **Post-rebase recovery push uses `--force-with-lease` on a tracking-ref-less branch** (`mohist-github-pr.workflow.yaml:294-301`; `packages/runner/src/actions/push.ts:46-60`). The dynamic workflow branch `mohist/run-<runId>` has no configured remote-tracking ref. `push.ts` works around this by probing the remote tip via `ls-remote` and using the explicit `--force-with-lease=<target>:<sha>` form; but when `resolveRemoteTip` returns `null` (probe failure) it falls back to the bare `--force-with-lease` form, which git rejects with "(stale info)". `looksLikeNonFastForward` (`push.ts:114-121`) deliberately matches "stale info" as `base-moved`, so the failure is misclassified and triggers an entire needless rebase recovery — re-entering defect #1.

3. **Dead `conflictMode: task` config** (`mohist-github-pr.workflow.yaml:280`). The rebase `with` block declares `conflictMode: task`, but the rebase action never reads this field. It is leftover from a removed feature that regressed.

4. **`archive-change` names its archive directory with a UTC date prefix** (`packages/runner/src/actions/openspec.ts:221`). `archivePrefix = new Date().toISOString().slice(0, 10) + "-" + sourceName` yields e.g. `2026-06-26-issue-273`. The action's idempotency check `findExistingArchive` (`openspec.ts:418-426`) looks up `archivePrefix` — but on a cross-day retry the prefix recomputes to `2026-06-27-issue-273`, the lookup misses, the source directory is already gone (moved on day 1), and the action fails with `missing-source` permanently.

Defects #1–#3 are configuration/YAML plus a small push-action input addition. Defect #4 requires a new runner capability: an action must be able to persist a workflow runtime variable *during* execution (before the directory move), so a retry observes the persisted name. Today the only variable-write path is `WorkExecutor.applySetVars()` (`packages/runner/src/runtime/executor.ts:539-559`), which fires strictly *after* a task returns `completed` — too late for an action that fails mid-flight after an irreversible side effect.

Stakeholders: the `mohist/github-pr` profile (primary) and `mohist/default` profile (archive-change only). No external API consumers; all changes are internal recovery/action behavior.

## Goals / Non-Goals

**Goals:**
- Break the rebase-conflict recovery loop so a resolved rebase is preserved and the flow continues to `recover:push` then the merge retry.
- Make the post-rebase recovery push succeed on single-owner dynamic branches without being misclassified as `base-moved`.
- Remove the dead `conflictMode` declaration.
- Make `archive-change` idempotent across retries and reruns, including across a day boundary.
- Add a runner action capability to persist a workflow runtime variable mid-execution, surviving subsequent task failure.
- Keep the check-stage regular push on the safer `--force-with-lease` (it does no rebase rewriting).

**Non-Goals (from issue):**
- Workspace cleanup (issue #272).
- Continuation-aware rebase action rewrite (unnecessary once `retrySelf` is removed).
- Folding push into the rebase action (keep tasks separated).
- Letting the conflict-resolution agent perform the push itself (agent only resolves conflicts + completes the rebase).

## Decisions

### Decision 1 — Remove `retrySelf: true` from the rebase conflict handler

Remove the handler-level `retrySelf: true` at `mohist-github-pr.workflow.yaml:293` (the `when: conflict` handler under `recover:rebase.recovery`). After the `recover:resolve-rebase-conflicts` agent task completes, the workflow continues directly to the next sibling recovery task (`recover:push`) and then to the `retrySelf` of the parent `base-moved` handler (`merge-pr`), which is the correct re-attempt point.

The flow becomes: `recover:rebase` → conflict → `recover:resolve-rebase-conflicts` (no retry) → `recover:push` → retry `merge-pr`. The agent's completed rebase is never aborted.

**Alternatives considered:**
- *Make the rebase action continuation-aware* (detect that a rebase was already completed by the agent and skip `abortRebaseIfInProgress`). Rejected — it's listed as a non-goal, and it's strictly more complex than removing one flag. Once `retrySelf` is gone, `recover:rebase` never re-runs after the agent, so there is nothing to make continuation-aware.
- *Move the conflict-resolution task out of `recover:rebase.onFailure` to a top-level recovery task.* Rejected — it would lose the precise "rebase failed with conflict" scoping and complicate the recovery ordering.

### Decision 2 — Add a `force: true` input mode to the `mohist/push` action

Add a boolean `force` input to `push.ts` (alongside the existing `forceWithLease`). When `force === true`, emit a bare `--force` and skip the `ls-remote` lease probe entirely. Switch only the `recover:push` task under the `base-moved` handler (`mohist-github-pr.workflow.yaml:294-301`) from `forceWithLease: true` to `force: true`. Leave every other push (check stage, integrate stage, `pr-checks-failed` recovery) on `forceWithLease: true`.

Rationale: the `mohist/run-<runId>` branch is single-owner — exactly one workflow run pushes to it, so there is no "someone else overwrote my push" risk that `--force-with-lease` guards against. `--force` removes the tracking-ref dependency that causes the stale-lease failure and the resulting `base-moved` misclassification.

`force` and `forceWithLease` are mutually exclusive in effect; if both are set, `force` wins (it is the more explicit, narrower instruction for the post-rebase case). Implement as an early branch in the push-args construction (`push.ts:45-60`): if `force`, `pushArgs.push("--force")` and return; else run the existing `forceWithLease` logic.

**Alternatives considered:**
- *Harden the `ls-remote` lease workaround so the bare fallback never triggers.* Rejected — even with a perfect probe, `--force-with-lease=<target>:<sha>` adds a round-trip and still fails if the remote tip changed for any reason (e.g. a prior partial push), re-entering the misclassification. The lease provides no safety on a single-owner branch, only failure modes.
- *Auto-detect dynamic `mohist/run-*` branches and force them implicitly.* Rejected — implicit behavior based on branch-name conventions is fragile and surprising; an explicit `force: true` input keeps the safety posture visible in the profile and preserves `--force-with-lease` semantics everywhere else.
- *Use `--force-with-lease` without the bare fallback (treat probe failure as fatal).* Rejected — it converts a recoverable push into a hard failure and does not solve the core misclassification.

### Decision 3 — Remove the dead `conflictMode: task` declaration

Delete `conflictMode: task` from `mohist-github-pr.workflow.yaml:280` (inside `recover:rebase.with`). The rebase action ignores this field; it delegates conflicts to a task by default. No code change required — this is pure dead-config removal. (Confirmed `mohist-default.workflow.yaml` has no `conflictMode` to sync.)

### Decision 4 — Persist the archive directory name to a workflow runtime variable before the move

Modify `archiveChangeAction` (`openspec.ts:215-342`) so the archive directory name is stable across retries:

1. At the top of the action, check `context.variables` for a previously persisted name under a reserved internal key (see Decision 5).
2. If present → use it as `archivePrefix`, skipping the date computation.
3. If absent → compute `archivePrefix` from the date as today, then **persist it to the server immediately** via the mid-execution variable write *before* calling `moveChangeDir` / `uniqueDestination`.
4. Proceed with the existing `findExistingArchive` → `uniqueDestination` → `moveChangeDir` flow.

Because the write is persisted server-side before the (irreversible) directory rename, a retry or rerun — even after the move succeeded but a later git step (add/rm/commit) failed, or across a day boundary — observes the same name, `findExistingArchive` locates the already-moved directory, and the action reuses it instead of failing with `missing-source`.

The variable key is scoped per change so concurrent archive actions (if ever introduced) do not collide; for the single-archive-per-run reality a single namespaced key suffices, but keying on `sourceName` keeps it forward-compatible.

**Alternatives considered:**
- *Drop the date prefix entirely; name the archive directory by `sourceName` alone.* Rejected — the date prefix gives chronological ordering in the archive directory and disambiguates a re-archival of a same-named change. The idempotency problem is the *instability* of the name across retries, not the presence of a date.
- *Write a sidecar marker file (e.g. `.archive-target`) into the archive parent before the move, and read it on retry.* Rejected — the worktree can be reset/abandoned between attempts, taking the marker with it; a server-side runtime variable is durable across worktree resets and is observable by a fresh runner dispatch.
- *Broaden `findExistingArchive` to match any `*-<sourceName>` directory ignoring the date.* Rejected — it would silently reuse the wrong archive on re-runs of legitimately distinct changes that happen to share a source name, and it hides the real instability rather than fixing it.
- *Make the move + git commit atomic and reversible.* Rejected — far larger change; the variable approach makes the *name* deterministic, which is the actual root cause.

### Decision 5 — Mid-execution variable write via an `ActionContext` helper

Add a helper to `ActionContext` (`packages/runner/src/core/types.ts:72-88`) that persists workflow runtime variables immediately, reusing the existing `ServerConnection.patchRunVars` RPC (`packages/runner/src/server/connection.ts:194-202`, `PATCH /api/workflow-runs/{id}/workflow-profile/variables`). Wire it in `baseContext()` (`packages/runner/src/runtime/executor.ts:578-580`) as a thin closure over `this.connection.patchRunVars(work.workflowRunId, vars, signal)`.

Proposed shape: `context.setRuntimeVar(key: string, value: JsonValue): Promise<void>` (or `context.writeVars(vars: JsonObject)`). A single-key variant matches the archive use case and reads clearly at the call site.

This is deliberately distinct from the declarative `setVars` mechanism (`executor.ts:539-559`):
- `setVars` is post-completion, atomic-with-success, parses the action's structured `output`, and a failure flips the task to `failed`. It must remain the only terminal variable side effect.
- The mid-execution write is immediate/best-effort, persists *now*, and is **not** rolled back if the task later fails. That non-rollback is the whole point: a retry must observe the value written before the failure.

On the next dispatch, `work.variables` (built server-side and sent in `WorkDispatchResponse`) includes the patched value, so the action reads it via `context.variables` — no new read path is needed. This is the same lifecycle `setVars` already relies on; only the *timing* of the write moves earlier.

Internal key convention: store under a reserved, namespaced prefix that the declarative `setVars` path and template rendering will not collide with, e.g. `_actions.archiveChange.destination`. The leading underscore signals "runner-internal, not user-declared."

**Alternatives considered:**
- *Have the action call `context.serverConnection?.patchRunVars(...)` directly.* Rejected as the primary API — it's viable (and is the exact precedent used by `addTasks` in `openspec.ts:80-81`), but a dedicated `ActionContext` helper centralizes error handling, gives a uniform testable seam, and makes the "this persists immediately and outlives a failure" semantics explicit rather than implicit. The `serverConnection` field remains available for one-off needs; the helper is the encouraged path for variable writes.
- *Extend the declarative `setVars` to support a "pre-execute" variant.* Rejected — `setVars` is coupled to the action's terminal `output`, which does not exist mid-execution. Conflating the two would undermine the atomic-success guarantee of `setVars`.
- *Add a separate "checkpoint" work-item type.* Rejected — massive overkill for persisting one string; the runtime-variable store already exists and is the right substrate.

## Risks / Trade-offs

- **`--force` on a branch that is *not* actually single-owner would silently overwrite remote work.** -> Mitigation: `force: true` is opt-in per task and applied *only* to the `recover:push` under `base-moved`, whose branch is `mohist/run-<runId>` by construction. Every other push keeps `forceWithLease`. The branch-naming convention is enforced by the workflow, not guessed by the action.
- **Mid-execution variable write is not rolled back on task failure, so a stale/partial value could persist.** -> Mitigation: the archive action writes only the deterministic archive *name*, which is correct by construction once computed; it is never "stale." The helper's contract (immediate, non-transactional) will be documented. Actions must treat it as a monotone, append-only fact, not as transactional state.
- **Removing `retrySelf` changes recovery semantics; if the conflict-resolution agent *doesn't* complete the rebase, the flow continues to `recover:push` against a conflicted tree.** -> Mitigation: the agent prompt already mandates "loop until complete + `git rebase --continue`" and is an acceptance-gated task. If the agent fails, its own task fails and recovery stops before `recover:push`. The pre-existing behavior (loop to budget exhaustion) was strictly worse.
- **`patchRunVars` failure mid-execution could abort the archive action.** -> Mitigation: the write happens before the irreversible move, so a failed write fails safely (no partial state). Treat a write failure as a `retry-safe` archive failure so the existing `budget: 3` recovery re-attempts. (Open question: whether to make the write best-effort-ignore vs. hard-fail — see below.)
- **Internal `_actions.*` variable keys live in the same namespace as user-declared variables.** -> Mitigation: leading-underscore reserved prefix convention; user profiles do not declare underscore keys. Low risk given the closed set of built-in actions.

## Migration Plan

This change is internal and there is no version compatibility to preserve (project is in active development). Deployment is a single coordinated update of server + runner; there is no mixed-version window to worry about.

1. **Runner first** (`packages/runner`):
   - Add `force` input to `push.ts` (Decision 2).
   - Add `setRuntimeVar`/`writeVars` to `ActionContext` + `baseContext()` (Decision 5).
   - Update `archiveChangeAction` to read/persist the archive name (Decision 4).
   - Add/Update runner unit tests (push `force` mode; archive cross-day idempotency with a fake connection; mid-execution write survives a simulated post-write failure).
2. **Server** (`packages/server`):
   - Edit `mohist-github-pr.workflow.yaml`: remove `retrySelf` (Decision 1), remove `conflictMode: task` (Decision 3), switch `recover:push` to `force: true` (Decision 2).
   - Update workflow profile assertion tests for the corrected recovery handlers.
   - `mohist-default.workflow.yaml` needs no recovery-config sync; its `archive-change` inherits the runner fix automatically.
3. **Restart** via `mo update runner` and `mo update server` (do not `dotnet run` manually — avoids runner-id drift and workflow sticky-assignment mismatch).

**Rollback:** revert the commits and re-run `mo update`. Because the changes are config flags and a new (additive) action input, rollback is clean — no data migration, no schema change. Already-archived directories from the old date-prefix scheme remain valid (the new code's `findExistingArchive` still matches them when the persisted name is absent on a first run).

## Open Questions

- **`patchRunVars` failure policy in the archive action.** Should a failed mid-execution variable write hard-fail the task (safer — guarantees the name is durable before the move) or be best-effort (proceed with the move, accept a possible non-idempotent retry)? Leaning toward hard-fail-as-`retry-safe` so the `budget: 3` recovery re-attempts the write, but this needs confirmation against the server's variable-endpoint reliability.
- **`ActionContext` helper signature: single-key `setRuntimeVar(key, value)` vs. bag `writeVars(vars)`.** The archive use case is single-key, but a bag matches `patchRunVars`'s shape and the declarative `setVars` naming. Minor; will follow whichever is more consistent with the existing `setVars`/`patchRunVars` vocabulary (leaning `writeVars` for symmetry).
- **Whether the internal `_actions.*` namespace needs server-side enforcement** (reserved-prefix validation) or is purely a runner-side convention. Given the closed action set, runner-side convention is likely sufficient, but worth confirming if user-authored custom actions are on the roadmap.
