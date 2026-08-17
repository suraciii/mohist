# Self-Review: Issue 625 Plan

Round: **re-review**. I re-read the canonical issue body with `mo issue view
625 --project proj_f6c141d63b6243bfbb481737b2243b87 --json body` before
checking the updated `proposal.md`, `design.md`, `tasks.json`, and both
specification files. I also verified the relevant current Server workflow
binding, stage initialization, profile resolution, Runner script timeout, and
result-journal paths.

## Verdict

**FAIL** — one prior must-fix finding remains unresolved in the actual plan/codebase
boundary. It is listed before observations.

## Must-Fix Findings

### M-2 remains unresolved — lane mode is not actually immutable or persisted

The previous review's M-2 disposition says that lane behavior is selected from
an immutable, persisted bound definition. The updated plan repeats that claim at
`openspec/changes/issue-625/design.md:56,85,90-92` and in
`openspec/changes/issue-625/tasks.json:59`, but neither the current code nor the
task breakdown provides that definition snapshot or a persisted lane-mode
marker.

The current `WorkflowRun` persists a profile ID and agent action, not the full
workflow definition (`packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.cs:67-69`).
`WorkflowRunBindingParticipant` creates and stores only a `WorkflowStructure`
containing stage names and approval flags (`packages/server/src/Mohist.Server/Workflow/Grains/WorkflowRunBindingParticipant.cs:56-63`).
The build task definitions are materialized later by
`WorkflowStageInitializer`, which calls `LoadStageSpecsAsync`
(`packages/server/src/Mohist.Server/Workflow/Grains/WorkflowStageInitializer.cs:48-57`),
and that resolver deliberately reloads the current profile on each stage entry
for hot reload (`packages/server/src/Mohist.Server/Workflow/Services/WorkflowDefinitionResolver.cs:88-118`).

Therefore, a run created with the old aggregate `verify` definition while it is
still in `plan` can enter `build` after the new profiles are deployed and be
materialized with the six new lanes. It has no persisted old build definition
from which the Server can identify it as legacy. That can change its dispatch,
recovery, gate, and historical task behavior, contrary to the issue's explicit
non-goal: **"Retrying, rerunning, or mutating historical blocked WorkflowRuns."**
It also contradicts the plan's required legacy behavior that an old aggregate
run must retain its existing path without synthesized lane blockers or task
rewrites.

This is a must-fix because the mixed-version rollout can still alter an
already-created run, and the stated acceptance/rollout guarantee cannot be
implemented by the listed tasks. The plan must add a concrete immutable boundary
at run initialization, such as persisting the effective workflow definition (or
an explicitly sufficient legacy/lane mode and the required stage definitions),
make later stage initialization use that snapshot, and test a run bound before
profile activation whose build stage starts afterward. Merely inspecting the
currently materialized build tasks does not cover runs whose build stage has not
yet been initialized.

## Re-Review Dispositions

- **M-1 — fixed properly.** `tasks.json:45` now explicitly forbids adding,
  restoring, configuring, or referencing resource-containment mechanisms and
  related failure codes, while preserving only existing process-group
  termination, report protocol, and Runner slot behavior. This matches the
  issue's non-negotiable operator constraint.
- **M-3 — fixed properly.** Every lane is required to carry the profile-specific
  `fix-ci` recovery declaration (`design.md:64-68`, `tasks.json:57`). The plan
  now classifies the preserved underlying timeout/failure instead of the
  Runner's outer scheduling status, keeps the helper outside the lane catalog,
  and requires a later direct same-lane success before `pass`. The associated
  recovery scenarios and tests are explicit.
- **M-2 — not fixed.** The claimed persisted-definition solution is not present
  in the current state model or in any task that captures the definition at run
  binding. The migration guarantee consequently still fails for runs that have
  not reached `build`.

## Dimension Sweep

- **Issue goals and acceptance criteria — checked, must-fix found.** The six
  commands, independent budgets, durable lane outcomes, ordered gate, recovery
  preservation, unchanged strictness, and downstream idempotency are addressed.
  The rollout still violates the issue's no-historical-mutation non-goal for
  already-created runs that have not initialized `build`.
- **Coverage — checked, must-fix gap found.** The plan covers legacy state that
  already has an aggregate task and new lane-enabled state, but not the
  mixed-version case where an old run's future build stage is materialized after
  profile activation. No task persists or tests the required mode/definition
  boundary.
- **Correctness — checked, must-fix found.** The lane classification and
  recovery approach is coherent once a run's mode is known, but the stated mode
  cannot be recovered from the current persisted run state at the point the
  build stage is initialized.
- **Consistency with the current codebase and conventions — checked,
  must-fix inconsistency found.** The plan assumes immutable bound definitions,
  while the current resolver intentionally reloads profile definitions per
  stage. The plan must explicitly change or bypass that hot-reload path for the
  captured run definition.
- **Task breakdown, ordering, and verifiability — checked, must-fix gap found.**
  T-001 through T-004 cover lane state, Runner behavior, profiles, and recovery,
  but no task owns definition snapshotting/mode persistence or the required
  pre-activation/post-build-initialization rollout test.

## Observations

- The initial per-lane timeout values remain an open design question
  (`design.md:80` and `design.md:99`). The task contract requires literal
  positive finite values and an end-to-end clean run, so this remains a tuning
  concern rather than an additional must-fix finding.
- Whether to retain or remove the now-unused `vars.ci.verify` project variable
  remains open (`design.md:84` and `design.md:100`). The issue requires only
  that the aggregate task no longer be part of the built-in gate; either
  compatible choice can satisfy that boundary.
- The plan promises a link from a retry attempt to its failed attempt
  (`design.md:68`), but does not name the persisted field or projection shape.
  The lane ID, attempt identity, and diagnostics requirements are otherwise
  covered, so this is an implementation-detail observation.
- The structural lane predicate could affect a custom profile that happens to
  use all six reserved IDs in the same order. Constraining the predicate to the
  two built-in profile IDs would make the non-goal about arbitrary workflows
  clearer, but this is outside the issue-blocking finding above.

`jq empty openspec/changes/issue-625/tasks.json` and `git diff --check` passed.

<promise>FAIL</promise>
