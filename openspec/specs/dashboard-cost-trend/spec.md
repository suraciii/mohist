### Requirement: Daily cost bar chart renders one bar per trailing day in the Productivity zone

The Dashboard `Productivity` zone SHALL render a daily cost bar chart: one bar per trailing day across the same trailing window the project agent-usage timeseries already exposes, with each bar's height encoding that day's token cost. The per-bar cost SHALL be sourced from the existing agent usage timeseries daily buckets (the same data the `InvestmentPanel` rollup derives from); the cost-trend widget SHALL NOT introduce a second data collection path. The existing `InvestmentPanel` scalar figures SHALL remain; the bar chart is an addition to the zone, not a replacement.

#### Scenario: One bar renders per trailing day, height from that day's cost

- **WHEN** the Productivity zone renders the daily cost bar chart for a project whose agent-usage timeseries has cost across the trailing window
- **THEN** the chart SHALL render one bar per trailing day
- **AND** each bar's height SHALL encode that day's token cost sourced from the agent-usage timeseries daily bucket

#### Scenario: A day with no cost renders a zero-height bar rather than a gap

- **WHEN** a day within the trailing window has no sessions with usage (zero cost)
- **THEN** the chart SHALL render a bar for that day at zero height
- **AND** the chart SHALL NOT omit the day or collapse the axis

#### Scenario: Bar values come from the existing timeseries, not a new collection

- **WHEN** the daily cost bar chart renders
- **THEN** the per-day cost values SHALL be sourced from the existing agent-usage timeseries
- **AND** the widget SHALL NOT introduce a new data collection path for daily cost

### Requirement: A cost-per-ship trend line overlays the daily cost bars

The daily cost bar chart SHALL overlay a cost-per-ship trend line: for each day in the trailing window, the line plots cumulative spend divided by cumulative shipped-issue count, evaluated as of that day (i.e., across the project's history up to and including that day). The trend line SHALL be sourced from the per-day cumulative cost-per-ship series the project agent-usage surface exposes; the widget SHALL NOT compute the cumulative ratio client-side from a windowed-only sample. The line SHALL express whether unit-delivery cost is rising or falling as output scales.

#### Scenario: Trend line plots cumulative spend over cumulative shipped count per day

- **WHEN** the daily cost bar chart renders the cost-per-ship trend overlay for a project that has cumulative spend and at least one shipped issue as of a day in the window
- **THEN** the trend line SHALL plot, for that day, cumulative spend as of that day divided by cumulative shipped-issue count as of that day
- **AND** the trend line SHALL be sourced from the per-day cumulative cost-per-ship series exposed by the agent-usage surface

#### Scenario: Days with no shipped issues yet produce an undefined cost-per-ship

- **WHEN** as of a day in the trailing window the cumulative shipped-issue count is zero
- **THEN** the cost-per-ship for that day SHALL be the undefined (empty) result
- **AND** the trend line SHALL NOT plot a numeric value (zero or otherwise) for that day

#### Scenario: Free shipping up to a day is a real zero, not an empty sample

- **WHEN** as of a day in the trailing window the cumulative spend is zero but the cumulative shipped-issue count is greater than zero
- **THEN** the trend line SHALL plot a genuine zero cost-per-ship for that day
- **AND** this SHALL be distinguishable from the undefined result produced by a zero-shipped-issues day

### Requirement: Cost-trend widget renders loading, error, and empty states with a next action

The daily cost trend widget SHALL render the loading state while the underlying agent-usage data is in flight, the error state when the underlying data fetch fails, and the empty state when the project has no agent usage recorded yet. The empty state SHALL name a concrete next action telling the operator how the chart will gain data — that cumulative cost and cost-per-ship appear once an agent session reports usage on the project. The widget SHALL route these states through the shared dashboard chart three-state wrapper.

#### Scenario: Loading state renders while data is in flight

- **WHEN** the underlying agent-usage data for the cost trend is still loading
- **THEN** the widget SHALL render the loading state via the shared chart three-state wrapper
- **AND** the chart content SHALL NOT render until data has resolved

#### Scenario: Error state renders when the data fetch fails

- **WHEN** the underlying agent-usage fetch for the cost trend fails
- **THEN** the widget SHALL render the error state via the shared chart three-state wrapper
- **AND** the widget SHALL NOT render stale or fabricated chart content

#### Scenario: Empty state renders with a next action when there is no usage yet

- **WHEN** the project has no agent sessions with usage recorded yet
- **THEN** the widget SHALL render the empty state via the shared chart three-state wrapper
- **AND** the empty state SHALL name a concrete next action describing that the chart gains data once an agent session reports usage on the project

### Requirement: Cost-trend widget composes against the dashboard chart baseline and is read-only

The daily cost trend widget SHALL compose against the reusable dashboard chart baseline: the single pinned chart library, the theme-token color contract, the shared three-state wrapper, the accessibility wrapper (screen-reader data summary and a legend that does not rely on color alone to distinguish the bar series from the trend series), and the numeric (`tabular-nums`) and motion (`transform`-based, `prefers-reduced-motion`-aware) conventions. The widget SHALL be purely read-only: it SHALL NOT mutate issue, session, or workflow state, and SHALL NOT introduce any new backend write, event, or data collection beyond the additive cumulative cost-per-ship series read.

#### Scenario: Widget uses the chart baseline components and conventions

- **WHEN** the daily cost trend widget renders its chart
- **THEN** the widget SHALL render through the single pinned chart library
- **AND** the widget SHALL source colors from theme tokens, route states through the shared chart three-state wrapper, expose the accessibility wrapper, and apply the tabular-nums and transform-based motion conventions

#### Scenario: Widget is read-only with respect to domain state

- **WHEN** a user views or refreshes the daily cost trend widget
- **THEN** the widget SHALL NOT perform any write or mutation against issue, session, or workflow domain state
- **AND** the widget SHALL NOT introduce any new backend write, event, or data collection beyond the additive cumulative cost-per-ship series read
