### Requirement: Normalize measurement Tracks to the selected scope

The canonical duration-measurement sequence MUST be the ordered intersection of the configured measurement Track IDs and the Track lanes selected for the current application, repository, or focused scope. An absent configured Track MUST NOT cancel measurement isolation for later configured Tracks that are selected.

#### Scenario: Later configured Track remains isolated after an earlier Track is absent
- **WHEN** the configured measurement sequence is `[cli, server-spec]` and the selected scope contains `server-unit`, `server-arch`, and `server-spec` but not `cli`
- **THEN** the planner MUST create a measurement group for `server-spec`, MUST preserve its measurement phase, and MUST NOT add `cli` to the planned scope

#### Scenario: Full scope preserves configured measurement order
- **WHEN** the configured measurement sequence is `[cli, server-spec]` and both Tracks are selected
- **THEN** the planner MUST place `cli` before `server-spec` in the measurement sequence regardless of the order of unrelated selected Tracks

### Requirement: Preserve measurement Resources and terminal barriers

Each retained single-lane measurement group MUST claim the existing `duration-measurement` Resource. Each later retained measurement group MUST depend on the terminal lane of its preceding retained group, and every other selected lane MUST wait for the terminal lane of the final retained measurement group unless an isolation Track boundary applies.

#### Scenario: Partial application scope keeps the selected measurement Resource and barrier
- **WHEN** `server-spec` is the only retained measurement Track and `server-unit` is another selected lane
- **THEN** `server-spec` MUST claim `duration-measurement`, and `server-unit` MUST depend on the terminal lane of `server-spec`

#### Scenario: Ordered full portfolio uses each measurement boundary
- **WHEN** `cli` and `server-spec` are both selected measurement Tracks
- **THEN** `cli` MUST claim `duration-measurement` without a predecessor, `server-spec` MUST claim `duration-measurement` and depend on `cli`, and non-measurement lanes MUST wait for the terminal lane of `server-spec` unless they are governed by the configured isolation Track

### Requirement: Keep the isolation Track boundary scope-local

When the configured duration isolation Track is selected, it MUST wait for the final retained measurement terminal and MUST claim `duration-measurement`. Selected Vitest lanes other than that isolation Track MUST depend on the isolation Track. An isolation Track that is absent from the current scope MUST NOT introduce a dependency or an unselected lane.

#### Scenario: Selected isolation Track gates the remaining Vitest fan-out
- **WHEN** `[cli, server-spec]` are the retained measurement Tracks, `runner` is the selected isolation Track, and `web` is another selected Vitest lane
- **THEN** `runner` MUST depend on the terminal lane of `server-spec` and claim `duration-measurement`, and `web` MUST depend on `runner`

#### Scenario: Focused scope does not inherit an absent isolation Track
- **WHEN** a focused scope selects only `server-unit` while the configured measurement Tracks are `[cli, server-spec]` and the configured isolation Track is `runner`
- **THEN** the planner MUST leave `server-unit` without a measurement Resource or measurement dependency and MUST NOT add `runner`, `cli`, or `server-spec`

### Requirement: Preserve zero-match plans unchanged

If no configured duration-measurement Track is present in the selected scope, the planner MUST return the selected plan with its existing lanes, Resources, and dependencies unchanged.

#### Scenario: No configured measurement Track is selected
- **WHEN** the configured measurement sequence is `[cli, server-spec]` and the selected scope contains only `server-unit`
- **THEN** the planned `server-unit` lane MUST retain its original Resources and dependencies, and no measurement phase MUST be added

### Requirement: Fail closed for malformed multi-lane measurement groups

A selected measurement Track that expands to multiple execution lanes MUST use its existing coverage terminal to represent group completion. If multiple execution lanes have no valid coverage terminal, the planner MUST return the unisolated selected plan rather than silently applying incomplete measurement isolation.

#### Scenario: Valid multi-lane Track uses its coverage terminal
- **WHEN** a selected configured measurement Track expands to multiple execution lanes and a `<track-id>-coverage` terminal lane is present
- **THEN** downstream measurement groups and non-measurement lanes MUST depend on that coverage terminal, preserving the existing multi-lane terminal semantics

#### Scenario: Missing multi-lane coverage terminal fails closed
- **WHEN** a selected configured measurement Track expands to multiple execution lanes and its coverage terminal is absent
- **THEN** the planner MUST return the selected plan without newly added measurement Resources or measurement dependencies

### Requirement: Do not change duration policy outside scope normalization

The scope-isolation behavior MUST NOT change Track populations, test commands, duration budgets, suite deadlines, worker capacity, Resource limits, or CI execution topology.

#### Scenario: Partial-match repair changes only planning isolation
- **WHEN** a partial application scope is planned under the existing canonical configuration
- **THEN** the planner MUST preserve every selected Track's execution and budget configuration and MUST change only the measurement Track membership and the Resources or dependencies required by the normalized phase
