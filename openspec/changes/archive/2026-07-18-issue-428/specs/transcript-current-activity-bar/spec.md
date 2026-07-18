### Requirement: A persistent current-activity bar renders while the session is alive and a tool call is in progress

While the hosting session is alive (running) and at least one tool call is in the in-progress state (`pending` or `running`), a current-activity status bar SHALL render pinned to the bottom of the transcript viewport. The bar SHALL remain visible at every transcript scroll position. The bar SHALL display the title of the currently-executing tool call together with its live elapsed duration, where the duration ticks once per second under the same liveness and formatting rules as the in-progress tool row's duration.

#### Scenario: Bar renders while a tool call is in progress in a live session
- **WHEN** the session is running and a tool call with state `pending` or `running` exists in the transcript
- **THEN** the current-activity bar SHALL render at the bottom of the transcript viewport
- **AND** the bar SHALL display the active tool call's title and live elapsed duration

#### Scenario: Bar remains visible across scroll positions
- **WHEN** the transcript is scrolled away from the bottom while a tool call is in progress in a live session
- **THEN** the current-activity bar SHALL remain visible within the transcript viewport

#### Scenario: Bar duration ticks once per second
- **WHEN** the current-activity bar is visible
- **THEN** the elapsed duration displayed in the bar SHALL advance once per second while the active tool call remains in progress

### Requirement: The current-activity bar is absent when no tool call is in progress or the session is not running

The current-activity bar SHALL NOT render when the hosting session is not running, regardless of any in-progress tool state carried over from earlier. The bar SHALL ALSO NOT render while the session is alive but no tool call is in the `pending` or `running` state (for example, while the agent is streaming text or thinking). When the session ends, the bar SHALL be removed.

#### Scenario: Bar does not render in a non-running session
- **WHEN** the session is not running (ended, completed, failed, cancelled, or inactive)
- **THEN** the current-activity bar SHALL NOT render

#### Scenario: Bar does not render while no tool call is in progress
- **WHEN** the session is running but no tool call has state `pending` or `running`
- **THEN** the current-activity bar SHALL NOT render

#### Scenario: Bar is removed when the session ends mid-tool
- **WHEN** a running session that is displaying the current-activity bar transitions to not running
- **THEN** the current-activity bar SHALL be removed

### Requirement: Activating the current-activity bar scrolls the corresponding tool row into view

Activating the current-activity bar (for example, a pointer click or keyboard activation) SHALL scroll the tool row that corresponds to the currently-executing tool call into view. The corresponding tool row SHALL be located by its stable tool-call identity anchor (`data-tool-call-id`) carried on the row.

#### Scenario: Clicking the bar scrolls to the active tool row
- **WHEN** a user activates the current-activity bar while a tool call is in progress
- **THEN** the tool row whose tool-call identity matches the bar's active tool call SHALL be scrolled into view

#### Scenario: Activation targets the stable tool-call identity
- **WHEN** the active tool call transitions from `running` to `completed` and the bar re-targets a new active call
- **THEN** subsequent activation of the bar SHALL scroll to the tool row matching the newly active tool-call identity, not the previously active one
