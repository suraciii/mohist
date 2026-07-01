### Requirement: Cycle-time scatter control chart renders one point per delivered issue in the Productivity zone

The Dashboard `Productivity` zone SHALL render a cycle-time scatter control chart: one scatter point per delivered issue (an issue that reached the terminal `done` state) across a fixed trailing window, with each point's x-position encoding that issue's completion date and its y-position encoding the issue's delivery duration in days. The per-issue completion date and duration SHALL be sourced from the project delivery-time surface; the widget SHALL NOT introduce a second data-collection path. The trailing window SHALL be fixed and SHALL NOT be user-configurable.

#### Scenario: One point renders per delivered issue, positioned by completion date and duration

- **WHEN** the Productivity zone renders the cycle-time scatter chart for a project whose delivery-time surface returns delivered issues within the trailing window
- **THEN** the chart SHALL render one scatter point per delivered issue
- **AND** each point's x-position SHALL encode that issue's completion date
- **AND** each point's y-position SHALL encode that issue's delivery duration in days

#### Scenario: Point values come from the delivery-time surface, not a new collection

- **WHEN** the cycle-time scatter chart renders
- **THEN** the per-issue completion date and duration SHALL be sourced from the project delivery-time surface
- **AND** the widget SHALL NOT introduce a new data-collection path

#### Scenario: The trailing window is fixed and not configurable

- **WHEN** the cycle-time scatter chart renders
- **THEN** the trailing window SHALL span a single fixed length
- **AND** the window length SHALL NOT be configurable by the user

### Requirement: The chart exposes both a lead-time lens and a cycle-time lens

The cycle-time scatter chart SHALL expose two duration lenses over the same delivered-issue population: a **lead-time** lens (created → completed) and a **cycle-time** lens (first work-started → completed). Both lenses SHALL be available on the chart so the operator can separate queue/wait from active work. The scatter points' y-values and the overlaid percentile lines SHALL reflect the currently selected lens. An issue whose cycle time is undefined (no recorded work-start) SHALL be excluded from the cycle-time lens, while its lead time SHALL remain visible under the lead-time lens.

#### Scenario: Both lenses are available on the chart

- **WHEN** the cycle-time scatter chart renders for a project that has delivered issues
- **THEN** the chart SHALL expose a lead-time lens and a cycle-time lens
- **AND** the operator SHALL be able to view delivered-issue durations under either lens

#### Scenario: Point positions and percentile lines reflect the selected lens

- **WHEN** the operator selects the cycle-time lens, then switches to the lead-time lens
- **THEN** the scatter points' y-values SHALL reflect the durations of the selected lens
- **AND** the overlaid percentile lines SHALL reflect the durations of the selected lens

#### Scenario: Issues without a work-start are excluded from the cycle-time lens only

- **WHEN** the delivery-time surface returns a delivered issue whose cycle time is undefined
- **THEN** that issue SHALL be excluded from the cycle-time lens
- **AND** that issue's lead time SHALL remain visible under the lead-time lens

### Requirement: Rolling P50 and P85 percentile lines overlay the scatter

The cycle-time scatter chart SHALL overlay two rolling percentile lines across the delivered-issue series ordered by completion date: a **P50 (median)** line expressing how fast most issues complete, and a **P85** line expressing tail dispersion. For each position along the completion-date axis, each percentile line's value SHALL be the median (P50) or the 85th percentile (P85) of the durations of the delivered issues within a fixed trailing rolling window ending at that position. The rolling window SHALL be fixed and SHALL NOT be user-configurable. The percentile lines SHALL be computed over the durations of the currently selected lens, excluding issues whose duration for that lens is undefined. The percentile computation SHALL be derived client-side from the per-issue series returned by the delivery-time surface; the widget SHALL NOT require a new backend percentile computation.

#### Scenario: P50 and P85 lines plot rolling statistics over the delivered-issue series

- **WHEN** the scatter chart renders the percentile overlays for a series of delivered issues ordered by completion date
- **THEN** the chart SHALL overlay a P50 (median) line and a P85 line
- **AND** each line's value at a position SHALL be the median or 85th percentile of the durations within the fixed trailing rolling window ending at that position

#### Scenario: Percentile lines follow the selected lens

- **WHEN** the operator switches between the cycle-time lens and the lead-time lens
- **THEN** the P50 and P85 lines SHALL be recomputed over the durations of the selected lens
- **AND** issues whose duration is undefined for the selected lens SHALL be excluded from the percentile computation

#### Scenario: Positions near the start of the series with fewer samples still plot

- **WHEN** a position along the completion-date axis has fewer prior issues than the rolling window size (near the start of the trailing window)
- **THEN** the percentile lines SHALL plot the median and 85th percentile over the available issues up to and including that position
- **AND** the lines SHALL NOT be omitted solely because the rolling window is not yet full

#### Scenario: Percentiles are computed client-side from the per-issue series

- **WHEN** the percentile overlays render
- **THEN** the P50 and P85 values SHALL be derived client-side from the per-issue series returned by the delivery-time surface
- **AND** the widget SHALL NOT require a new backend percentile computation

### Requirement: Cycle-time chart renders loading, error, and empty states with a next action

The cycle-time scatter chart SHALL render the loading state while the underlying delivery-time data is in flight, the error state when the underlying data fetch fails, and the empty state when the project has no delivered issues within the trailing window. The empty state SHALL name a concrete next action telling the operator how the chart will gain data — that cycle time appears once an issue completes on the project. The widget SHALL route these states through the shared dashboard chart three-state wrapper, and SHALL NOT render a bare empty coordinate axis.

#### Scenario: Loading state renders while data is in flight

- **WHEN** the underlying delivery-time data for the scatter chart is still loading
- **THEN** the widget SHALL render the loading state via the shared chart three-state wrapper
- **AND** the chart content SHALL NOT render until data has resolved

#### Scenario: Error state renders when the data fetch fails

- **WHEN** the underlying delivery-time fetch for the scatter chart fails
- **THEN** the widget SHALL render the error state via the shared chart three-state wrapper
- **AND** the widget SHALL NOT render stale or fabricated chart content

#### Scenario: Empty state renders with a next action and no bare axis

- **WHEN** the project has no delivered issues within the trailing window
- **THEN** the widget SHALL render the empty state via the shared chart three-state wrapper
- **AND** the empty state SHALL name a concrete next action describing that the chart gains data once an issue completes on the project
- **AND** the widget SHALL NOT render a bare empty coordinate axis

### Requirement: Scatter points are positioned by completion-event time, not by issue edit time

The scatter chart's per-point x-position SHALL be driven by the issue's persisted completion time (the terminal `done` moment), sourced from the delivery-time surface. A post-completion edit to an issue (comment, label, title, or any update that bumps `updatedAt` after the issue has already been delivered) SHALL NOT move, add, or resurface a point. A reopened and re-completed issue SHALL position at the latest terminal `done` moment, consistent with the completion-time rule, and SHALL NOT also retain a point at the prior completion.

#### Scenario: A post-completion edit does not move the point

- **WHEN** a delivered issue completed on a prior day is edited on the current day in a way that bumps its `updatedAt`
- **THEN** the chart SHALL NOT move, add, or resurface a point for the current day as a result of that edit
- **AND** the issue's point SHALL remain positioned at its completion date

#### Scenario: A reopen and re-completion positions at the latest completion

- **WHEN** a delivered issue first reached `done`, was reopened, then reached `done` again on a new day
- **THEN** the chart SHALL position the issue's point at the latest terminal `done` moment
- **AND** the chart SHALL NOT also retain a point at the prior completion

### Requirement: Cycle-time chart composes against the dashboard chart baseline and is read-only

The cycle-time scatter chart SHALL compose against the reusable dashboard chart baseline: the single pinned chart library, the theme-token color contract, the shared three-state wrapper, the accessibility wrapper (a screen-reader data summary, and a legend that does not rely on color alone to distinguish the scatter series, the P50 line, the P85 line, and the lead-time vs cycle-time lenses), and the numeric (`tabular-nums`) and motion (`transform`-based, `prefers-reduced-motion`-aware) conventions. The widget SHALL NOT introduce a new charting library dependency. The widget SHALL be purely read-only: it SHALL NOT mutate issue, session, workflow, or approval state, and SHALL NOT introduce any new backend write, event, or data collection beyond the additive delivery-time surface read.

#### Scenario: Widget uses the chart baseline components and conventions

- **WHEN** the cycle-time scatter chart renders
- **THEN** the widget SHALL render through the single pinned chart library
- **AND** the widget SHALL source colors from theme tokens, route states through the shared chart three-state wrapper, expose the accessibility wrapper, and apply the tabular-nums and transform-based motion conventions
- **AND** the widget SHALL NOT introduce a new charting library dependency

#### Scenario: Legend distinguishes scatter series and percentile lines without color alone

- **WHEN** the scatter chart renders its legend
- **THEN** the legend SHALL disambiguate the scatter series, the P50 line, and the P85 line by a channel other than color (label, shape, or pattern)
- **AND** a user who cannot perceive color SHALL be able to tell the series apart from the legend

#### Scenario: Widget is read-only with respect to domain state

- **WHEN** a user views or refreshes the cycle-time scatter chart
- **THEN** the widget SHALL NOT perform any write or mutation against issue, session, workflow, or approval domain state
- **AND** the widget SHALL NOT introduce any new backend write, event, or data collection beyond the additive delivery-time surface read
