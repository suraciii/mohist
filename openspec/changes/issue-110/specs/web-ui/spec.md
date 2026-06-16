## ADDED Requirements

### Requirement: Session page renders context health indicator bar

The session page SHALL render a context window health indicator bar. The bar SHALL be positioned prominently in the session page header or metadata area. It SHALL display usage as "used / total tokens (percent%)" with color coding: green (<60%), yellow (60-80%), red (>80%).

#### Scenario: Health indicator in session page header
- **WHEN** a user views a session detail page
- **AND** the session has context metrics (500K used / 1M total, 50%)
- **THEN** the health indicator SHALL display "500K / 1M tokens (50%)" with a green bar
- **AND** the indicator SHALL be visible in the session page metadata area

#### Scenario: Health indicator updates during live session
- **WHEN** a `context_health_update` SSE event arrives during a live session
- **THEN** the health indicator SHALL update its display values and color in real time

#### Scenario: Health indicator shows red for critical usage
- **WHEN** context usage reaches 88%
- **THEN** the health indicator SHALL render in red
- **AND** the text SHALL be clearly visible against the red indicator background

### Requirement: Session page shows warning banner at high context usage

The session page SHALL display a warning banner above the session transcript when context usage exceeds 80%. The banner SHALL inform the user that context is nearing capacity and SHALL link to Compact and Reset actions.

#### Scenario: Warning banner appears at high usage
- **WHEN** a user views a session with context usage at 85%
- **THEN** a warning banner SHALL appear above the transcript
- **AND** the banner SHALL contain text indicating context is near capacity
- **AND** the banner SHALL include actionable links/buttons for Compact and Reset

#### Scenario: Warning banner hides after recovery
- **WHEN** context usage drops below 80% after compaction
- **AND** a `context_health_update` SSE event arrives
- **THEN** the warning banner SHALL be dismissed

### Requirement: Session page provides Compact and Reset action buttons

The session page SHALL render Compact and Reset buttons as part of the session action controls. Both buttons SHALL be disabled when the session is actively running. Both buttons SHALL have clear labels and tooltips explaining their function.

#### Scenario: Compact and Reset buttons visible on session page
- **WHEN** a user views a session detail page
- **THEN** both Compact and Reset buttons SHALL be visible in the session action area
- **AND** each button SHALL have a descriptive tooltip

#### Scenario: Buttons disabled during active session
- **WHEN** the session status is `running`
- **THEN** both Compact and Reset buttons SHALL be disabled
- **AND** tooltips SHALL indicate "Unavailable while session is active"

#### Scenario: Buttons enabled for inactive sessions
- **WHEN** the session status is `completed`, `failed`, `cancelled`, or `probing`
- **THEN** both Compact and Reset buttons SHALL be enabled
- **AND** clicking Compact SHALL trigger the compact API call
- **AND** clicking Reset SHALL open the confirmation dialog

### Requirement: Reset action shows confirmation dialog

The Reset button SHALL open a confirmation dialog before executing. The dialog SHALL warn that all session context will be erased and the conversation history will be lost. The dialog SHALL have Cancel and "Reset Session" confirm actions.

#### Scenario: Confirmation dialog for Reset
- **WHEN** a user clicks the Reset button on an inactive session
- **THEN** a modal dialog SHALL appear with the warning "This will clear all session context. The agent will lose all conversation history."
- **AND** the dialog SHALL have a "Cancel" button and a "Reset Session" button

#### Scenario: Reset confirmed executes the action
- **WHEN** a user clicks "Reset Session" in the confirmation dialog
- **THEN** the dialog SHALL close
- **AND** the reset API call SHALL be sent
- **AND** the page SHALL refresh to show the cleared session state

#### Scenario: Reset cancelled does nothing
- **WHEN** a user clicks "Cancel" in the confirmation dialog
- **THEN** the dialog SHALL close
- **AND** no API call SHALL be made
- **AND** the session SHALL remain unchanged

### Requirement: Session list entries show compact context health indicators

Session list views (on issue detail and workflow views) SHALL render a compact context health indicator per session. The indicator SHALL be a small colored dot or bar with a percentage label. Color coding SHALL follow the same green/yellow/red semantics.

#### Scenario: Session list shows health indicators
- **WHEN** a session list renders entries for sessions with varying context usage
- **THEN** each entry SHALL show a color-coded health indicator
- **AND** the indicator SHALL show the usage percentage

#### Scenario: Session without context data hides indicator
- **WHEN** a session has no context usage data (contextWindowSize is 0)
- **THEN** the session list entry SHALL NOT show a context health indicator
- **AND** the entry SHALL not show misleading "0%" text
