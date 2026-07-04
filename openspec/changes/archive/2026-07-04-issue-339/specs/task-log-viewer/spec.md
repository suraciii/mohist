### Requirement: Agent-task milestone rows are merged into the ops log timeline and sorted by time

The task log panel SHALL render an agent task's milestone rows inside the same timeline as Phase 1 ops log lines, interleaved and sorted by time (each milestone carrying a timestamp comparable to an ops line's timestamp). Milestones SHALL NOT be rendered in a separate, parallel region; they SHALL appear inline so the user can read the session's key boundary events in chronological context with the command output. When an agent task has no ops log lines at all, the milestone rows SHALL still render as the timeline's content.

#### Scenario: Milestones interleave with ops lines by time

- **WHEN** an agent task has ops log lines at timestamps `08:00:01` and `08:05:00` and a session-end milestone at `08:04:00`
- **THEN** the rendered timeline SHALL order them `08:00:01` (ops), `08:04:00` (milestone), `08:05:00` (ops)

#### Scenario: Milestones render even when the agent task has no ops lines

- **WHEN** an agent task produced no ops command output (its task-log store is empty) but its session summary carries a bound model and an end status
- **THEN** the panel SHALL render the milestone rows as the timeline's content
- **AND** SHALL NOT show the Phase 1 "no execution log captured" empty state for that task

### Requirement: Milestone rows have a distinct visual marker separating session events from command output

Each milestone row SHALL carry a visual treatment (e.g. a distinct marker, icon, and/or color) that makes a session event unmistakable from a command-output line at a glance. The ops line styling (timestamp + `[source]` + text) SHALL remain as in Phase 1/3a; only the milestone row variant SHALL be visually distinct.

#### Scenario: A milestone row is visually distinguishable from an ops line

- **WHEN** a timeline contains both an ops log line and a milestone row
- **THEN** the milestone row SHALL expose a distinguishing marker (e.g. a dedicated icon and/or a different color/label) that the ops line does not carry
- **AND** a user SHALL be able to tell which row is a session event without reading its text

### Requirement: Milestone rows appear only for agent tasks

The milestone row variant SHALL be rendered exclusively for tasks identified as agent tasks (per the `agent-task-milestone-stitching` capability's identification rule). Pure ops tasks SHALL render only Phase 1/3a ops lines and SHALL never render a milestone row, even when session-like data happens to be present.

#### Scenario: An ops task renders no milestone rows

- **WHEN** a pure ops task (e.g. `origin.uses === 'mohist/rebase'`, no `sessionName`) is expanded
- **THEN** its task log panel SHALL render only ops log lines
- **AND** SHALL render no milestone rows

### Requirement: Phase 3a keyword search applies to milestone rows

The Phase 3a keyword search SHALL treat milestone rows as first-class timeline entries: a milestone row SHALL remain visible when its rendered text (e.g. the resolved model name, the status label, or the failure reason) contains the search term as a case-insensitive substring, and SHALL be hidden otherwise. Search SHALL remain a pure client-side computation with no server request.

#### Scenario: A search term matching a milestone's text keeps it visible

- **WHEN** the user types a term that appears in a milestone row's rendered text (for example the resolved model name) and a sibling ops line does not contain it
- **THEN** the milestone row SHALL remain visible
- **AND** the non-matching ops line SHALL be hidden

#### Scenario: A search term matching no milestone hides it

- **WHEN** the user types a term that appears in no milestone row's text
- **THEN** that milestone row SHALL be hidden, exactly like a non-matching ops line

### Requirement: Source-chip filtering remains an ops-line concern and does not gate milestone rows

The Phase 3a source chips are derived from ops line `source` values and SHALL remain an ops-only filter dimension. Milestone rows SHALL NOT contribute to the chip set, SHALL NOT carry an ops `source`, and SHALL NOT be hidden when an ops source chip is toggled off. The chip set SHALL continue to be derived solely from the ops lines present in the loaded log.

#### Scenario: Milestones do not add source chips

- **WHEN** an agent task's timeline contains milestone rows and ops lines
- **THEN** the source-chip set SHALL be derived only from the ops lines' `source` values
- **AND** no chip SHALL be derived from a milestone row

#### Scenario: Toggling an ops source chip off does not hide milestone rows

- **WHEN** the user toggles off an ops source chip
- **THEN** only ops lines of that source SHALL be hidden
- **AND** every milestone row SHALL remain visible

### Requirement: Terminal-state milestone visibility is the acceptance floor

Once the agent task's session has ended, the bound-model milestone and the session-end milestone (carrying the status, and on failure the failure reason) SHALL be visible from the persisted session summary alone. This terminal-state visibility SHALL be the acceptance floor and SHALL NOT depend on the Phase 2 real-time channel having delivered any event. The panel SHALL render these terminal milestones for a finished agent task even when opened after the session has closed.

#### Scenario: A finished agent task shows its outcome milestones on open

- **WHEN** a user expands a finished agent task whose session summary is persisted
- **THEN** the bound-model milestone and the session-end milestone (with status, and on failure the reason) SHALL be visible
- **AND** this SHALL hold without any real-time session event having been observed by the panel

### Requirement: The milestone row variant is accessible

The new milestone row variant SHALL pass the same structural axe rules applied to the rest of the task log panel, and any interactive element it introduces SHALL be keyboard reachable and shall expose an appropriate accessible name/role. The milestone's distinguishing marker SHALL convey its meaning non-redundantly (e.g. via accessible name or `aria-label`), not by color alone.

#### Scenario: Structural axe rules pass with milestone rows present

- **WHEN** the panel renders a timeline containing both ops lines and milestone rows under the a11y test harness
- **THEN** it SHALL pass the structural axe rule set used across the app for the panel

#### Scenario: The milestone marker is not color-only

- **WHEN** a milestone row is inspected for accessibility
- **THEN** its meaning as a session event SHALL be conveyed by text/label/icon semantics, not by color alone

### Requirement: Phase 1/2/3a acquisition, data model, and display do not regress

The `TaskLogLine` / `TaskLogPage` wire types, the REST snapshot acquisition, the SignalR delta channel, the live-append (`mergeTaskLogDelta`) semantics, the truncation indicator, the terminal-state cache invalidation, and the Phase 3a search/source/download behavior SHALL continue to behave as before this change. Milestones are an additive, transient view-layer projection; they SHALL NOT alter the ops line data path.

#### Scenario: Ops line rendering and live append are unchanged

- **WHEN** a running task streams new ops deltas with no filter active
- **THEN** the ops lines SHALL render in seq order and live-append exactly as before this change
- **AND** the truncation indicator SHALL still appear when the retained tail is truncated

#### Scenario: Search, source chips, and download behave as in Phase 3a

- **WHEN** the user searches, toggles source chips, or downloads on a timeline that mixes ops lines and milestone rows
- **THEN** the Phase 3a semantics (case-insensitive substring search, ops-source chip filtering, filtered-view export) SHALL be preserved
- **AND** the download export SHALL continue to reflect the currently filtered view
