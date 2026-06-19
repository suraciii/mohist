## ADDED Requirements

### Requirement: Issue Detail opens Activity event timeline in an on-demand dialog

Issue Detail SHALL provide an `Activity` entry control in the title/header area that opens the full event timeline in a Dialog on demand. The Dialog SHALL follow the project's existing dialog conventions (the same pattern as `WorkflowYamlDialog` / `ReviewReportModal`). The Issue Detail main content column SHALL have zero Activity footprint by default: no event list, no event count, and no "recent failure" preview SHALL render in the main content area before the dialog is opened. The blocked/failed reason SHALL remain owned by `RuntimeDecisionSurface` on the first screen and SHALL NOT be duplicated by an Activity preview.

#### Scenario: Activity entry opens the timeline dialog

- **WHEN** a user activates the `Activity` entry control on Issue Detail
- **THEN** the event timeline SHALL open inside a Dialog
- **AND** the Dialog SHALL remain in the Issue Detail context without navigating to a separate route

#### Scenario: Main content area has no Activity footprint by default

- **WHEN** a user opens Issue Detail for an issue
- **THEN** the main content column SHALL NOT render an Activity event list, an event count, or a recent-failure preview
- **AND** the Activity timeline SHALL be reachable only through the `Activity` entry control

#### Scenario: Blocked reason is not duplicated by an Activity preview

- **WHEN** a user opens Issue Detail for a blocked or failed issue
- **THEN** the first-screen blocked/failed reason SHALL be presented by `RuntimeDecisionSurface`
- **AND** no Activity preview or recent-failure hint SHALL render in the main content area

### Requirement: Activity event history is lazy-loaded when the dialog opens

The Web client SHALL NOT request issue events during Issue Detail initial load. The Web client SHALL request `GET /api/projects/{projectRef}/issues/{number}/events` only when the Activity dialog is opened, and SHALL seed the timeline with the returned merged chronological feed. If the dialog is never opened, no events request SHALL be made for that Issue Detail view.

#### Scenario: Page initial load does not fetch events

- **WHEN** a user opens Issue Detail for an issue and does not open the Activity dialog
- **THEN** the Web client SHALL NOT request `GET /api/projects/{projectRef}/issues/{number}/events`
- **AND** no event data SHALL be fetched as part of Issue Detail initial load

#### Scenario: Opening the dialog fetches the event history

- **WHEN** a user opens the Activity dialog for an issue
- **THEN** the Web client SHALL request `GET /api/projects/{projectRef}/issues/{number}/events`
- **AND** the returned events SHALL be rendered in the Activity timeline

#### Scenario: Empty event history shows an empty state

- **WHEN** the Activity dialog is opened for an issue whose events endpoint returns an empty array
- **THEN** the Activity timeline SHALL show its empty state
- **AND** the Web client SHALL NOT treat the empty response as an error

### Requirement: Activity entry shows no event count before first open

The `Activity` entry control SHALL NOT display a precise event count before the dialog has been opened at least once, so that counts do not require preloading the full event history on Issue Detail initial load.

#### Scenario: Entry shows no count before first open

- **WHEN** Issue Detail renders the `Activity` entry control before the dialog has been opened for the current issue
- **THEN** the entry SHALL NOT display a precise event count
- **AND** no events request SHALL be issued solely to populate a count

### Requirement: Events arriving while the Activity dialog is closed are not lost

Events that arrive while the Activity dialog is closed SHALL NOT be lost. When the dialog is reopened, the Web client SHALL re-request the merged event history so the full persisted timeline is presented, including any events that occurred while the dialog was closed.

#### Scenario: Reopening the dialog shows events that arrived while closed

- **WHEN** a user closes the Activity dialog
- **AND** one or more events occur for the issue while the dialog is closed
- **AND** the user reopens the Activity dialog
- **THEN** the reopened dialog SHALL display the full persisted event history including the events that arrived while it was closed
- **AND** no event SHALL be missing relative to the persisted history

### Requirement: Activity dialog reuses timeline filter, sort, and detail-expand capabilities

The Activity dialog SHALL reuse the existing event timeline capabilities: category filters with counts, newest-first default with chronological toggle, and inline event detail expansion. Reusing these capabilities SHALL NOT change their underlying behavior.

#### Scenario: Category filters remain available inside the dialog

- **WHEN** the Activity dialog is open
- **THEN** category filter chips with counts SHALL remain available
- **AND** selecting one or more chips SHALL narrow the visible feed to the selected categories

#### Scenario: Sort toggle remains available inside the dialog

- **WHEN** the Activity dialog is open
- **THEN** the newest-first default ordering SHALL remain in effect
- **AND** the chronological toggle SHALL remain available to flip ordering

#### Scenario: Event detail expansion remains available inside the dialog

- **WHEN** a user expands an event row inside the Activity dialog
- **THEN** the inline detail block SHALL be available with the same content as before
- **AND** no filter, sort, or expand capability SHALL be removed relative to the prior panel experience

### Requirement: Activity dialog renders as a near-fullscreen sheet on mobile

On mobile widths the Activity dialog SHALL render as a near-fullscreen sheet rather than a small centered box. The `Activity` entry control SHALL remain visible and operable on mobile, and the dialog SHALL preserve the full Activity feature set (open, filter, sort, expand detail) on mobile without functional loss.

#### Scenario: Mobile dialog uses a near-fullscreen sheet

- **WHEN** a user opens the Activity dialog at a mobile viewport width
- **THEN** the dialog SHALL render as a near-fullscreen sheet
- **AND** the dialog SHALL NOT render as a small centered box that clips the timeline

#### Scenario: Activity entry is reachable on mobile

- **WHEN** a user views Issue Detail on a mobile viewport
- **THEN** the `Activity` entry control SHALL be visible and meet the project's minimum hit-target baseline
- **AND** activating it SHALL open the Activity dialog

#### Scenario: Mobile preserves Activity capabilities

- **WHEN** a user interacts with the Activity dialog on a mobile viewport
- **THEN** filtering, sorting, and detail expansion SHALL remain available
- **AND** no Activity capability available on desktop SHALL be missing on mobile
