## MODIFIED Requirements

### Requirement: Pulse zone renders real-time compact candidate cards for in-flight sessions

The Pulse zone SHALL render one compact candidate card per active in-flight session, derived in real time from live activity data. Each card SHALL display the issue number, a stage badge reflecting the session's current workflow stage, and the session title. Each card SHALL display token and cost usage when those values are available, a task progress bar when task progress exists, and a context-health indicator when context-window data exists. The context-health indicator SHALL render the context-health alert treatment (alert color and descriptive tooltip) when usage crosses the yellow/red thresholds, consistent with the shared `ContextHealthIndicator` contract. Each card SHALL additionally render a context-usage trend mini-chart over the session lifetime, derived from a short usage history, rather than only the latest usage snapshot; the mini-chart SHALL degrade gracefully when the history is empty or insufficient to plot. Activating a card SHALL navigate to that card's corresponding issue detail view.

#### Scenario: Card renders session signals

- **WHEN** an active in-flight session exists
- **THEN** the Pulse zone SHALL render a compact candidate card for it
- **AND** the card SHALL display the issue number, a stage badge, and the session title
- **AND** the card SHALL display token and cost usage when those values are available
- **AND** the card SHALL display a task progress bar when task progress exists
- **AND** the card SHALL display a context-health indicator when context-window data exists

#### Scenario: Card renders the context-health alert treatment on threshold breach

- **WHEN** a card's context-window usage crosses the yellow or red threshold
- **THEN** the card SHALL render the context-health alert treatment (alert color and descriptive tooltip)
- **AND** the treatment SHALL be consistent with the shared `ContextHealthIndicator` contract used in other surfaces

#### Scenario: Card renders a context-usage trend mini-chart

- **WHEN** an active in-flight session has a non-empty usage history available
- **THEN** the card SHALL render a context-usage trend mini-chart over the session lifetime derived from that history

#### Scenario: Card degrades gracefully without usage history

- **WHEN** an active in-flight session has no usage history or insufficient history to plot a trend
- **THEN** the card SHALL degrade gracefully
- **AND** the card SHALL NOT render a broken or empty-axis trend chart

#### Scenario: Activating a card navigates to issue detail

- **WHEN** a user activates a compact candidate card
- **THEN** the application SHALL navigate to that card's issue detail view
- **AND** the issue detail SHALL correspond to the issue represented by the card

### Requirement: Pulse zone derives content exclusively from existing live read-only sources

The Pulse zone SHALL derive its content from the existing live frontend activity sources — the agent activity feed consumed via `useActivityCards`. The zone SHALL NOT mutate issue, session, or event domain state and SHALL NOT add write operations. To supply the context-usage trend mini-chart, the live activity source MAY be enriched to carry a short context-usage history for a session (the current source exposes only the latest snapshot); this is the only permitted relaxation of the "no new endpoint" constraint and SHALL NOT introduce an independent new query endpoint beyond enriching the existing live activity feed. The Pulse zone SHALL remain purely read-only with respect to domain state.

#### Scenario: No independent new backend endpoint is introduced

- **WHEN** the Pulse zone renders and refreshes its data, including the trend mini-chart
- **THEN** the zone SHALL consume the existing live agent activity source
- **AND** no independent new backend API endpoint SHALL be added to support the Pulse zone beyond enriching the existing live activity feed with usage history

#### Scenario: Pulse is read-only with respect to domain state

- **WHEN** a user views the Pulse zone
- **THEN** the zone SHALL NOT perform any write or mutation against issue, activity, or session domain state
- **AND** the zone SHALL be purely a read-only composition over existing live data
