### Requirement: Canonical workflow sessions list

The issue workflow view SHALL render the list of sessions for a workflow run through a single canonical workflow sessions panel. That panel SHALL be the sole list component backing the session list surfaced to users, and no parallel, unused, or duplicate session-list component SHALL remain wired into the application. Every rendered session entry SHALL present meaningful session information — at minimum the session name and its status — and the panel SHALL NOT contain a placeholder or "dead-stub" region that displays no session data.

#### Scenario: Single session list implementation

- **WHEN** the issue workflow view renders the session list for a workflow run
- **THEN** the list SHALL be rendered by the single canonical workflow sessions panel
- **AND** no parallel session-list component SHALL render the same workflow-run sessions

#### Scenario: Session entries show meaningful information

- **WHEN** a session entry is rendered in the workflow sessions panel
- **THEN** the entry SHALL display meaningful session information including at least the session name and status
- **AND** the panel SHALL NOT contain a placeholder region that displays no session data

### Requirement: Filtering sessions by status and stage

The workflow sessions panel SHALL provide controls to filter the displayed sessions by status and by workflow stage. The status filter SHALL cover the statuses the panel surfaces, including at least `running`, `completed`, and `failed`. The stage filter SHALL cover the executable pipeline stages: `plan`, `build`, `check`, and `integrate`. Status and stage filters SHALL be combinable so that both apply at once, and applying one or more filters SHALL reduce the visible rows to only sessions matching every active filter. Clearing a filter SHALL restore the sessions excluded by that filter.

#### Scenario: Filter by status

- **WHEN** a user selects the status filter value `failed`
- **THEN** only sessions whose status is `failed` SHALL be visible
- **AND** sessions with other statuses SHALL be hidden

#### Scenario: Filter by stage

- **WHEN** a user selects the stage filter value `build`
- **THEN** only sessions whose stage is `build` SHALL be visible
- **AND** sessions with other stages SHALL be hidden

#### Scenario: Combined status and stage filters

- **WHEN** a user selects status `completed` and stage `check` at the same time
- **THEN** only sessions that are both `completed` and in the `check` stage SHALL be visible
- **AND** sessions matching only one of the filters SHALL be hidden

#### Scenario: Stage filter covers executable pipeline stages

- **WHEN** the stage filter control renders its selectable options
- **THEN** the options SHALL include `plan`, `build`, `check`, and `integrate`

#### Scenario: Clearing a filter restores excluded sessions

- **WHEN** a user clears an active status or stage filter
- **THEN** the panel SHALL show every session regardless of that filter dimension
- **AND** any other still-active filter SHALL continue to apply

### Requirement: Sorting sessions in the workflow list

The workflow sessions panel SHALL provide a sort control with at least the following sortable dimensions: `createdAt`, tokens, and duration. The default sort SHALL be `createdAt` ascending (earliest first) so that existing behavior is preserved when the user has not chosen a sort. Selecting a different dimension SHALL reorder the visible rows by that dimension, and the sort SHALL apply to the rows that remain after filtering. Duration SHALL be computed from the session's start time to its completion time, or to the current time for sessions that are not yet complete.

#### Scenario: Default sort is createdAt ascending

- **WHEN** the panel renders with no explicit sort selection
- **THEN** sessions SHALL be ordered by `createdAt` ascending (earliest first)

#### Scenario: Sort by tokens

- **WHEN** a user selects the tokens sort dimension
- **THEN** sessions SHALL be ordered by their token usage

#### Scenario: Sort by duration

- **WHEN** a user selects the duration sort dimension
- **THEN** sessions SHALL be ordered by their computed duration
- **AND** a not-yet-complete session's duration SHALL be measured up to the current time

#### Scenario: Sort applies after filtering

- **WHEN** a user applies a filter and then selects a sort dimension
- **THEN** only the sessions remaining after the filter SHALL be reordered
- **AND** filtered-out sessions SHALL NOT appear in the sorted result

### Requirement: Responsive session row layout in the workflow list

The workflow sessions panel SHALL render each session row so that session information stays readable on narrow container widths. Row content SHALL wrap across multiple lines rather than placing every metric into a single non-wrapping line, and the panel SHALL NOT produce horizontal overflow on narrow viewports. The session name and status SHALL remain visible and legible at narrow widths, and metric chips SHALL wrap gracefully instead of being truncated out of view.

#### Scenario: Row wraps on a narrow container

- **WHEN** a session row renders in a narrow container width
- **THEN** the row content SHALL wrap across multiple lines rather than overflowing horizontally
- **AND** the session name and status SHALL remain visible

#### Scenario: No horizontal overflow in the panel

- **WHEN** the workflow sessions panel renders on a narrow viewport
- **THEN** the panel SHALL NOT produce horizontal overflow
- **AND** metric chips SHALL wrap rather than being clipped or truncated out of view

### Requirement: Adjacent session navigation on the session page

The session page SHALL provide "previous session" and "next session" navigation that moves the user along the ordered set of sibling sessions belonging to the same issue (the same workflow run's sessions). The navigation SHALL follow the canonical session ordering (by `createdAt` ascending) so that prev/next move to the chronologically adjacent sibling. When the current session is the first sibling, the "previous session" control SHALL be disabled or hidden; when it is the last sibling, the "next session" control SHALL be disabled or hidden. Navigation SHALL stay within the current issue's session set.

#### Scenario: Next session navigation

- **WHEN** a user activates "next session" while viewing session K that is not the last sibling
- **THEN** the session page SHALL navigate to the chronologically next sibling session

#### Scenario: Previous session navigation

- **WHEN** a user activates "previous session" while viewing session K that is not the first sibling
- **THEN** the session page SHALL navigate to the chronologically previous sibling session

#### Scenario: Boundary disables navigation

- **WHEN** the current session is the first sibling session
- **THEN** the "previous session" control SHALL be disabled or hidden
- **WHEN** the current session is the last sibling session
- **THEN** the "next session" control SHALL be disabled or hidden

#### Scenario: Navigation stays within the issue's sessions

- **WHEN** a user navigates using previous or next session
- **THEN** the destination SHALL be a sibling session within the same issue
- **AND** navigation SHALL NOT leave the issue's session set

### Requirement: Sibling sessions sidebar on the session page

The session page SHALL render a sidebar that lists the sessions belonging to the same issue (the same workflow run's sessions), enabling quick switching between sibling sessions. Each sibling session SHALL appear as a navigable entry, and the sidebar SHALL visually indicate which session is currently being viewed. The set of sessions shown in the sidebar SHALL match the set shown by the workflow sessions panel for the same workflow run.

#### Scenario: Sidebar lists sibling sessions

- **WHEN** the session page renders for a session in a workflow run with multiple sessions
- **THEN** the sidebar SHALL list the sibling sessions as navigable entries
- **AND** activating an entry SHALL navigate to that session

#### Scenario: Current session is indicated

- **WHEN** the session page renders with the current session in view
- **THEN** the sidebar SHALL visually indicate the currently viewed session

#### Scenario: Sidebar matches the workflow sessions panel set

- **WHEN** the sidebar renders its session entries for a given workflow run
- **THEN** the entries SHALL match the set of sessions the workflow sessions panel renders for the same workflow run
- **AND** the sidebar SHALL NOT omit or invent sessions relative to that panel

### Requirement: Issue-level usage aggregation

The issue / workflow-run view SHALL surface an issue-level usage total that aggregates usage across the sessions belonging to the issue: at minimum the total token count and the total cost summed across the issue's sessions. The aggregate SHALL be derived from the same usage data the individual session rows report, so the total is consistent with the sum of its parts. The aggregate total SHALL be visible on the issue page alongside the workflow sessions panel, so a user can see at a glance what the issue cost across all of its sessions.

#### Scenario: Issue page shows total tokens and total cost

- **WHEN** the issue page renders for an issue that has one or more sessions with usage data
- **THEN** the page SHALL display the total token count aggregated across the issue's sessions
- **AND** the page SHALL display the total cost aggregated across the issue's sessions

#### Scenario: Aggregate is consistent with session rows

- **WHEN** the issue-level usage total is rendered alongside the workflow sessions panel
- **THEN** the aggregate total SHALL be consistent with the sum of the individual session rows' usage
- **AND** the aggregate SHALL NOT diverge from the per-session data shown in the same view

#### Scenario: No usage data does not render a misleading total

- **WHEN** the issue has no sessions with usage data
- **THEN** the issue page SHALL NOT render a misleading non-zero total
- **AND** the page MAY omit the aggregate region entirely

### Requirement: Realtime usage feed carries the complete usage payload

The realtime (SSE) usage feed that updates the workflow sessions panel SHALL deliver the complete usage payload on a `usage.updated` event, including `contextUsagePercent` and `healthStatus`, so the panel reflects live context-health updates without a full refetch. The `useWorkflowRunSessions` SSE handler SHALL apply every usage field the session usage read model carries, achieving parity with the `useCoderSessions` handler, and SHALL NOT silently drop `contextUsagePercent` or `healthStatus` from the update.

#### Scenario: SSE usage.updated carries context-usage percent and health status

- **WHEN** a `usage.updated` SSE event arrives for a workflow-run session
- **THEN** the workflow sessions panel SHALL update the session's `contextUsagePercent` from the event
- **AND** the panel SHALL update the session's `healthStatus` from the event
- **AND** the panel SHALL reflect the live context health without requiring a full refetch

#### Scenario: SSE handler parity with useCoderSessions

- **WHEN** the `useWorkflowRunSessions` SSE handler processes a `usage.updated` event
- **THEN** it SHALL apply the same set of usage fields that the `useCoderSessions` handler applies
- **AND** it SHALL NOT omit `contextUsagePercent` or `healthStatus` from the applied update
