## ADDED Requirements

### Requirement: Session page displays context window usage bar

The session page SHALL display a context window usage bar showing current token usage relative to the context window size. The bar SHALL display both absolute values (e.g., "450K / 1M tokens") and a percentage. The usage bar SHALL update when new context usage data arrives via SSE or on page refresh.

#### Scenario: Context window usage is visible during active session
- **WHEN** a user views an active session page
- **AND** the session has context window data (contextWindowSize: 1000000, contextWindowUsed: 450000)
- **THEN** the page SHALL display "450K / 1M tokens (45%)"
- **AND** the usage bar SHALL be filled to 45%

#### Scenario: Context window usage updates during live session
- **WHEN** a user views a live session and new context usage data arrives via SSE
- **THEN** the usage bar SHALL update to reflect the new values
- **AND** the bar transition SHALL be smooth (not jumpy)

#### Scenario: Context window usage is visible in session history
- **WHEN** a user views a completed session from history
- **THEN** the final recorded context window usage SHALL be displayed
- **AND** intermediate usage values MAY be available in compaction event history

### Requirement: Color-coded health indicator reflects context usage thresholds

The context usage bar SHALL use color-coded states to indicate health: green when usage is below 60%, yellow when usage is between 60% and 80%, and red when usage exceeds 80%. The color coding SHALL be applied to the usage bar and any associated health indicator.

#### Scenario: Low usage shows green indicator
- **WHEN** context window usage is 45%
- **THEN** the usage bar SHALL be rendered in green
- **AND** no warning is displayed

#### Scenario: Moderate usage shows yellow indicator
- **WHEN** context window usage is 72%
- **THEN** the usage bar SHALL be rendered in yellow
- **AND** no warning banner is displayed (below 80%)

#### Scenario: High usage shows red indicator with warning
- **WHEN** context window usage is 85%
- **THEN** the usage bar SHALL be rendered in red
- **AND** a warning banner SHALL be displayed

### Requirement: Warning banner appears when context usage exceeds threshold

A warning banner SHALL appear on the session page when context window usage exceeds 80%. The banner SHALL inform the user that context is approaching its limit and suggest using Compact or Reset to recover headroom.

#### Scenario: Warning appears above 80% usage
- **WHEN** context window usage reaches 82%
- **THEN** a warning banner SHALL appear stating context is near the limit
- **AND** the banner SHALL suggest compact or reset as recovery options

#### Scenario: Warning is dismissed when usage drops below threshold
- **WHEN** context window usage drops to 75% after compaction
- **THEN** the warning banner SHALL be removed
- **AND** the usage bar color SHALL change from red to yellow

#### Scenario: Warning does not appear below 80%
- **WHEN** context window usage is 60%
- **THEN** no warning banner SHALL be displayed

### Requirement: Compaction event history is listed in session details

The session page SHALL list compaction events in the session timeline or details section. Each compaction entry SHALL show the time of compaction, the strategy used, and the token reduction achieved (before → after).

#### Scenario: Compaction events appear in session timeline
- **WHEN** a session has undergone 2 compaction events
- **THEN** the session timeline SHALL show 2 compaction entries
- **AND** each entry SHALL display timestamp, strategy, and token counts before/after

#### Scenario: Session with no compactions shows no compaction history
- **WHEN** a session has no compaction events
- **THEN** the compaction history section SHALL be empty or hidden
- **AND** no misleading "no compactions" message SHALL dominate the view

### Requirement: Session list shows context health indicators

Issue-level session lists and workflow session lists SHALL display a compact context health indicator for each session. The indicator SHALL use the same color-coded semantics (green/yellow/red) with at minimum a usage percentage.

#### Scenario: Session list entry shows health indicator
- **WHEN** a session list renders a session with 45% context usage
- **THEN** the session entry SHALL show a green health dot or bar with "45%"

#### Scenario: Session list entry shows warning for high usage
- **WHEN** a session list renders a session with 88% context usage
- **THEN** the session entry SHALL show a red health indicator with "88%"
- **AND** the entry MAY show a compact warning icon
