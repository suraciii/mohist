### Requirement: FTR trend line renders one point per trailing time bucket in the Productivity zone

The Dashboard `Productivity` zone SHALL render a first-time-right (FTR) trend line: one point per trailing time bucket across the trailing window the project AI-quality surface already evaluates, with each point's value encoding that bucket's FTR percentage (first-time-right shipped issues ÷ all shipped issues within the bucket). The per-bucket FTR values SHALL be sourced from the per-bucket FTR series the project AI-quality surface exposes; the FTR-trend widget SHALL NOT introduce a second quality computation path. The existing `QualityPanel` 7-day / 30-day single-point scalar figures SHALL remain; the trend line is an addition to the zone, not a replacement.

#### Scenario: One point renders per trailing time bucket, value from that bucket's FTR rate

- **WHEN** the Productivity zone renders the FTR trend line for a project whose AI-quality surface has shipped issues across the trailing window
- **THEN** the line SHALL render one point per trailing time bucket
- **AND** each point's value SHALL encode that bucket's FTR percentage sourced from the per-bucket FTR series

#### Scenario: A bucket with no shipped issues renders no numeric FTR point

- **WHEN** a time bucket within the trailing window contains no issues that reached `Done`
- **THEN** the trend line SHALL NOT plot a numeric FTR value (zero or otherwise) for that bucket
- **AND** the bucket SHALL NOT be reported as a perfect FTR of one or a worst-case of zero

#### Scenario: FTR values come from the existing quality surface, not a new computation

- **WHEN** the FTR trend line renders
- **THEN** the per-bucket FTR values SHALL be sourced from the per-bucket FTR series exposed by the AI-quality surface
- **AND** the widget SHALL NOT introduce a new quality computation path for the FTR rate

### Requirement: An optional rework-rate series overlays the FTR trend line

The FTR trend line SHALL support an optional rework-rate overlay rendered on the same percentage axis, so first-time-right and rework can be read together over time. For each time bucket, the overlay SHALL plot that bucket's rework rate sourced from the per-bucket rework series the project AI-quality surface exposes; the widget SHALL NOT compute the rework rate client-side from the FTR line. The overlay SHALL be optional: the operator SHALL be able to view the FTR line alone or with the rework overlay. A bucket with no shipped issues SHALL plot no numeric point for either series.

#### Scenario: Overlay plots the per-bucket rework rate on the same axis as FTR

- **WHEN** the rework overlay is shown for a project whose AI-quality surface has shipped issues across the trailing window
- **THEN** the overlay SHALL plot, for each time bucket, that bucket's rework rate on the same percentage axis as the FTR line
- **AND** the rework rate SHALL be sourced from the per-bucket rework series, not derived client-side from the FTR line

#### Scenario: Overlay is optional and toggleable

- **WHEN** the operator views the FTR trend line
- **THEN** the operator SHALL be able to view the FTR line alone
- **AND** the operator SHALL be able to enable the rework overlay on the same axis

#### Scenario: A bucket with no shipped issues plots no numeric point for either series

- **WHEN** a time bucket within the trailing window contains no issues that reached `Done`
- **THEN** the FTR line SHALL plot no numeric point for that bucket
- **AND** the rework overlay SHALL plot no numeric point for that bucket

### Requirement: FTR-trend widget renders loading, error, and empty states with a next action

The FTR-trend widget SHALL render the loading state while the underlying quality data is in flight, the error state when the underlying data fetch fails, and the empty state when the trailing window contains no shipped issues. The empty state SHALL name a concrete next action telling the operator how the chart will gain data — that the line gains data once an issue ships within the window. The widget SHALL route these states through the shared dashboard chart three-state wrapper.

#### Scenario: Loading state renders while data is in flight

- **WHEN** the underlying quality data for the FTR trend is still loading
- **THEN** the widget SHALL render the loading state via the shared chart three-state wrapper
- **AND** the chart content SHALL NOT render until data has resolved

#### Scenario: Error state renders when the data fetch fails

- **WHEN** the underlying quality fetch for the FTR trend fails
- **THEN** the widget SHALL render the error state via the shared chart three-state wrapper
- **AND** the widget SHALL NOT render stale or fabricated chart content

#### Scenario: Empty state renders with a next action when no shipped issues are in the window

- **WHEN** the trailing window contains no issues that reached `Done`
- **THEN** the widget SHALL render the empty state via the shared chart three-state wrapper
- **AND** the empty state SHALL name a concrete next action describing that the line gains data once an issue ships within the window

### Requirement: FTR-trend widget composes against the dashboard chart baseline and is read-only

The FTR-trend widget SHALL compose against the reusable dashboard chart baseline: the single pinned chart library, the theme-token color contract, the shared three-state wrapper, the accessibility wrapper (a screen-reader data summary and a legend that does not rely on color alone to distinguish the FTR series from the rework series), and the numeric (`tabular-nums`) and motion (`transform`-based, `prefers-reduced-motion`-aware) conventions. The widget SHALL be purely read-only: it SHALL NOT mutate issue, session, or workflow state, and SHALL NOT introduce any new backend write, event, or data collection beyond the additive per-bucket series read.

#### Scenario: Widget uses the chart baseline components and conventions

- **WHEN** the FTR-trend widget renders its chart
- **THEN** the widget SHALL render through the single pinned chart library
- **AND** the widget SHALL source colors from theme tokens, route states through the shared chart three-state wrapper, expose the accessibility wrapper, and apply the tabular-nums and transform-based motion conventions

#### Scenario: Legend distinguishes the FTR series from the rework series without color alone

- **WHEN** the FTR-trend widget renders the rework overlay alongside the FTR line
- **THEN** the legend SHALL disambiguate the FTR series from the rework series by a channel other than color (label, shape, or pattern)
- **AND** a user who cannot perceive color SHALL be able to tell the two series apart from the legend

#### Scenario: Widget is read-only with respect to domain state

- **WHEN** a user views or refreshes the FTR-trend widget
- **THEN** the widget SHALL NOT perform any write or mutation against issue, session, or workflow domain state
- **AND** the widget SHALL NOT introduce any new backend write, event, or data collection beyond the additive per-bucket series read
