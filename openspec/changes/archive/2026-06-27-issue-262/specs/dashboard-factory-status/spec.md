## MODIFIED Requirements

### Requirement: Headline reserves a today-cost field slot that ships empty

The headline SHALL surface a **today-cost** field positioned alongside the runner-online, in-flight, awaiting-approval, and today-shipped fields. The field SHALL be populated from the project's agent cost rollup endpoint (`agent-cost-metrics` `todayCost`) — the slot that previously shipped empty pending that endpoint is now connected to the rollup value. The headline SHALL source the value from the rollup endpoint rather than recomputing it over the local session set. The empty/zero-sample case (the rollup returning no sessions with usage for the current day) SHALL render in a way that is visibly distinct from a literal zero-cost value, so a missing or empty rollup is not mistaken for free operation; a genuine `todayCost` of zero produced by sessions with usage that summed to zero SHALL render as a real numeric zero, distinct from the empty case.

#### Scenario: Today-cost field is populated from the rollup endpoint

- **WHEN** the factory status headline renders and the agent cost rollup endpoint returns a `todayCost` value with a non-empty sample
- **THEN** the today-cost field SHALL display that numeric `todayCost` value
- **AND** the value SHALL come from the rollup endpoint rather than being recomputed locally over the session set
- **AND** the runner-online, in-flight, awaiting-approval, and today-shipped fields SHALL continue to render their real values

#### Scenario: Empty today-cost is distinct from a zero value

- **WHEN** the agent cost rollup returns the empty/zero-sample result for `todayCost` (no sessions with usage for the current day)
- **THEN** the today-cost field SHALL render an empty/no-data placeholder
- **AND** the slot SHALL NOT display a numeric zero that could be mistaken for an actual computed cost

#### Scenario: Genuine zero today-cost renders as a real zero

- **WHEN** the agent cost rollup returns a `todayCost` of zero produced by sessions with usage that summed to zero
- **THEN** the today-cost field SHALL render a numeric zero
- **AND** it SHALL be distinguishable from the empty/zero-sample placeholder
