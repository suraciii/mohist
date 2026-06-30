## ADDED Requirements

### Requirement: Agent-usage surface exposes a per-day cumulative cost-per-ship series over the trailing window

The project agent-usage surface SHALL expose a per-day cumulative cost-per-ship series across the same trailing window the existing 7-day daily-bucket usage timeseries covers. For each day in the window, the series SHALL provide the cumulative spend as of that day (the sum of `UsageSummary.CostAmount` over every session with usage whose `CreatedAt` falls on or before the end of that day) and the cumulative shipped-issue count as of that day (the count of issues whose persisted completion time falls on or before the end of that day), from which the per-day cumulative cost-per-ship ratio is derived. The cumulative series SHALL be computed purely from already-recorded per-session `UsageSummary` and the already-recorded issue completion time — the same sources the existing rollup and timeseries already use — and SHALL NOT introduce any new event, state collection, session-domain write, or issue-domain write.

#### Scenario: Per-day cumulative series carries history up to and including each day

- **WHEN** the agent-usage surface returns the cumulative series for a project whose sessions and shipped issues span the trailing window
- **THEN** each day's cumulative spend SHALL be the sum of cost over all sessions with usage created on or before the end of that day
- **AND** each day's cumulative shipped-issue count SHALL be the count of issues whose completion time is on or before the end of that day
- **AND** the per-day cumulative cost-per-ship SHALL be that day's cumulative spend divided by that day's cumulative shipped-issue count

#### Scenario: A day with no shipped issues yet yields an undefined cost-per-ship

- **WHEN** the cumulative shipped-issue count as of a day in the window is zero
- **THEN** the cumulative cost-per-ship for that day SHALL be the undefined (empty) result
- **AND** it SHALL NOT be reported as a numeric zero
- **AND** it SHALL be distinguishable from a genuine zero produced by non-zero spend and zero cumulative cost

#### Scenario: A genuine zero cumulative cost is distinguishable from the undefined result

- **WHEN** as of a day in the window the cumulative spend is zero but the cumulative shipped-issue count is greater than zero
- **THEN** the cumulative cost-per-ship for that day SHALL be a genuine zero
- **AND** this SHALL be distinguishable from the undefined result produced by a zero-shipped-issues day

#### Scenario: Cumulative series introduces no new data collection

- **WHEN** the cumulative series is computed and returned
- **THEN** the series SHALL be derived from the already-recorded per-session `UsageSummary` and the already-recorded issue completion time
- **AND** no new event, state collection, session-domain write, or issue-domain write SHALL be introduced to support the series

### Requirement: Cumulative series is co-located with the agent-usage surface and leaves existing contracts unchanged

The per-day cumulative cost-per-ship series SHALL be exposed co-located with the existing project agent-usage surface (the existing 7-day daily-bucket usage timeseries at `/api/projects/{projectRef}/agent/usage` and the cost rollup), so a dashboard can read the trend in the same surface it already reads for spend. Introducing the cumulative series SHALL NOT alter, remove, or re-shape the existing `totalCost`, `todayCost`, `doneIssuesCount`, or `costPerShip` rollup contract, nor the existing 7-day daily-bucket usage timeseries contract; the cumulative series is strictly additive. The zero-sample cumulative-series cases SHALL be returned as `200` with the defined empty result per day, not as an error. The surface SHALL return `404` for an unknown project, consistent with the existing endpoints.

#### Scenario: Cumulative series is readable from the agent-usage surface

- **WHEN** a client requests the project agent-usage surface for a project that has usage or shipped issues
- **THEN** the surface SHALL return the per-day cumulative cost-per-ship series alongside the existing timeseries and rollup
- **AND** the existing 7-day daily-bucket usage timeseries and the `totalCost`/`todayCost`/`doneIssuesCount`/`costPerShip` rollup SHALL remain available and unchanged

#### Scenario: Existing rollup and timeseries contracts are preserved

- **WHEN** the cumulative series is added to the agent-usage surface
- **THEN** the existing `totalCost`, `todayCost`, `doneIssuesCount`, and `costPerShip` rollup fields SHALL retain their existing semantics and shape
- **AND** the existing 7-day daily-bucket usage timeseries contract SHALL NOT be altered or removed

#### Scenario: Zero-sample cumulative series returns 200, not an error

- **WHEN** a client requests the cumulative series for a project that has no sessions with usage or no shipped issues
- **THEN** the surface SHALL return `200` with the defined empty result for each affected day
- **AND** the response SHALL NOT report a numeric zero for an undefined cumulative cost-per-ship day

#### Scenario: Unknown project returns not found

- **WHEN** a client requests the cumulative series for a project reference that does not resolve to a known project
- **THEN** the surface SHALL return `404`
