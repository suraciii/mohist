### Requirement: Total cost sums cost across every session with usage in the project

The `totalCost` aggregation SHALL be the arithmetic sum of the per-session `UsageSummary.CostAmount` over every agent session belonging to the project that carries a usage summary. A session whose `UsageSummary` is absent (null) SHALL NOT contribute to `totalCost`; such a session SHALL be skipped, mirroring the additive cost aggregation the existing 7-day daily-bucket usage timeseries already performs. `totalCost` SHALL NOT be time-bounded - it is the cumulative spend across the project's whole history of sessions with usage.

#### Scenario: Total cost is the sum of every contributing session's cost

- **WHEN** a project has three sessions with usage whose `CostAmount` values are `0.02`, `0.05`, and `0.10`
- **THEN** the `totalCost` SHALL be `0.17`
- **AND** the sum SHALL include all three sessions regardless of which day each session was created

#### Scenario: Sessions without usage are skipped

- **WHEN** a project has one session with `UsageSummary.CostAmount: 0.05` and two sessions whose `UsageSummary` is null
- **THEN** the `totalCost` SHALL be `0.05`
- **AND** the two sessions without usage SHALL NOT contribute to the sum

### Requirement: Today cost is the spend within the current calendar day bucket

The `todayCost` aggregation SHALL be the sum of the per-session `UsageSummary.CostAmount` over sessions with usage whose `CreatedAt` falls within the current UTC calendar day - i.e. exactly the current-day bucket of the existing 7-day daily-bucket usage timeseries (`[today_utc, today_utc + 1d)`). Sessions whose `CreatedAt` falls on a prior calendar day SHALL NOT contribute to `todayCost`. The current-day boundary SHALL move with the current time.

#### Scenario: Today cost sums only sessions created today

- **WHEN** a project has a session with usage created today (UTC) with `CostAmount: 0.04` and a session with usage created two days ago with `CostAmount: 0.10`
- **THEN** the `todayCost` SHALL be `0.04`
- **AND** the session from two days ago SHALL NOT contribute to `todayCost`

#### Scenario: Today cost bucket boundary matches the timeseries current-day bucket

- **WHEN** the aggregation is requested at two different UTC times whose calendar dates differ
- **THEN** the `todayCost` bucket SHALL move with the current UTC date
- **AND** a session whose `CreatedAt` ages into the prior day between the two requests SHALL drop out of `todayCost`

### Requirement: Done-issues count is the number of shipped issues in the project

The `doneIssuesCount` aggregation SHALL be the number of issues in the project whose status is `Done`. An issue with any status other than `Done` (in-flight, failed, cancelled, or otherwise not shipped) SHALL NOT contribute to `doneIssuesCount`. `doneIssuesCount` SHALL NOT be time-bounded - it is the cumulative count of shipped issues across the project's whole history.

#### Scenario: Done-issues count counts only Done issues

- **WHEN** a project has seven issues at status `Done`, three issues at `in_progress`, and one issue at `open`
- **THEN** the `doneIssuesCount` SHALL be `7`
- **AND** the four non-`Done` issues SHALL NOT contribute to the count

### Requirement: Cost-per-ship is total cost divided by shipped-issue count

The `costPerShip` aggregation SHALL equal `totalCost` divided by `doneIssuesCount`. The numerator SHALL be exactly the `totalCost` aggregation and the denominator SHALL be exactly the `doneIssuesCount` aggregation; no other figure SHALL be substituted for either operand. A non-zero `doneIssuesCount` yielding a `totalCost` of zero SHALL produce a genuine `costPerShip` of zero (free shipping), which is a real computed value and not an empty sample.

#### Scenario: Cost-per-ship is total cost over done issues

- **WHEN** a project has `totalCost: 1.50` and `doneIssuesCount: 6`
- **THEN** the `costPerShip` SHALL be `1.50 / 6` (`0.25`)

#### Scenario: Free shipping is a real zero, not an empty sample

- **WHEN** a project has `totalCost: 0` and `doneIssuesCount: 5`
- **THEN** the `costPerShip` SHALL be a genuine `0`
- **AND** this SHALL be distinguishable from the undefined cost-per-ship produced by a zero-shipped-issues project

### Requirement: Zero-sample aggregation returns a defined empty result distinguishable from a real computed value

The rollup SHALL return a defined empty result (rather than an error, an implicit zero, or an implicit undefined) for each metric that lacks a sample, and the empty result for each metric SHALL be distinguishable by the consumer from a genuine computed value so a UI can render "no data yet" rather than a misleading "$0.00" or "free". Specifically: (1) when the project has no sessions with usage, the spend figures (`totalCost` and `todayCost`) SHALL be the empty/zero-sample result, distinguishable from a genuine zero cost produced by sessions with usage that summed to zero; (2) when `doneIssuesCount` is zero, `costPerShip` SHALL be the empty/undefined result (an undefined ratio), which SHALL NOT be reported as zero and SHALL NOT be reported as an error. Each metric SHALL be evaluated independently for emptiness - a non-empty `totalCost` does not imply a non-empty `costPerShip`, and vice versa.

#### Scenario: No sessions with usage yields an empty spend result

- **WHEN** a project has agent sessions but none of them carry a usage summary
- **THEN** the rollup SHALL return the empty/zero-sample result for `totalCost` and `todayCost`
- **AND** the result SHALL NOT be reported as a numeric zero cost
- **AND** the response SHALL be successful (not an error)

#### Scenario: Zero shipped issues yields an undefined cost-per-ship

- **WHEN** a project has sessions with usage (`totalCost: 1.20`) but `doneIssuesCount: 0`
- **THEN** the rollup SHALL return the empty/undefined result for `costPerShip`
- **AND** `costPerShip` SHALL NOT be reported as a numeric zero
- **AND** the response SHALL be successful (not an error)
- **AND** `totalCost` SHALL still be reported as the real computed `1.20`

#### Scenario: Emptiness is evaluated independently per metric

- **WHEN** a project has sessions with usage but no shipped issues
- **THEN** `totalCost` and `todayCost` SHALL be reported as real computed values
- **AND** `costPerShip` SHALL be the empty/undefined result
- **AND** the two emptiness states SHALL NOT be coupled

#### Scenario: A genuine zero cost is distinguishable from the empty spend result

- **WHEN** a project has sessions with usage whose `CostAmount` values sum to exactly zero
- **THEN** the rollup SHALL report `totalCost` as a genuine zero with a non-zero sample count
- **AND** this SHALL be distinguishable from the empty/zero-sample spend result

### Requirement: Backend exposes a project-scoped agent cost rollup endpoint with no new data collection

The server SHALL expose a project-scoped HTTP endpoint that returns the agent cost rollup - `totalCost`, `todayCost`, `doneIssuesCount`, and `costPerShip`, together with their empty/zero-sample state - for the project. The endpoint SHALL be co-located with the existing project agent-usage surface (the existing 7-day daily-bucket usage timeseries endpoint at `/api/projects/{projectRef}/agent/usage`) so a dashboard can fetch the spend summary in one read; the existing 7-day timeseries contract SHALL NOT be removed or altered beyond co-location. The endpoint SHALL compute the rollup purely from the already-recorded per-session `UsageSummary` on agent sessions (the same source as the 7-day timeseries) and the already-recorded issue status; the change SHALL NOT introduce any new event, state collection, session-domain write, or issue-domain write to support the endpoint. The zero-sample cases SHALL be returned as `200` with the defined empty result, not as an error. The endpoint SHALL return `404` for an unknown project.

#### Scenario: Client reads the rollup for a project with spend and shipped issues

- **WHEN** a client requests the cost rollup for a project that has sessions with usage and at least one shipped issue
- **THEN** the server SHALL return `200` with `totalCost`, `todayCost`, `doneIssuesCount`, and `costPerShip` computed only from the already-recorded per-session usage and issue status
- **AND** the existing 7-day usage timeseries endpoint SHALL remain available and unchanged

#### Scenario: Project with no usage or no shipped issues returns the empty results per metric

- **WHEN** a client requests the cost rollup for a project that has no sessions with usage, or no shipped issues
- **THEN** the server SHALL return `200` with the defined empty result for each affected metric
- **AND** the response SHALL NOT report a numeric zero for the empty metrics

#### Scenario: Aggregation introduces no new data collection

- **WHEN** the cost rollup endpoint is invoked
- **THEN** the server SHALL compute the result from the already-recorded per-session usage summaries and issue status
- **AND** no new event, state collection, session-domain write, or issue-domain write SHALL be introduced to support the endpoint

#### Scenario: Unknown project returns not found

- **WHEN** a client requests the cost rollup for a project reference that does not resolve to a known project
- **THEN** the server SHALL return `404`
