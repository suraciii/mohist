## ADDED Requirements

### Requirement: The agent-cost surface returns windowed current and previous-adjacent spend and per-issue cost alongside the cumulative rollup

The agent-cost surface SHALL additionally return a **windowed** spend and per-issue cost for the current window and for the immediately preceding window of the same length, distinct from the existing cumulative `totalCost`, `todayCost`, `doneIssuesCount`, and `costPerShip` rollup. The windowed spend for a window SHALL be the sum of the per-session `UsageSummary.CostAmount` over sessions with usage whose creation time falls within that window; the windowed per-issue cost for a window SHALL be that window's spend divided by the count of issues completed (reached `done`) within that window. The previous window SHALL be the same length as and immediately precede the current window, and both SHALL advance with the current time. The surface SHALL return the current-window and previous-window spend and per-issue cost together so a consumer can derive the spend delta and the per-issue-cost delta in a single read. When a window has no sessions with usage, that window's spend SHALL be the defined empty result; when a window has no completed issues, that window's per-issue cost SHALL be the undefined (empty) result, each evaluated independently per window and per metric. This return SHALL be strictly additive: the existing cumulative rollup and the existing 7-day usage timeseries SHALL be preserved unchanged; only the windowed current-and-previous figures are added.

#### Scenario: Windowed spend is the sum of in-window session cost

- **WHEN** the current window contains sessions with usage whose `CostAmount` values sum to 1.82
- **THEN** the current-window spend SHALL be 1.82
- **AND** the previous-window spend SHALL be the corresponding sum over sessions created in the previous window

#### Scenario: Windowed per-issue cost is window spend over in-window completed issues

- **WHEN** the current window's spend is 1.82 and 5 issues completed within the current window
- **THEN** the current-window per-issue cost SHALL be 1.82 / 5
- **AND** the previous-window per-issue cost SHALL be the previous window's spend divided by the previous window's completed-issue count

#### Scenario: Both windows' figures are returned for delta derivation

- **WHEN** the surface returns current and previous windowed spend and per-issue cost
- **THEN** a consumer SHALL be able to derive the spend delta and the per-issue-cost delta from the two windows in a single read

#### Scenario: A window with no usage or no completed issues yields the empty result for the affected metric

- **WHEN** the current window has sessions with usage but no completed issues
- **THEN** the current-window spend SHALL be a real computed value
- **AND** the current-window per-issue cost SHALL be the undefined (empty) result
- **AND** the two emptiness states SHALL be evaluated independently

#### Scenario: The cumulative rollup and usage timeseries are preserved

- **WHEN** the windowed current-and-previous figures are added to the surface
- **THEN** the existing cumulative `totalCost`, `todayCost`, `doneIssuesCount`, and `costPerShip` rollup SHALL retain their existing semantics and shape
- **AND** the existing 7-day usage timeseries SHALL remain available and unchanged
- **AND** the windowed figures SHALL be strictly additive to the existing response
