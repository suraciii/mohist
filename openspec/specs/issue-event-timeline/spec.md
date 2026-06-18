# OpenSpec Capability: issue-event-timeline

### Requirement: Timeline loads merged event history on issue open

The event timeline SHALL load a single time-ordered merge of issue events and workflow events for the current issue from the existing `GET /api/projects/{projectRef}/issues/{number}/events` endpoint when the issue detail page is opened. The timeline SHALL render historical events so a freshly opened issue immediately shows its past without requiring a live run to be in progress.

#### Scenario: Opening an issue with prior history shows past events

- **WHEN** a user opens the issue detail page for an issue that has prior stage transitions, comments, and label changes
- **THEN** the Activity timeline SHALL display those prior events in chronological order
- **AND** the timeline SHALL NOT require a live workflow run to be active to show history

#### Scenario: Issue with no events shows empty state

- **WHEN** a user opens the issue detail page for an issue that has no issue events and no workflow events
- **THEN** the Activity timeline SHALL display a clear empty state indicating no activity yet
- **AND** the timeline SHALL NOT render a raw error or a blank panel

### Requirement: Timeline updates in real time while workflow is active

While the issue detail page is open and the issue's workflow is active, the event timeline SHALL append newly arrived events in real time over the existing SignalR bus without a full page reload. New events SHALL appear with a top-enter animation so the user can perceive live progress.

#### Scenario: New event arrives while viewing an active issue

- **WHEN** a user is viewing the issue detail page for an issue with an active workflow run
- **AND** a new workflow or issue event arrives over SignalR for that issue
- **THEN** the event timeline SHALL append the new event without reloading the page
- **AND** the new event SHALL appear with a visible enter animation

#### Scenario: Timeline stops accumulating when issue is closed

- **WHEN** the issue's workflow is no longer active
- **THEN** the timeline SHALL continue to display all loaded events
- **AND** the Live indicator SHALL stop pulsing to reflect that no live updates are expected

### Requirement: Timeline entries are human-readable with timestamps

Each timeline entry SHALL display a human-readable description composed of a clear verb and subject (for example "Stage moved from Plan to Code", "Rebase conflict detected on 3 files", "Approval requested for Check", "Issue labeled bug", "Merge completed") and a timestamp. The timeline SHALL NOT display raw event type strings as the primary reading experience.

#### Scenario: Stage transition renders a readable description

- **WHEN** the timeline renders a stage transition event from the Plan stage to the Code stage
- **THEN** the entry SHALL display a description equivalent to "Stage moved from Plan to Code"
- **AND** the entry SHALL NOT display the raw event type string as the message

#### Scenario: Label change renders a readable description

- **WHEN** the timeline renders an issue labels-changed event where the labels are `bug` and `ux`
- **THEN** the entry SHALL display a description equivalent to "Issue labeled bug, ux"
- **AND** the entry SHALL display a timestamp showing when the change occurred

#### Scenario: Rebase conflict renders a readable description with detail

- **WHEN** the timeline renders a rebase conflict event affecting 3 files
- **THEN** the entry SHALL display a description equivalent to "Rebase conflict detected on 3 files"
- **AND** the conflicting file paths SHALL be available as expandable inline detail

### Requirement: Timeline applies category color coding

The timeline SHALL classify each event into one of six categories and render a colored dot and category tag per row using the existing pill/dot palette: workflow/lifecycle (blue), approval (amber), integration (purple), success (green), failures (red), and metadata (gray).

#### Scenario: Workflow lifecycle events use blue

- **WHEN** the timeline renders a run-started, stage-transition, or task start/complete event
- **THEN** the row SHALL use the blue category color for its dot and tag

#### Scenario: Approval events use amber

- **WHEN** the timeline renders an approval-requested or approval-resolved event
- **THEN** the row SHALL use the amber category color for its dot and tag

#### Scenario: Integration events use purple

- **WHEN** the timeline renders a rebase, merge, check, or integration step event that is not a failure
- **THEN** the row SHALL use the purple category color for its dot and tag

#### Scenario: Success events use green

- **WHEN** the timeline renders a stage-completed or merge-completed event
- **THEN** the row SHALL use the green category color for its dot and tag

#### Scenario: Failure events use red

- **WHEN** the timeline renders a stage-failed, run-failed, merge-failed, or rebase-conflict event
- **THEN** the row SHALL use the red category color for its dot and tag

#### Scenario: Metadata events use gray

- **WHEN** the timeline renders a labels-changed, priority-changed, prerequisite-added, prerequisite-removed, or comment event
- **THEN** the row SHALL use the gray category color for its dot and tag

### Requirement: Timeline visually emphasizes failures and attention-required events

The timeline SHALL visually distinguish failures and attention-required events (stage failed, run failed, approval requested, rebase conflict, base drift needs-attention) from normal progress events using a tinted row background and a haloed dot so the eye finds decisions and failures fast.

#### Scenario: Failed stage is visually emphasized

- **WHEN** the timeline renders a stage-failed event
- **THEN** the row SHALL have a tinted background distinct from normal progress rows
- **AND** the dot SHALL be rendered with a halo effect

#### Scenario: Approval requested is visually emphasized

- **WHEN** the timeline renders an approval-requested event
- **THEN** the row SHALL have a tinted background distinct from normal progress rows
- **AND** the dot SHALL be rendered with a halo effect

#### Scenario: Normal progress is not emphasized

- **WHEN** the timeline renders a task-started or run-resumed event
- **THEN** the row SHALL NOT have the attention tinted background or haloed dot

### Requirement: Timeline failures expand inline detail

Failure events SHALL expose an inline mono detail block that the user can expand to see the surrounding context, such as conflicting file paths, error messages, or failing step output.

#### Scenario: Rebase conflict expands file paths

- **WHEN** a rebase-conflict event row is expanded
- **THEN** the inline detail block SHALL display the conflicting file paths in a monospaced font

#### Scenario: Stage failure expands error detail

- **WHEN** a stage-failed event row is expanded
- **THEN** the inline detail block SHALL display the failure reason or error message in a monospaced font

### Requirement: Timeline merges issue and workflow events with source tags

The timeline SHALL merge issue-level events and workflow-level events into a single chronological feed, and each row SHALL be tagged with its source (`ISSUE` or `WORKFLOW`) to disambiguate which stream the event came from. The timeline SHALL NOT render two separate lists.

#### Scenario: Issue and workflow events appear interleaved

- **WHEN** the timeline renders events for an issue where a comment was added at 10:00, a stage transition occurred at 10:01, and a label changed at 10:02
- **THEN** all three events SHALL appear in a single time-ordered list
- **AND** the comment and label rows SHALL be tagged `ISSUE`
- **AND** the stage transition row SHALL be tagged `WORKFLOW`

#### Scenario: Events are ordered by timestamp across sources

- **WHEN** the timeline contains issue events and workflow events with interleaved timestamps
- **THEN** the merged feed SHALL order all events by timestamp regardless of source

### Requirement: Timeline provides category filters with counts

The timeline SHALL provide category filter chips with live counts, mirroring the Logs page level-filter pattern. Selecting one or more category chips SHALL narrow the visible feed to only events in the selected categories.

#### Scenario: Filter to failures and approval

- **WHEN** a user selects only the Failures and Approval category chips during a noisy active run
- **THEN** the timeline SHALL display only failure and approval events
- **AND** all other events SHALL be hidden

#### Scenario: Category chips show counts

- **WHEN** the timeline renders the category filter chips
- **THEN** each chip SHALL display the count of events in that category currently loaded in the feed

#### Scenario: Clearing filters shows all events

- **WHEN** a user deselects all active category filters
- **THEN** the timeline SHALL display all loaded events regardless of category

### Requirement: Timeline defaults to newest-first with chronological toggle

The timeline SHALL default to newest-first ordering so live events appear without scrolling. A toggle SHALL allow the user to flip to chronological (oldest-first) ordering.

#### Scenario: Default ordering is newest-first

- **WHEN** the timeline renders with default settings
- **THEN** the most recent event SHALL appear at the top of the feed

#### Scenario: User toggles to chronological order

- **WHEN** a user activates the order toggle
- **THEN** the timeline SHALL reorder to chronological (oldest-first) order
- **AND** activating the toggle again SHALL restore newest-first order

### Requirement: Timeline shows day separators when feed spans days

When the loaded feed contains events from more than one calendar day, the timeline SHALL render sticky day separators between events on different days so the user can orient chronologically across days.

#### Scenario: Events spanning two days get a separator

- **WHEN** the timeline renders events from June 16 and June 17
- **THEN** a day separator SHALL appear between the June 16 and June 17 events
- **AND** the separator SHALL remain visible while scrolling (sticky)

#### Scenario: Same-day events get no separator

- **WHEN** all loaded events occurred on the same calendar day
- **THEN** no day separator SHALL be rendered between events

### Requirement: Timeline shows a Live indicator while workflow is active

The timeline SHALL display a pulsing Live badge while the issue's workflow is active, mirroring the existing `animate-pulse` "Running" pill animation convention. The Live indicator SHALL stop pulsing when the workflow is no longer active.

#### Scenario: Active workflow shows pulsing Live badge

- **WHEN** the issue's workflow status is running
- **THEN** the timeline SHALL display a Live badge with a pulse animation

#### Scenario: Inactive workflow does not show pulsing Live badge

- **WHEN** the issue's workflow is completed, failed, or not started
- **THEN** the Live badge SHALL NOT pulse
- **AND** the Live badge SHALL be visually de-emphasized to indicate no live updates are expected

### Requirement: Timeline is a read-only observation surface

The event timeline SHALL be a read-only observation surface. It SHALL NOT provide controls that mutate issue or workflow state. The timeline SHALL NOT replace the stage/task projection view or the runtime decision surface.

#### Scenario: Timeline does not offer state mutations

- **WHEN** a user views the event timeline
- **THEN** no approve, reject, retry, rerun, start, close, reopen, or comment controls SHALL be present in the timeline panel

#### Scenario: Timeline coexists with stage/task view

- **WHEN** the issue detail page renders the Activity timeline
- **THEN** the stage/task projection view SHALL remain visible and functional above or alongside the timeline
- **AND** the timeline SHALL NOT displace the stage/task view
