# Self-Review: Issue 625 Plan

Round: **first review (full sweep)**. I re-read the canonical issue body and
acceptance criteria from `mo issue view 625 --project
proj_f6c141d63b6243bfbb481737b2243b87` before reviewing `proposal.md`,
`design.md`, `tasks.json`, and both specification files. I also checked the
current built-in profiles, Server workflow state/recovery paths, Runner script
execution, result journaling, and the existing profile/resource contract tests.

## Verdict

**FAIL** — the plan has must-fix problems that violate the issue's
non-negotiable constraints and recovery/rollout goals. They are listed before
observations.

## Must-Fix Findings

### M-1 — T-002 preserves a mechanism the issue forbids and the repository has removed

`openspec/changes/issue-625/tasks.json:45` says to "Keep existing
process-group termination, result protocol, **resource containment**, and Runner
slot policy unchanged." This is not an accurate description of the current
code and gives the builder an instruction to retain or restore exactly the
mechanism prohibited by the issue: per-work resource profiles, cgroups, memory
limits, process-tree containment, resource budgets, or resource-containment
failure codes. The current repository explicitly tests the opposite: the
Runner test at `packages/runner/src/actions/built-in-core.test.ts:7` verifies
that no hidden resource profile is injected, and the built-in profile tests at
`packages/server/tests/Mohist.Server.UnitTests/Issue/Profile/MohistGithubPrIssueWorkflowProfileTests.cs:291-292`
and `MohistWorkflowDefinitionTests.cs:32` assert that `resourceProfile` is
absent.

This violates the issue's **Non-Negotiable Operator Constraints** and the
non-goal forbidding reintroduction of resource containment. The task note must
be corrected to state that no resource-containment mechanism or related
configuration/error code is to be added, restored, or referenced; only the
existing timeout/process-group and Runner slot behavior may remain unchanged.

### M-2 — The migration order can block current and newly created runs

The design makes the all-six-lanes predicate treat a missing lane as blocking
(`openspec/changes/issue-625/design.md:54-58`), but its migration plan deploys
that Server gate before enabling the new profile definitions
(`design.md:84-87`). Existing initialized runs retain the old aggregate
`verify` task and have no lane history. Once the new Server behavior is live,
those runs have six missing lanes and cannot complete the build stage. A new run
created during the stated rollout window can have the same problem if it
materializes the old definition. This is a regression against the issue's goal
that a representative clean run advances to push/check and against the
acceptance criteria requiring the gate to advance after all required lanes pass.

The design's proposed escape hatch is also not an implementation: it says
operators may explicitly rerun the build stage, while the issue explicitly
makes retrying, rerunning, or mutating historical blocked WorkflowRuns a
**Non-Goal** (`design.md:79`, `design.md:94`). The plan needs one coherent
rollout rule: for example, atomically activate the lane definitions with the
gate, or make the gate apply only to a positively identified lane-enabled run
until all old runs drain. It must also explicitly leave legacy blocked runs
untouched rather than relying on an unspecified rerun policy.

### M-3 — Per-lane recovery is not specified in the profile task or the Server recovery contract

The issue requires that a timed-out lane remain resumable at that lane and
that recovery preserve earlier passes. The current recovery mechanism is
declaration-driven: `core/script` failure is converted by the Runner into a
completed scheduling result with inserted recovery/retry tasks when the task's
`recovery` declaration applies (`packages/runner/src/runtime/recovery.ts:65-93`).
The current aggregate profile has that recovery declaration on `verify`, but
replacing `verify` with six tasks does not automatically give the six lane
tasks
that behavior.

The plan only says recovery uses the declared `fix-ci` task "when applicable"
(`openspec/changes/issue-625/design.md:60-64`). T-003's acceptance criteria
(`tasks.json:53-57`) require lane IDs, commands, budgets, and removal of the
aggregate task, but do not require each lane's recovery declaration or define
which recovery operation creates the next attempt. T-004 then says to extend
Server recovery without identifying a trigger/API/state transition that does
so. Following the task literally can leave a timeout as an ordinary failed
workflow task, with no declared repair/retry path, or can accidentally mark the
Runner's recovery-scheduling report as a passed lane.

This violates the issue's **Fix Shape** and acceptance criteria that a timed-out
lane be resumable, earlier pass evidence be preserved, and recovery resume at
the first unfinished lane. The plan must specify whether the same repair and
retry policy is attached to every lane or whether Server owns a separate
lane-recovery operation, including the exact ordering and the rule that the
original `fail`/`timeout` evidence remains non-pass until a later lane attempt
succeeds. Tests in T-003/T-004 must verify that contract.

## Dimension Sweep

- **Issue goals and acceptance criteria — checked, must-fix findings found.**
  The six commands, independent budgets, durable outcomes, ordered gate,
  preserved evidence, idempotent downstream effects, unchanged strictness, and
  prohibited-mechanism constraints were compared directly with the issue. M-1
  violates the operator constraint; M-2 and M-3 leave required rollout/recovery
  behavior incomplete.
- **Coverage — checked, must-fix gaps found.** The plan covers the nominal six
  lanes and status projection, but does not cover a safe activation boundary
  for legacy aggregate runs (M-2) or an explicit recovery definition/operation
  for each replacement lane (M-3). The prohibited resource instruction is
  also covered incorrectly (M-1).
- **Correctness — checked, must-fix problems found.** Ordinary serial tasks and
  the existing report fences are compatible with ordered execution, but the
  stated migration order can make missing lanes permanently block old/current
  runs, and the existing declaration-driven recovery semantics are not tied to
  the six new lane tasks.
- **Consistency with the current codebase and conventions — checked, must-fix
  inconsistency found.** The plan correctly reuses `TaskRun`, `NextWork`, and
  `WorkResultJournal`, but M-1 conflicts with the current no-resource-profile
  tests. The plan also needs to account explicitly for the Runner's existing
  conversion of recoverable failures into scheduling results when classifying
  lane outcomes.
- **Task breakdown, ordering, and verifiability — checked, must-fix gaps
  found.** The dependency graph is acyclic and the broad work areas are
  present, but T-003 does not make per-lane recovery testable and the migration
  order in the design has no corresponding compatibility/activation task.

## Observations

These do not affect the verdict:

- The initial timeout values remain an open question (`design.md:74` and
  `design.md:92`). T-003 does require positive finite literal values and T-004
  can validate a clean run, so this is a tuning/verification concern rather
  than a must-fix issue under the issue's stated criteria.
- The plan leaves whether to retain or remove the now-unused `vars.ci.verify`
  project variable unresolved (`design.md:78` and `design.md:93`). The issue
  only requires that it no longer be part of the built-in gate, so either
  choice can satisfy the stated scope if the contract tests make that boundary
  explicit.
- The design chooses a derived status projection instead of a new event stream
  (`design.md:44`). That is compatible with the issue's requirement for an
  observable result through workflow status or event projections, but the API
  field placement and serialization compatibility should be pinned down during
  implementation.
- The plan's rollback note still mentions stopping or rerunning active lane
  runs (`design.md:80-88`). Once M-2 is corrected, rollback should preserve the
  issue's no-historical-mutation boundary and define only how new lane-enabled
  runs are stopped or drained.

<promise>FAIL</promise>
