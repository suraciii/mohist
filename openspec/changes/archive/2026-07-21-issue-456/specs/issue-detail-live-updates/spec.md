### Requirement: The issue detail page applies workflow transitions from the live event stream without a reload or wholesale re-render

While the issue detail page is open, the page SHALL subscribe to the live event stream that the Activity view already consumes and SHALL apply workflow transitions — stage transitions, task starts, task completions, approval requests, and blocked states — incrementally as those transitions occur. Each transition SHALL appear without navigating or reloading the page, and the page SHALL NOT perform a wholesale re-render of its content tree on each event; only the region affected by the transition SHALL update.

#### Scenario: A task completion appears without reload or full re-render

- **WHEN** a task-completion event for the viewed issue arrives over the live event stream while the issue detail page is open
- **THEN** the task's completed state SHALL appear in the page without a navigation or reload
- **AND** the page SHALL NOT re-render its full content tree as a side effect of the event

#### Scenario: A stage transition appears without reload or full re-render

- **WHEN** a stage-transition event for the viewed issue arrives over the live event stream while the page is open
- **THEN** the new stage SHALL appear in the page without a navigation or reload
- **AND** the page SHALL NOT re-render its full content tree as a side effect of the event

#### Scenario: An approval request or blocked state appears without reload

- **WHEN** the viewed issue enters an approval-waiting or blocked state while the page is open
- **THEN** the approval-waiting or blocked state SHALL appear in the page without a navigation or reload

### Requirement: The reader's scroll position is preserved across every live update

When a live event applies an update to the open page, the page SHALL preserve the reader's current scroll position. An update SHALL NOT move the content under the reader; specifically, the page SHALL NOT scroll to the top, to an anchor, or to any other element as a side effect of applying a live update.

#### Scenario: Scroll position does not move when a transition arrives

- **WHEN** a workflow transition event for the viewed issue arrives while the reader has scrolled to a position within the open page
- **THEN** the reader's scroll position SHALL remain at the same reading point after the update is applied
- **AND** the page SHALL NOT scroll to the top, to an anchor, or to any other element as a side effect of the update

### Requirement: Expanded and collapsed section state survives every live update

The expanded or collapsed state of every section the reader has toggled SHALL be preserved across each live update. An update SHALL NOT reset, re-mount, or flap any section's expanded/collapsed state; a section the reader expanded SHALL remain expanded, and a section the reader collapsed SHALL remain collapsed.

#### Scenario: A reader's toggled section keeps its state across a live update

- **WHEN** the reader expands or collapses a section on the open page and a workflow transition event then arrives
- **THEN** that section SHALL remain in the reader's chosen expanded or collapsed state after the update is applied
- **AND** no previously expanded section SHALL collapse itself and no previously collapsed section SHALL expand itself as a side effect of the update

### Requirement: Workflow timeline polling fires only as a reconnect fallback

The workflow timeline query SHALL NOT poll on a steady-state recurring timer while the events connection is healthy. The query SHALL refetch as a fallback only after the events connection drops and subsequently reconnects, so that any transition missed while the connection was down is recovered. After a catch-up refetch, the query SHALL NOT resume a recurring timer.

#### Scenario: No steady-state polling while the events connection is healthy

- **WHEN** the events connection is in the connected state and the issue detail page is open
- **THEN** the workflow timeline query SHALL NOT refetch on a recurring timer

#### Scenario: A reconnect triggers a catch-up refetch

- **WHEN** the events connection drops and later reconnects while the issue detail page is open
- **THEN** the workflow timeline query SHALL refetch to catch up on anything missed during the disconnection
- **AND** the query SHALL NOT resume a recurring timer after that catch-up refetch

### Requirement: Live updates and reading stability behave identically on a phone-width viewport

Every live-update and reading-stability requirement in this capability SHALL hold on a phone-width viewport exactly as it holds on a desktop viewport, including incremental application of transitions, scroll-position preservation, section-state survival, and reconnect-fallback polling.

#### Scenario: A live update on a phone-width viewport behaves like the desktop viewport

- **WHEN** the issue detail page renders at a phone-width viewport and a workflow transition event for the viewed issue arrives
- **THEN** the transition SHALL appear without a reload or full re-render
- **AND** the reader's scroll position SHALL be preserved
- **AND** any toggled section state SHALL survive the update
