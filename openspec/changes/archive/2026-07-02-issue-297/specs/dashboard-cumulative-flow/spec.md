## ADDED Requirements

### Requirement: A stacked-area cumulative flow diagram renders one band per workflow stage in the Productivity zone

The Dashboard `Productivity` zone SHALL render a stacked-area cumulative flow diagram (CFD) over a fixed trailing window: the x-axis SHALL be date, the y-axis SHALL be issue count, and the chart SHALL render one stacked color band per workflow stage (`backlog`, `plan`, `build`, `check`, `integrate`, `done`) in workflow stage order. Each band's width at a given day SHALL encode that stage's attributed issue count on that day. The stacked top edge's slope SHALL read as throughput, and a band bulge SHALL read as a stage bottleneck. The per-band per-day values SHALL be sourced from the project stage-population snapshot series; the widget SHALL NOT introduce a second data-collection path or recompute per-day populations from the event stream. The trailing window SHALL be fixed and SHALL NOT be user-configurable.

#### Scenario: One stacked band renders per stage, width from that stage's daily count

- **WHEN** the Productivity zone renders the CFD for a project whose stage-population snapshot series has snapshots across the trailing window
- **THEN** the chart SHALL render one stacked band per workflow stage in workflow stage order
- **AND** each band's width at a day SHALL encode that stage's attributed issue count on that day
- **AND** the x-axis SHALL encode date and the y-axis SHALL encode the stacked issue count

#### Scenario: Band values come from the snapshot series, not a new collection

- **WHEN** the CFD renders
- **THEN** the per-stage per-day band values SHALL be sourced from the project stage-population snapshot series
- **AND** the widget SHALL NOT introduce a new data-collection path or recompute per-day populations from the event stream

#### Scenario: The trailing window is fixed and not configurable

- **WHEN** the CFD renders
- **THEN** the trailing window SHALL span a single fixed length
- **AND** the window length SHALL NOT be configurable by the user

### Requirement: The CFD renders loading, error, and empty states with a next action

The CFD widget SHALL render the loading state while the underlying snapshot series is in flight, the error state when the underlying fetch fails, and the empty state when no daily snapshot has landed yet within the trailing window. The empty state SHALL name a concrete next action telling the operator how the chart will gain data — that the CFD gains history once the first daily stage-population snapshot lands. The widget SHALL route these states through the shared dashboard chart three-state wrapper, and SHALL NOT render a bare empty coordinate axis or fabricated bands.

#### Scenario: Loading state renders while data is in flight

- **WHEN** the underlying snapshot series for the CFD is still loading
- **THEN** the widget SHALL render the loading state via the shared chart three-state wrapper
- **AND** the chart content SHALL NOT render until data has resolved

#### Scenario: Error state renders when the data fetch fails

- **WHEN** the underlying snapshot-series fetch for the CFD fails
- **THEN** the widget SHALL render the error state via the shared chart three-state wrapper
- **AND** the widget SHALL NOT render stale or fabricated chart content

#### Scenario: Empty state renders with a next action and no bare axis

- **WHEN** no daily stage-population snapshot has landed yet within the trailing window
- **THEN** the widget SHALL render the empty state via the shared chart three-state wrapper
- **AND** the empty state SHALL name a concrete next action describing that the CFD gains history once the first daily snapshot lands
- **AND** the widget SHALL NOT render a bare empty coordinate axis or fabricated bands

### Requirement: The CFD composes against the dashboard chart baseline and is read-only

The CFD widget SHALL compose against the reusable dashboard chart baseline: the single pinned chart library, the theme-token color contract, the shared three-state wrapper, the accessibility wrapper (a screen-reader data summary describing the stages, the time range, and the salient per-stage populations, and a legend that does not rely on color alone to distinguish the six stage bands), and the numeric (`tabular-nums`) and motion (`transform`-based, `prefers-reduced-motion`-aware) conventions. The widget SHALL NOT introduce a new charting library dependency. The widget SHALL be purely read-only: it SHALL NOT mutate issue, session, workflow, or approval state, and SHALL NOT introduce any new backend write, event, or data collection beyond the additive stage-population snapshot series read.

#### Scenario: Widget uses the chart baseline components and conventions

- **WHEN** the CFD widget renders
- **THEN** the widget SHALL render through the single pinned chart library
- **AND** the widget SHALL source colors from theme tokens, route states through the shared chart three-state wrapper, expose the accessibility wrapper, and apply the tabular-nums and transform-based motion conventions
- **AND** the widget SHALL NOT introduce a new charting library dependency

#### Scenario: Stage bands are distinguishable without color alone

- **WHEN** the CFD renders its screen-reader summary and legend
- **THEN** the legend SHALL disambiguate the six stage bands by a channel other than color (label, shape, or pattern)
- **AND** a user who cannot perceive color SHALL be able to tell the stage bands apart from the legend

#### Scenario: Widget is read-only with respect to domain state

- **WHEN** a user views or refreshes the CFD widget
- **THEN** the widget SHALL NOT perform any write or mutation against issue, session, workflow, or approval domain state
- **AND** the widget SHALL NOT introduce any new backend write, event, or data collection beyond the additive stage-population snapshot series read
