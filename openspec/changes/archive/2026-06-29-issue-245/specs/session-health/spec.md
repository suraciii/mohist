## ADDED Requirements

### Requirement: Context-health alert treatment on threshold breach

The shared `ContextHealthIndicator` SHALL render an explicit, proactive alert treatment — an alert color together with a descriptive tooltip — whenever context-window usage crosses a warning threshold, in every surface the indicator appears in: session list rows, Pulse compact cards, and the session page. The alert treatment SHALL be driven by the canonical traffic-light classification (yellow when usage is 60–79.99%, red when usage is 80% or above) and SHALL NOT require the user to open a session page to discover that context is running low. When usage is below the yellow threshold (green) the indicator SHALL remain quiet (no alert styling), and when no context-window data is available the indicator SHALL be hidden entirely rather than render a misleading empty or zero state.

#### Scenario: Yellow threshold triggers a yellow alert with a tooltip

- **WHEN** context-window usage resolves to a percentage between 60% and 79.99%
- **THEN** the `ContextHealthIndicator` SHALL render with the yellow alert color
- **AND** the indicator SHALL expose a descriptive tooltip stating the context usage

#### Scenario: Red threshold triggers a red alert with a tooltip

- **WHEN** context-window usage resolves to a percentage of 80% or above
- **THEN** the `ContextHealthIndicator` SHALL render with the red alert color
- **AND** the indicator SHALL expose a descriptive tooltip stating the context usage

#### Scenario: Healthy usage stays quiet

- **WHEN** context-window usage resolves to a percentage below 60%
- **THEN** the `ContextHealthIndicator` SHALL render without alert styling
- **AND** the indicator SHALL NOT present a warning color

#### Scenario: No context data hides the indicator

- **WHEN** context-window usage cannot be derived (missing data, non-positive window size, or non-finite values)
- **THEN** the `ContextHealthIndicator` SHALL render nothing
- **AND** the surface SHALL NOT display a misleading empty or zero-usage indicator

#### Scenario: Alert treatment is consistent across every surface

- **WHEN** the same usage percentage is rendered in a session list row, a Pulse compact card, and the session page
- **THEN** the alert color and tooltip behavior SHALL be identical across all three surfaces

### Requirement: Compaction lineage link between runtime sessions

The system SHALL expose an explicit, navigable relationship between a runtime session and the runtime sessions adjacent to it in a compaction or reset lineage: the successor runtime session it was compacted/reset into (the `NewAgentSessionId` rebind) and, conversely, the predecessor runtime session it was produced from. This lineage SHALL be surfaced as a navigable link in the session UI rather than remaining an invisible implementation detail, so a user can move between the pre- and post-compaction runtime sessions of the same issue. Activating a lineage link SHALL navigate to the linked runtime session.

#### Scenario: Session that produced a successor shows a link to it

- **WHEN** a runtime session has been compacted or reset into a successor runtime session
- **THEN** the session UI SHALL render a lineage link to the successor runtime session
- **AND** activating the link SHALL navigate to that successor runtime session

#### Scenario: Session produced from a predecessor shows a link to it

- **WHEN** a runtime session was produced as the result of a prior session's compaction or reset
- **THEN** the session UI SHALL render a lineage link back to the predecessor runtime session
- **AND** activating the link SHALL navigate to that predecessor runtime session

#### Scenario: No lineage link when no compaction relationship exists

- **WHEN** a runtime session has neither a compaction predecessor nor a successor
- **THEN** the session UI SHALL NOT render a lineage link

### Requirement: Compaction timeline compact summary

Compaction events SHALL be visible in a compact summary without requiring the user to expand individual transcript rounds. The compact summary SHALL surface the compaction events that occurred during the session so a user can see at a glance that and when context was compacted, while the per-round `CompactionTimelineEntry` SHALL remain available for the detailed before/after token counts and summary.

#### Scenario: Compaction events are visible without expanding a round

- **WHEN** a session has one or more recorded compaction events
- **THEN** the session UI SHALL render a compact summary of those events that is visible without expanding any individual transcript round

#### Scenario: Per-round detail entry remains available

- **WHEN** a transcript round contains a compaction event
- **THEN** the per-round `CompactionTimelineEntry` SHALL remain available to present the detailed before/after token counts and optional summary

### Requirement: Context-usage trend mini-chart

Compact cards SHALL render a small context-usage trend chart over the session lifetime, derived from a short usage history retained for the session, rather than rendering only the latest usage snapshot. The trend mini-chart SHALL degrade gracefully when the usage history is empty or insufficient to plot (for example by hiding the chart) rather than rendering a broken or empty-axis chart.

#### Scenario: Trend mini-chart renders from usage history

- **WHEN** a session has a non-empty usage history available
- **THEN** the compact card SHALL render a context-usage trend mini-chart derived from that history

#### Scenario: Trend mini-chart degrades gracefully without history

- **WHEN** a session has no usage history or insufficient history to plot a trend
- **THEN** the compact card SHALL degrade gracefully
- **AND** the card SHALL NOT render a broken or empty-axis trend chart
