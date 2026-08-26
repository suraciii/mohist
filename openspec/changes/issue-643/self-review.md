# Self-Review: Issue 643 Plan Artifacts

## Must-Fix Findings

### MF-1 — Focused-scope Resource wording contradicts the existing plan contract

The spec's focused-scope scenario requires `server-unit` to have no
`duration-measurement` Resource (`specs/duration-measurement-scope-isolation/spec.md:33-35`),
and T-001 repeats that a zero-match or focused scope receives no Resource
(`tasks.json:13`). This is inconsistent with both the Issue 643 acceptance
contract and the current code:

- The issue requires focused selection to receive no unrelated Track or
  dependency, while the zero-match criterion requires the selected plan's
  existing Resources and dependencies to remain unchanged.
- `planTracks` always starts with `laneResources(track)`
  (`scripts/test-duration/guard.ts:434-440`), so it preserves Resources already
  declared by the selected Track.
- The canonical `server-unit` Track already declares
  `duration-measurement` in `test-duration.config.jsonc:83`. A focused
  `server-unit` plan therefore legitimately retains that pre-existing Resource.

Following the current wording could make the implementation remove an existing
Resource, violating the zero-match unchanged-plan requirement and changing
current Track policy outside this Issue. The spec and task acceptance must
distinguish a pre-existing Track Resource from a Resource newly added by the
duration-measurement phase, and require only that no absent Track or new phase
constraint is introduced.

## Verdict

FAIL — MF-1 makes the focused and zero-match contract incorrect relative to the
Issue and current planner behavior. The plan is not ready to build until the
Resource wording is reconciled.

## Review Dimensions

### Issue Basis — checked, no issue

The current Issue 643 record was read before reviewing the artifacts. Its
governing criteria are the ordered configured/selected intersection, partial
match isolation, zero-match unchanged plans, full-order and isolation-track
preservation, focused-scope locality, malformed multi-lane fail-closed behavior,
and unchanged canonical budgets, population, deadline, and CI topology.

### Coverage — finding recorded

The proposal, capability spec, design, and T-001 cover the Issue's main planner
and test matrix. MF-1 is a coverage/contract defect because the focused-scope
requirement adds a Resource-removal rule that the Issue does not require and
that conflicts with the zero-match unchanged-plan rule.

### Correctness — finding recorded

The ordered-intersection implementation described in `design.md` correctly
addresses the reported missing-earlier-Track failure and preserves the existing
graph construction. It cannot satisfy the current focused-scope wording
without either removing a pre-existing Resource or failing the spec assertion;
MF-1 therefore remains a correctness blocker.

### Consistency With the Current Codebase — finding recorded

The proposed implementation boundary in `applyDurationMeasurementPhase` is
consistent with the current planner, scheduler, canonical configuration, and
Resource model. The focused-scope Resource statement in the spec/tasks is the
only identified contradiction with those existing conventions and code paths.

### Task Breakdown — checked, no issue

The single T-001 vertical slice is appropriately scoped: planner normalization,
constraint preservation, and their deterministic tests are tightly coupled. Its
dependency graph is a valid one-node DAG, and the task includes acceptance
criteria for every required scenario. T-001 must inherit the corrected
focused-scope Resource wording before execution.

## Verification Performed

- Read the current Issue 643 body and comments with `mo issue view`.
- Read `proposal.md`, `design.md`, `tasks.json`, and the capability spec.
- Read the current planner, planner tests, canonical configuration, configuration
  validation, scheduler regression, and Server CI scope.
- Confirmed the worktree was clean before creating this review artifact.
- No implementation tests were run because this review is limited to plan
  artifacts and must not modify implementation files.

## Observations

- The design/tasks require valid and malformed multi-lane matrix tests, but the
  current `planTracks` path creates one lane per validated Track and no other
  production code constructs `-coverage` lanes. The implementation task should
  make its synthetic planner fixture explicit so these tests do not become
  superficial coverage of an unreachable shape. This is an observation because
  the existing pure planner branch can still be exercised without changing the
  Issue's scope.
- The Issue refers to a 300-second suite deadline, while the checked-in
  top-level `test-duration.config.jsonc` uses a 420-second full-suite deadline
  and the `server-spec` Track uses a 300-second Track deadline. The plan correctly
  forbids configuration changes; implementation evidence should identify which
  existing Server budget/deadline is being preserved.
- The tasks require `npm run test:fast` and `npm run verify`, but do not name a
  focused command for the planner matrix. The existing package scripts expose
  `test:archscripts`; this is an execution convenience rather than a plan
  completeness blocker.

<promise>FAIL</promise>
