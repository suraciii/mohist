## Context

The canonical test-duration configuration declares an ordered measurement sequence, currently `[cli, server-spec]`, and a Vitest isolation Track, currently `runner`. `planTracks` first creates one `PlannedLane` for each Track in the requested application, repository, or focused scope, then `applyDurationMeasurementPhase` adds Resources and dependency barriers before the scheduler consumes the plan.

The current phase function treats every configured measurement Track as mandatory in every scope. If one configured Track has no matching selected lane, it returns the original plan immediately. A Server application scope therefore loses isolation for its selected `server-spec` lane when `cli` is absent, even though the scope selection is valid. The scheduler already provides the required Resource and dependency semantics, so the defect is in phase normalization rather than scheduling, configuration validation, or test execution.

The design is constrained to deterministic canonical planning. It must preserve the existing Track commands, populations, budgets, deadlines, Resource limits, CI topology, and fail-closed behavior for malformed multi-lane groups.

## Goals / Non-Goals

**Goals:**

- Normalize configured measurement Tracks to the selected lanes using configured order as the source of truth.
- Preserve the existing measurement Resource, predecessor terminal, final terminal, and selected isolation Track behavior for the normalized sequence.
- Keep focused scopes local: absent Tracks and absent isolation lanes must not be introduced into their plans.
- Preserve the existing fail-closed result when a selected multi-lane Track has no coverage terminal.
- Add deterministic planner tests for partial, full, zero-match, focused, and multi-lane cases.

**Non-Goals:**

- Changing `test-duration.config.jsonc`, its Track IDs, or the public configuration shape.
- Changing the scheduler, Resource capacity rules, worker count, test population, duration budgets, suite deadlines, or CI job topology.
- Adding global serialization, retry behavior, or production test changes.
- Introducing a new persisted format, API, dependency, or runtime abstraction.

## Decisions

### 1. Normalize inside `applyDurationMeasurementPhase`

Before constructing `measurementGroups`, filter `durationMeasurementTracks` to IDs with at least one matching `planned` lane. Preserve the original order and retain the existing duplicate-ID fail-closed check. If the filtered sequence is empty, return a shallow copy of the original plan. Iterate the filtered sequence for all later coverage, terminal, Resource, and dependency calculations.

This keeps scope semantics at the point where selected lanes and canonical policy meet. The early return preserves each selected lane's existing Resources and dependencies; it does not strip base Track claims. The existing group mapping then remains authoritative: a single-lane group receives `duration-measurement`, later groups depend on the preceding terminal, and non-measurement lanes depend on the final terminal or selected isolation Track.

**Alternative considered:** Filter the configuration in each caller before invoking `planTracks`. Rejected because application, repository, focused, and future callers would each need to reproduce the same intersection rule, increasing drift and making the planner's contract incomplete.

**Alternative considered:** Maintain separate measurement Track lists for every application. Rejected because it duplicates canonical policy and makes configuration responsible for scope selection that the planner already owns.

### 2. Reuse the existing graph construction and scheduler contract

The implementation changes only the input sequence to the existing `measurementGroups` loop. Coverage lanes remain the terminal for valid multi-lane groups. Existing `withLaneConstraints` continues to merge and deduplicate dependencies and Resources. `planTracks` keeps its signature, and the scheduler continues to enforce the resulting graph and `duration-measurement` capacity.

**Alternative considered:** Rebuild the full dependency graph or add a scheduler concept for measurement phases. Rejected because the current graph already expresses ordered groups, terminal joins, Resource claims, and isolation fan-out; changing it would expand the failure surface without solving the missing intersection.

### 3. Keep existing multi-lane behavior at a narrow unit seam

This change does not alter multi-lane expansion or validation. Canonical configuration rejects duplicate Track IDs and `planTracks` creates one lane per selected Track, so a valid multi-lane group is not naturally constructible through the public canonical planner. Export the existing pure `applyDurationMeasurementPhase` helper and its `PlannedLane` shape as a narrow internal unit seam. Tests can construct synthetic lanes to preserve the existing coverage-terminal and fail-closed behavior without adding a production expansion path or a new planner capability.

**Alternative considered:** Construct multi-lane cases through duplicate canonical `TrackConfig` IDs. Rejected because `validateConfig` rejects duplicate IDs and the public planner does not represent lane expansion that way.

**Alternative considered:** Add a new production lane-expansion or coverage configuration path. Rejected because the Issue only fixes ordered configured/selected intersection and explicitly excludes new planner capabilities.

### 4. Verify the matrix at the planner boundary

Extend `scripts/test-duration/guard.test.ts` around the existing planner coverage. Tests will assert lane IDs, `dependsOn`, and `resources` for:

- `[cli, server-spec]` configured with only `server-spec` selected;
- both measurement Tracks selected in configured order;
- no configured measurement Track selected, using an input lane that already has Resources and `dependsOn` and comparing serialized output;
- a focused scope with no measurement or isolation Track;
- a synthetic valid multi-lane group with a coverage terminal at the narrow phase-helper seam; and
- a synthetic malformed multi-lane group without a coverage terminal at the same seam.

The emitted `plan.json` continues to serialize the resulting `planned` lanes, so no separate evidence schema or writer is needed. Existing canonical duration tests remain the integration check that the configured budgets and deadlines are unchanged.

**Alternative considered:** Validate the repair only through full CI duration evidence. Rejected because performance evidence cannot reliably distinguish a correct partial-match graph from an accidentally unisolated or globally serialized plan; the selection matrix must be asserted directly at the deterministic planner boundary.

## Risks / Trade-offs

- [A partial scope now starts a measurement Track that previously ran without isolation] -> This is the intended correction; deterministic planner assertions and existing `duration-measurement` capacity enforce the new boundary.
- [Filtering can hide a configuration Track that is simply absent from a scope] -> The plan records only selected lanes, and the configured order remains visible in the normalized sequence; zero-match scopes remain unchanged rather than inventing work.
- [A malformed multi-lane Track could still produce an unisolated plan] -> Preserve the existing fail-closed behavior and add an explicit regression test for the missing coverage terminal.
- [Changing the normalized plan can alter queue timing for application scopes] -> No worker, deadline, budget, or Resource limit changes are allowed; canonical CI must retain its existing performance gates.
- [A future caller may bypass `planTracks`] -> Keep all intersection logic inside `applyDurationMeasurementPhase`, the shared planner boundary used by `main`, rather than adding caller-specific filtering.

## Migration Plan

1. Add the ordered-intersection normalization in `scripts/test-duration/guard.ts`.
2. Add the deterministic selection and multi-lane tests in `scripts/test-duration/guard.test.ts`.
3. Run the focused test-duration tests, `npm run test:fast`, and the full `npm run verify` gate. Confirm canonical evidence still reports the existing populations, budgets, deadlines, and Resource limits.
4. Deploy as a code-only planner change. No data migration, configuration migration, API change, or coordination with other services is required.

Rollback is a source revert of the planner and its tests. No persisted plan schema changes are introduced; already-written evidence remains readable, and newly planned scopes revert to the previous behavior until the fix is redeployed.

## Open Questions

None. The configured Track sequence, isolation Track, coverage-terminal convention, and fail-closed behavior are already defined by the current planner and canonical configuration.
