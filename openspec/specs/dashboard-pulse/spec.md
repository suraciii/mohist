### Requirement: Pulse zone renders runner capacity and live status counts

The `dashboard-pulse` zone content SHALL surface runner slot usage as an active/max capacity indicator and SHALL render live status counts as pills for the four lifecycle states — `active`, `waiting`, `completed`, and `failed`. The slot-usage indicator's active/max values SHALL be derived from the unified `runner-capacity` projection (runner grain runtime active workflow works for `active`, persisted runner definition slots for `max`), and SHALL NOT be computed locally from the active-session-card count versus a runner-count-plus-one heuristic. These indicators SHALL render atop the zone regardless of whether any candidate cards exist.

#### Scenario: Slot usage and status pills render

- **WHEN** the Pulse zone renders with runner activity data
- **THEN** the zone SHALL render a slot-usage indicator showing active/max slots used
- **AND** the active/max values SHALL be sourced from the `runner-capacity` projection
- **AND** the zone SHALL render status pills for the `active`, `waiting`, `completed`, and `failed` counts

#### Scenario: Capacity header renders even with no active sessions

- **WHEN** the Pulse zone renders with no active in-flight sessions
- **THEN** the zone SHALL still render the slot-usage indicator and the four status pills atop the zone

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

### Requirement: Pulse zone caps visible cards and links overflow to the Activity page

The Pulse zone SHALL cap the number of visible compact candidate cards at a fixed constant (4). When the number of active in-flight sessions exceeds the cap, the zone SHALL render an overflow link to the Activity page (`/activity`) that conveys the remaining count. The cap SHALL NOT be user-configurable.

#### Scenario: Cards beyond the cap are summarized by an overflow link

- **WHEN** the number of active in-flight sessions exceeds the fixed cap
- **THEN** the zone SHALL render only the fixed-cap number of cards
- **AND** the zone SHALL render an overflow link to the Activity page indicating the remaining count

#### Scenario: No overflow link when within the cap

- **WHEN** the number of active in-flight sessions is at or below the fixed cap
- **THEN** the zone SHALL NOT render an overflow link

### Requirement: Pulse zone renders empty state when no active sessions exist

When there are no active in-flight sessions, the Pulse zone SHALL render an empty-state message ("No active sessions") in place of the card list. The empty state SHALL NOT render the overflow link.

#### Scenario: No active sessions shows empty state

- **WHEN** the Pulse zone data has resolved and there are no active in-flight sessions
- **THEN** the zone SHALL render an empty-state message
- **AND** the zone SHALL NOT render a card list or an overflow link

### Requirement: Pulse zone derives content exclusively from existing live read-only sources

The Pulse zone SHALL derive its content from the existing live frontend activity sources — the agent activity feed consumed via `useActivityCards`. The zone SHALL NOT mutate issue, session, or event domain state and SHALL NOT add write operations. To supply the context-usage trend mini-chart, the live activity source MAY be enriched to carry a short context-usage history for a session (the current source exposes only the latest snapshot); this is the only permitted relaxation of the "no new endpoint" constraint and SHALL NOT introduce an independent new query endpoint beyond enriching the existing live activity feed. The Pulse zone SHALL remain purely read-only with respect to domain state.

#### Scenario: No independent new backend endpoint is introduced

- **WHEN** the Pulse zone renders and refreshes its data, including the trend mini-chart
- **THEN** the zone SHALL consume only the existing live agent activity source
- **AND** no independent new backend API endpoint SHALL be added to support the Pulse zone beyond enriching the existing live activity feed with usage history

#### Scenario: Pulse is read-only with respect to domain state

- **WHEN** a user views the Pulse zone
- **THEN** the zone SHALL NOT perform any write or mutation against issue, activity, or session domain state
- **AND** the zone SHALL be purely a read-only composition over existing live data

### Requirement: Pulse zone excludes ETA prediction and activity ticker

The Pulse zone SHALL NOT render estimated time-to-completion (ETA) predictions for sessions and SHALL NOT render a scrolling or auto-advancing activity ticker. These are explicitly excluded as false-value signals given the high variance of LLM task durations and the noise introduced by a ticker.

#### Scenario: No ETA prediction is rendered

- **WHEN** the Pulse zone renders a compact candidate card
- **THEN** the card SHALL NOT display an estimated completion time

#### Scenario: No activity ticker is rendered

- **WHEN** the Pulse zone renders
- **THEN** the zone SHALL NOT render a scrolling or auto-advancing activity ticker
