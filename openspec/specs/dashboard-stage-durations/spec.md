### Requirement: Stage-duration distribution chart renders one horizontal bar per workflow stage in the Productivity zone

The Dashboard `Productivity` zone SHALL render a stage-duration distribution chart: one horizontal bar per workflow stage reached by at least one delivered issue across the fixed trailing window, with each bar's length encoding that stage's average duration (sourced from the project stage-duration surface). The chart SHALL render the stages in workflow stage order so the operator reads the flow left-to-right (plan → build → check → integrate). The per-bar values SHALL be sourced from the project stage-duration surface; the widget SHALL NOT introduce a second data-collection path. The trailing window SHALL be fixed and SHALL NOT be user-configurable.

#### Scenario: One bar renders per stage, length from that stage's average duration

- **WHEN** the Productivity zone renders the stage-duration chart for a project whose stage-duration surface returns `plan`, `build`, `check`, and `integrate` stages
- **THEN** the chart SHALL render one horizontal bar per stage
- **AND** each bar's length SHALL encode that stage's average duration sourced from the stage-duration surface
- **AND** the bars SHALL be ordered by workflow stage order

#### Scenario: Bar values come from the stage-duration surface, not a new collection

- **WHEN** the stage-duration chart renders
- **THEN** the per-stage average durations SHALL be sourced from the project stage-duration surface
- **AND** the widget SHALL NOT introduce a new data-collection path for stage durations

#### Scenario: The trailing window is fixed and not configurable

- **WHEN** the stage-duration chart renders
- **THEN** the trailing window SHALL span a single fixed length
- **AND** the window length SHALL NOT be configurable by the user

### Requirement: The chart exposes both an average lens and a median lens over the same stages

The stage-duration chart SHALL expose two duration lenses over the same stage population: an **average** lens and a **median** lens, both sourced from the stage-duration surface. The bar lengths SHALL reflect the currently selected lens. Exposing both lenses SHALL let the operator distinguish a typical stage from one skewed by an outlier, without a new backend read.

#### Scenario: Both lenses are available on the chart

- **WHEN** the stage-duration chart renders for a project whose stage-duration surface returns per-stage average and median
- **THEN** the chart SHALL expose an average lens and a median lens
- **AND** the operator SHALL be able to view stage durations under either lens

#### Scenario: Bar lengths follow the selected lens

- **WHEN** the operator selects the median lens after viewing the average lens
- **THEN** the bar lengths SHALL reflect the per-stage median durations
- **AND** the bar lengths SHALL NOT remain fixed on the average

### Requirement: A flow-efficiency ratio is surfaced alongside the bars

The stage-duration chart SHALL surface the flow-efficiency ratio (active-work time ÷ cycle time) returned by the stage-duration surface, rendered next to the bars so the operator sees at a glance what fraction of a delivered issue's cycle is actually working versus waiting. The ratio SHALL be sourced from the stage-duration surface; the widget SHALL NOT compute the ratio client-side from a partial sample.

#### Scenario: The flow-efficiency ratio is displayed next to the bars

- **WHEN** the stage-duration chart renders for a project whose stage-duration surface returns a defined flow-efficiency ratio
- **THEN** the chart SHALL display the flow-efficiency ratio next to the bars
- **AND** the displayed ratio SHALL be sourced from the stage-duration surface

#### Scenario: The ratio is not fabricated client-side when absent

- **WHEN** the stage-duration surface returns the empty result (no delivered issues in the window)
- **THEN** the widget SHALL NOT fabricate or infer a flow-efficiency ratio client-side
- **AND** the widget SHALL render the empty state per the three-state wrapper

### Requirement: A wait breakout surfaces approval-gate wait and inactive gaps separately

The stage-duration chart SHALL surface a wait breakout, separate from the bars, showing the average approval-gate wait per delivered issue and the average inactive-gap time per delivered issue, both sourced from the stage-duration surface. The breakout SHALL make *why* flow efficiency is what it is visible — how much of a typical issue's cycle is spent waiting on approvals versus sitting inactive between stages. Pending (`awaiting`) approvals SHALL NOT be presented as wait time in this chart; they are surfaced elsewhere as attention items, consistent with `approval-waiting-metrics`.

#### Scenario: The wait breakout shows average approval wait and average inactive gap

- **WHEN** the stage-duration chart renders for a project whose stage-duration surface returns a wait breakout
- **THEN** the chart SHALL display the average approval-gate wait per delivered issue and the average inactive-gap time per delivered issue
- **AND** both values SHALL be sourced from the stage-duration surface

#### Scenario: Pending approvals are not shown as wait time in this chart

- **WHEN** the project has `awaiting` approvals
- **THEN** the wait breakout SHALL NOT count those pending approvals as wait time
- **AND** those pending approvals SHALL continue to be surfaced as attention items rather than as stage-duration wait

### Requirement: Stage-duration chart renders loading, error, and empty states with a next action

The stage-duration chart SHALL render the loading state while the underlying stage-duration data is in flight, the error state when the underlying data fetch fails, and the empty state when the project has no delivered issues within the trailing window. The empty state SHALL name a concrete next action telling the operator how the chart will gain data — that stage durations appear once an issue completes on the project. The widget SHALL route these states through the shared dashboard chart three-state wrapper, and SHALL NOT render a bare empty coordinate axis or fabricated bars.

#### Scenario: Loading state renders while data is in flight

- **WHEN** the underlying stage-duration data for the chart is still loading
- **THEN** the widget SHALL render the loading state via the shared chart three-state wrapper
- **AND** the chart content SHALL NOT render until data has resolved

#### Scenario: Error state renders when the data fetch fails

- **WHEN** the underlying stage-duration fetch for the chart fails
- **THEN** the widget SHALL render the error state via the shared chart three-state wrapper
- **AND** the widget SHALL NOT render stale or fabricated chart content

#### Scenario: Empty state renders with a next action and no bare axis

- **WHEN** the project has no delivered issues within the trailing window
- **THEN** the widget SHALL render the empty state via the shared chart three-state wrapper
- **AND** the empty state SHALL name a concrete next action describing that the chart gains data once an issue completes on the project
- **AND** the widget SHALL NOT render a bare empty coordinate axis

### Requirement: Stage-duration chart composes against the dashboard chart baseline and is read-only

The stage-duration chart SHALL compose against the reusable dashboard chart baseline: the single pinned chart library, the theme-token color contract, the shared three-state wrapper, the accessibility wrapper (a screen-reader data summary describing the stages and their durations, and a legend that does not rely on color alone to distinguish the stage bars, the flow-efficiency ratio, and the wait-breakout components), and the numeric (`tabular-nums`) and motion (`transform`-based, `prefers-reduced-motion`-aware) conventions. The widget SHALL NOT introduce a new charting library dependency. The widget SHALL be purely read-only: it SHALL NOT mutate issue, session, workflow, or approval state, and SHALL NOT introduce any new backend write, event, or data collection beyond the additive stage-duration surface read.

#### Scenario: Widget uses the chart baseline components and conventions

- **WHEN** the stage-duration chart renders
- **THEN** the widget SHALL render through the single pinned chart library
- **AND** the widget SHALL source colors from theme tokens, route states through the shared chart three-state wrapper, expose the accessibility wrapper, and apply the tabular-nums and transform-based motion conventions
- **AND** the widget SHALL NOT introduce a new charting library dependency

#### Scenario: Accessibility wrapper distinguishes bars and wait components without color alone

- **WHEN** the stage-duration chart renders its screen-reader summary and legend
- **THEN** the legend SHALL disambiguate the stage bars, the flow-efficiency ratio, and the wait-breakout components by a channel other than color (label, shape, or pattern)
- **AND** a user who cannot perceive color SHALL be able to tell the stages and wait components apart

#### Scenario: Widget is read-only with respect to domain state

- **WHEN** a user views or refreshes the stage-duration chart
- **THEN** the widget SHALL NOT perform any write or mutation against issue, session, workflow, or approval domain state
- **AND** the widget SHALL NOT introduce any new backend write, event, or data collection beyond the additive stage-duration surface read